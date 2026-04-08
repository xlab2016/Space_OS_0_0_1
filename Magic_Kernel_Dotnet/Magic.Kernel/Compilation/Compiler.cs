using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Magic.Kernel.Core;
using Magic.Kernel.Processor;
using System.Linq;

namespace Magic.Kernel.Compilation
{
    public class Compiler
    {
        private readonly Linker _linker = new();

        public async Task<List<CompilationResult>> CompileDirectoryAsync(string directoryPath)
        {
            var result = new List<CompilationResult>();
            var sourceFiles = Directory.GetFiles(directoryPath, "*.agi", SearchOption.AllDirectories);
            foreach (var file in sourceFiles)
            {
                var fileResult = await CompileFileAsync(file);
                result.Add(fileResult);
            }
            return result;
        }

        public async Task<CompilationResult> CompileFileAsync(string filePath)
        {
            try
            {
                var fullPath = Path.GetFullPath(filePath);
                var stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var unit = await _linker.CompileFileInternalAsync(fullPath, stack);

                return new CompilationResult
                {
                    Success = true,
                    Result = unit
                };
            }
            catch (CompilationException cex)
            {
                var sourceCode = await File.ReadAllTextAsync(filePath);
                var (line, column) = GetLineAndColumn(sourceCode, cex.Position);
                var posSuffix = cex.Position >= 0
                    ? $" (line {line}, column {column})"
                    : string.Empty;

                return new CompilationResult
                {
                    Success = false,
                    ErrorMessage = cex.Message + posSuffix
                };
            }
            catch (Exception ex)
            {
                return new CompilationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        public async Task<CompilationResult> CompileAsync(string sourceCode)
            => await CompileAsync(sourceCode, null);

        public async Task<CompilationResult> CompileAsync(string sourceCode, string? sourcePath)
        {
            try
            {
                var parser = new Parser();
                var semanticAnalyzer = new SemanticAnalyzer();
                var executableUnit = new ExecutableUnit();

                var programStructure = parser.ParseProgram(sourceCode);

                executableUnit.Version = programStructure.Version;
                executableUnit.Name = programStructure.ProgramName;
                executableUnit.Module = programStructure.Module;
                executableUnit.System = programStructure.System;

                string? resolvedMain = null;
                if (!string.IsNullOrWhiteSpace(sourcePath))
                {
                    resolvedMain = System.IO.Path.GetFullPath(sourcePath);
                    _linker.StampSourcePathOnUnit(executableUnit, resolvedMain);
                    executableUnit.AttachSourcePath = resolvedMain;
                }

                TypeResolutionIndex? typeIndex = null;
                IReadOnlyDictionary<string, string>? extTypeQual = null;
                if (resolvedMain != null)
                {
                    var qualStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    typeIndex = await TypeResolutionIndex.BuildAsync(programStructure, _linker, resolvedMain, qualStack);
                    extTypeQual = typeIndex.ToExternalQualificationDictionary();
                }
                else
                {
                    typeIndex = new TypeResolutionIndex();
                    TypeResolutionIndex.AddLocalTypesFromProgram(programStructure, typeIndex);
                    extTypeQual = typeIndex.ToExternalQualificationDictionary();
                }

                await semanticAnalyzer.PrepareDeclaredTypeFieldsAsync(
                        programStructure,
                        resolvedMain,
                        resolvedMain != null ? _linker : null,
                        extTypeQual)
                    .ConfigureAwait(false);
                var analyzed = semanticAnalyzer.AnalyzeProgram(programStructure, parser, sourceCode, extTypeQual, typeIndex);
                executableUnit.EntryPoint = analyzed.EntryPoint;
                executableUnit.Procedures = analyzed.Procedures;
                executableUnit.Functions = analyzed.Functions;
                CollectTypeAndObjectDefinitions(executableUnit);

                // Функции, имена которых соответствуют соглашению для методов типов (Point_ctor_1, Point_Add, ...),
                // помечаем как methods для текстового AGIASM.
                foreach (var fn in analyzed.Functions.Keys)
                {
                    if (fn.Contains("_ctor_", StringComparison.OrdinalIgnoreCase) ||
                        fn.Contains("_", StringComparison.OrdinalIgnoreCase))
                    {
                        executableUnit.MethodNames.Add(fn);
                    }
                }

                if (resolvedMain != null && programStructure.UseDirectives.Count > 0)
                {
                    var useStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    await _linker.LinkUseDirectivesAsync(
                        importingFilePath: resolvedMain,
                        importingStructure: programStructure,
                        executableUnit: executableUnit,
                        semanticAnalyzer: semanticAnalyzer,
                        useStack: useStack);

                    // Линкер может подмешать прелюдии импортов (def/defobj в entrypoint),
                    // поэтому после link-pass пересобираем metadata типов (Objects — в интерпретаторе при defobj).
                    CollectTypeAndObjectDefinitions(executableUnit);
                }

                // Post-pass: вычищаем известные артефакты ловеринга, когда calculate
                // ошибочно трактуется как объект/тип и компилируется в push "calculate"; def.
                RemoveSpuriousCalculateDef(executableUnit);

                executableUnit.StampListingLineNumbers();

                return new CompilationResult
                {
                    Success = true,
                    Result = executableUnit
                };
            }
            catch (CompilationException cex)
            {
                // Добавляем позицию (строка/колонка), если она есть.
                var (line, column) = GetLineAndColumn(sourceCode, cex.Position);
                var posSuffix = cex.Position >= 0
                    ? $" (line {line}, column {column})"
                    : string.Empty;

                return new CompilationResult
                {
                    Success = false,
                    ErrorMessage = cex.Message + posSuffix
                };
            }
            catch (Exception ex)
            {
                return new CompilationResult
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        private static void RemoveSpuriousCalculateDef(ExecutableUnit unit)
        {
            static void Clean(ExecutionBlock? block)
            {
                if (block == null || block.Count < 2)
                    return;

                for (var i = 0; i + 1 < block.Count; i++)
                {
                    var c0 = block[i];
                    var c1 = block[i + 1];

                    if (c0.Opcode == Opcodes.Push &&
                        c0.Operand1 is PushOperand po &&
                        po.Kind == "StringLiteral" &&
                        string.Equals(po.Value as string, "calculate", StringComparison.OrdinalIgnoreCase) &&
                        c1.Opcode == Opcodes.Def)
                    {
                        block.RemoveAt(i + 1);
                        block.RemoveAt(i);
                        i -= 2;
                        if (i < -1)
                            i = -1;
                    }
                }
            }

            Clean(unit.EntryPoint);
            foreach (var p in unit.Procedures.Values)
                Clean(p.Body);
            foreach (var f in unit.Functions.Values)
                Clean(f.Body);
        }

        private static void CollectTypeAndObjectDefinitions(ExecutableUnit unit)
        {
            if (unit == null)
                return;

            unit.Types.Clear();
            unit.Objects.Clear();

            var seenTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unitNs = DefName.GetNamespace(unit);

            void ScanBlock(ExecutionBlock? block)
            {
                if (block == null || block.Count == 0)
                    return;

                for (var i = 0; i < block.Count; i++)
                {
                    var cmd = block[i];
                    if (cmd.Opcode != Opcodes.Def)
                        continue;

                    if (!TryReadDefArgs(block, i, out var args))
                        continue;
                    if (args.Count == 0 || args[0] is not string typeName || string.IsNullOrWhiteSpace(typeName))
                        continue;

                    // Exclude child defs and non-type defs: fields, columns, db/table/list etc.
                    if (IsChildOrNonTypeDef(args))
                        continue;

                    var baseNames = args.Skip(1).OfType<string>().ToList();
                    var isClass = baseNames.Count > 0 && !string.Equals(baseNames[0], "type", StringComparison.OrdinalIgnoreCase);
                    var defNs = string.IsNullOrWhiteSpace(cmd.TypeDefUnitNamespace) ? unitNs : cmd.TypeDefUnitNamespace.Trim();
                    var created = DefType.FromDefBytecode(typeName, defNs, isClass, isClass ? baseNames : null);
                    if (!seenTypeNames.Add(created.FullName))
                        continue;

                    unit.Types.Add(created);
                }
            }

            ScanBlock(unit.EntryPoint);
            foreach (var procedure in unit.Procedures.Values)
                ScanBlock(procedure.Body);
            foreach (var function in unit.Functions.Values)
                ScanBlock(function.Body);

            ExecutableUnitBytecodeMetadata.AppendTypeFieldsFromDefInstructions(unit);
        }

        private static bool IsChildOrNonTypeDef(IReadOnlyList<object?> args)
        {
            // Parent-based def (column/field and similar) always has non-string first arg.
            if (args.Count == 0 || args[0] is not string)
                return true;

            for (var i = 1; i < args.Count; i++)
            {
                if (args[i] is not string marker)
                    continue;

                if (string.Equals(marker, "column", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(marker, "field", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(marker, "table", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(marker, "database", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(marker, "list", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryReadDefArgs(ExecutionBlock block, int defIndex, out List<object?> args) =>
            ExecutableUnitBytecodeMetadata.TryReadDefArgs(block, defIndex, out args);

        private static object? TryReadPushValue(Command pushCommand) =>
            ExecutableUnitBytecodeMetadata.TryReadPushValue(pushCommand);

        /// <summary>Разрешает путь из <c>use</c> относительно импортирующего .agi (те же правила, что у линкера).</summary>
        public static string ResolveUseModuleFilePath(string importingFilePath, string modulePath)
            => Linker.ResolveUseModuleFilePath(importingFilePath, modulePath);

        private static (int Line, int Column) GetLineAndColumn(string source, int position)
        {
            if (position < 0 || position > source.Length)
                return (0, 0);

            var line = 1;
            var col = 1;
            for (var i = 0; i < position; i++)
            {
                if (source[i] == '\n')
                {
                    line++;
                    col = 1;
                }
                else
                {
                    col++;
                }
            }
            return (line, col);
        }
    }
}
