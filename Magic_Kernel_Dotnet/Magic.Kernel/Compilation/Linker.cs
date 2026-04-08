using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Magic.Kernel.Core;
using Magic.Kernel.Processor;

namespace Magic.Kernel.Compilation
{
    internal class Linker
    {
        private readonly Dictionary<string, ExecutableUnit> _linkedCache =
            new Dictionary<string, ExecutableUnit>(StringComparer.OrdinalIgnoreCase);

        public async Task<ExecutableUnit> CompileFileInternalAsync(string fullPath, HashSet<string> useStack)
        {
            if (_linkedCache.TryGetValue(fullPath, out var cached))
                return cached;

            if (!useStack.Add(fullPath))
                throw new CompilationException($"Cyclic 'use' detected. File '{fullPath}' is already in the stack.", -1);

            try
            {
                var sourceCode = await File.ReadAllTextAsync(fullPath);
                var parser = new Parser();
                var semanticAnalyzer = new SemanticAnalyzer();

                var executableUnit = new ExecutableUnit();
                var programStructure = parser.ParseProgram(sourceCode);

                executableUnit.Version = programStructure.Version;
                executableUnit.Name = programStructure.ProgramName;
                executableUnit.Module = programStructure.Module;
                executableUnit.System = programStructure.System;

                var typeIndex = await TypeResolutionIndex.BuildAsync(programStructure, this, fullPath, useStack);
                var extTypeQual = typeIndex.ToExternalQualificationDictionary();

                await semanticAnalyzer.PrepareDeclaredTypeFieldsAsync(programStructure, fullPath, this, extTypeQual).ConfigureAwait(false);
                var analyzed = semanticAnalyzer.AnalyzeProgram(programStructure, parser, sourceCode, extTypeQual, typeIndex);
                executableUnit.EntryPoint = analyzed.EntryPoint;
                executableUnit.Procedures = analyzed.Procedures;
                executableUnit.Functions = analyzed.Functions;

                // Помечаем функции, являющиеся методами типов, чтобы в AGIASM они сериализовались как 'method'.
                foreach (var fn in analyzed.Functions.Keys)
                {
                    if (fn.Contains("_ctor_", StringComparison.OrdinalIgnoreCase) ||
                        fn.Contains("_", StringComparison.OrdinalIgnoreCase))
                    {
                        executableUnit.MethodNames.Add(fn);
                    }
                }

                StampSourcePathOnUnit(executableUnit, fullPath);
                executableUnit.AttachSourcePath = fullPath;

                // Then resolve and link imports.
                await LinkUseDirectivesAsync(
                    importingFilePath: fullPath,
                    importingStructure: programStructure,
                    executableUnit: executableUnit,
                    semanticAnalyzer: semanticAnalyzer,
                    useStack: useStack);

                CollectTypeAndObjectDefinitions(executableUnit);

                executableUnit.StampListingLineNumbers();

                _linkedCache[fullPath] = executableUnit;
                return executableUnit;
            }
            finally
            {
                useStack.Remove(fullPath);
            }
        }

        public async Task LinkUseDirectivesAsync(
            string importingFilePath,
            ProgramStructure importingStructure,
            ExecutableUnit executableUnit,
            SemanticAnalyzer semanticAnalyzer,
            HashSet<string> useStack)
        {
            if (importingStructure.UseDirectives.Count == 0)
                return;

            var mergedModuleEntryPoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var use in importingStructure.UseDirectives)
            {
                var modulePath = Path.GetFullPath(ResolveUseModuleFilePath(importingFilePath, use.ModulePath));
                var moduleUnit = await CompileFileInternalAsync(modulePath, useStack);
                var moduleNs = DefName.GetNamespace(moduleUnit);
                var importPrefix = BuildUseCallPrefix(use);
                var moduleQualifyMap = BuildModuleSymbolQualifyMap(moduleUnit);

                MergeImportedModuleEntryPoint(
                    executableUnit,
                    moduleUnit,
                    modulePath,
                    moduleQualifyMap,
                    mergedModuleEntryPoints);

                // For now we only support "use <module> as { function ...; procedure ...; }".
                // Alias-form is parsed but not semantically applied as a namespace object.
                if (use.Signatures != null && use.Signatures.Count > 0)
                {
                    foreach (var sig in use.Signatures)
                    {
                        if (string.Equals(sig.Kind, "function", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!moduleUnit.Functions.ContainsKey(sig.Name))
                                throw new CompilationException($"Used module '{use.ModulePath}' does not define function '{sig.Name}'.", -1);

                            var nestedPrefix = sig.Name + "_";
                            foreach (var kv in moduleUnit.Functions)
                            {
                                if (!string.Equals(kv.Key, sig.Name, StringComparison.OrdinalIgnoreCase) &&
                                    !kv.Key.StartsWith(nestedPrefix, StringComparison.OrdinalIgnoreCase))
                                    continue;

                                var qn = BuildSymbolAddress(moduleUnit, kv.Value.Name);
                                if (!executableUnit.Functions.ContainsKey(qn))
                                {
                                    var body = CloneExecutionBlockShallow(kv.Value.Body);
                                    StampCommandBlockSourcePath(body, modulePath, moduleNs);
                                    QualifyBareCallsInBlock(body, moduleQualifyMap);
                                    executableUnit.Functions[qn] = new Processor.Function { Name = qn, Body = body };
                                }

                                RewriteImportedCallReferences(executableUnit, importPrefix, kv.Value.Name, qn);
                            }
                        }
                        else if (string.Equals(sig.Kind, "procedure", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!moduleUnit.Procedures.TryGetValue(sig.Name, out var importedProc))
                                throw new CompilationException($"Used module '{use.ModulePath}' does not define procedure '{sig.Name}'.", -1);

                            var qualifiedName = BuildSymbolAddress(moduleUnit, importedProc.Name);
                            if (!executableUnit.Procedures.ContainsKey(qualifiedName))
                            {
                                var body = CloneExecutionBlockShallow(importedProc.Body);
                                StampCommandBlockSourcePath(body, modulePath, moduleNs);
                                QualifyBareCallsInBlock(body, moduleQualifyMap);
                                executableUnit.Procedures[qualifiedName] = new Processor.Procedure { Name = qualifiedName, Body = body };
                            }

                            RewriteImportedCallReferences(executableUnit, importPrefix, importedProc.Name, qualifiedName);
                        }
                        else
                        {
                            throw new CompilationException($"Unsupported use signature kind '{sig.Kind}'.", -1);
                        }
                    }
                }
                else
                {
                    // use <module>;
                    foreach (var kv in moduleUnit.Functions)
                    {
                        var qualifiedName = BuildSymbolAddress(moduleUnit, kv.Value.Name);
                        if (!executableUnit.Functions.ContainsKey(qualifiedName))
                        {
                            var body = CloneExecutionBlockShallow(kv.Value.Body);
                            StampCommandBlockSourcePath(body, modulePath, moduleNs);
                            QualifyBareCallsInBlock(body, moduleQualifyMap);
                            executableUnit.Functions[qualifiedName] = new Processor.Function { Name = qualifiedName, Body = body };
                        }

                        RewriteImportedCallReferences(executableUnit, importPrefix, kv.Value.Name, qualifiedName);
                    }

                    foreach (var kv in moduleUnit.Procedures)
                    {
                        var qualifiedName = BuildSymbolAddress(moduleUnit, kv.Value.Name);
                        if (!executableUnit.Procedures.ContainsKey(qualifiedName))
                        {
                            var body = CloneExecutionBlockShallow(kv.Value.Body);
                            StampCommandBlockSourcePath(body, modulePath, moduleNs);
                            QualifyBareCallsInBlock(body, moduleQualifyMap);
                            executableUnit.Procedures[qualifiedName] = new Processor.Procedure { Name = qualifiedName, Body = body };
                        }

                        RewriteImportedCallReferences(executableUnit, importPrefix, kv.Value.Name, qualifiedName);
                    }
                }
            }
        }

        /// <summary>Разрешает путь из <c>use</c> относительно импортирующего .agi (те же правила, что у линкера).</summary>
        public static string ResolveUseModuleFilePath(string importingFilePath, string modulePath)
        {
            if (string.IsNullOrWhiteSpace(modulePath))
                throw new CompilationException("Empty module path in 'use'.", -1);

            var normalized = modulePath.Trim();
            normalized = normalized.Replace('/', '\\');

            if (!normalized.EndsWith(".agi", StringComparison.OrdinalIgnoreCase))
                normalized += ".agi";

            // Do not treat "modularity:module1.agi" as rooted — only real roots (e.g. C:\..., \\share\...).
            if (Path.IsPathRooted(normalized))
                return Path.GetFullPath(normalized);

            var dir = Path.GetDirectoryName(importingFilePath) ?? Directory.GetCurrentDirectory();

            // Candidate 1: relative to importing file folder.
            var candidate1 = Path.GetFullPath(Path.Combine(dir, normalized));
            if (File.Exists(candidate1))
                return candidate1;

            // Candidate 2: relative to nearest ancestor folder named "samples".
            string? samplesRoot = null;
            var cursor = dir;
            for (var i = 0; i < 10; i++)
            {
                if (string.Equals(Path.GetFileName(cursor), "samples", StringComparison.OrdinalIgnoreCase))
                {
                    samplesRoot = cursor;
                    break;
                }

                var parent = Directory.GetParent(cursor);
                if (parent == null) break;
                cursor = parent.FullName;
            }

            if (!string.IsNullOrWhiteSpace(samplesRoot))
            {
                var candidate2 = Path.GetFullPath(Path.Combine(samplesRoot, normalized));
                if (File.Exists(candidate2))
                    return candidate2;
            }

            // Last attempt: relative to dir (even if doesn't exist) to keep error message deterministic.
            return candidate1;
        }

        /// <summary>Все команды в блоке получают один путь — для отладки и линковки.</summary>
        public static void StampCommandBlockSourcePath(ExecutionBlock? block, string agiFilePath, string? typeDefUnitNamespace = null)
        {
            if (block == null || string.IsNullOrWhiteSpace(agiFilePath))
                return;
            var norm = Path.GetFullPath(agiFilePath);
            foreach (var c in block)
            {
                c.SourcePath = norm;
                if (typeDefUnitNamespace != null)
                    c.TypeDefUnitNamespace = typeDefUnitNamespace;
            }
        }

        public void StampSourcePathOnUnit(ExecutableUnit unit, string fullPath)
        {
            var norm = Path.GetFullPath(fullPath);
            var ns = DefName.GetNamespace(unit);
            StampCommandBlockSourcePath(unit.EntryPoint, norm, ns);
            foreach (var p in unit.Procedures.Values)
                StampCommandBlockSourcePath(p.Body, norm, ns);
            foreach (var f in unit.Functions.Values)
                StampCommandBlockSourcePath(f.Body, norm, ns);
        }

        /// <summary>Короткие имена символов модуля → полные адреса для разрешения call внутри импортируемых тел (вложенные calculate_add и т.д.).</summary>
        private static Dictionary<string, string> BuildModuleSymbolQualifyMap(ExecutableUnit moduleUnit)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in moduleUnit.Functions)
                d[kv.Key] = BuildSymbolAddress(moduleUnit, kv.Value.Name);
            foreach (var kv in moduleUnit.Procedures)
                d[kv.Key] = BuildSymbolAddress(moduleUnit, kv.Value.Name);
            return d;
        }

        private static ExecutionBlock CloneExecutionBlockShallow(ExecutionBlock source)
        {
            var block = new ExecutionBlock();
            foreach (var cmd in source)
            {
                var c = new Command
                {
                    Opcode = cmd.Opcode,
                    Operand2 = cmd.Operand2,
                    SourceLine = cmd.SourceLine,
                    SourcePath = cmd.SourcePath,
                    TypeDefUnitNamespace = cmd.TypeDefUnitNamespace,
                    AsmListingLine = cmd.AsmListingLine
                };
                if (cmd.Operand1 is CallInfo oci)
                {
                    var n = new CallInfo { FunctionName = oci.FunctionName };
                    foreach (var p in oci.Parameters)
                        n.Parameters[p.Key] = p.Value;
                    c.Operand1 = n;
                }
                else
                    c.Operand1 = cmd.Operand1;
                block.Add(c);
            }
            return block;
        }

        private static void QualifyBareCallsInBlock(ExecutionBlock? block, Dictionary<string, string> moduleSymbolToQualified)
        {
            if (block == null || moduleSymbolToQualified.Count == 0)
                return;
            foreach (var command in block)
            {
                if (command.Operand1 is CallInfo ci &&
                    !string.IsNullOrEmpty(ci.FunctionName) &&
                    moduleSymbolToQualified.TryGetValue(ci.FunctionName, out var qualified))
                    ci.FunctionName = qualified;
            }
        }

        private static string BuildUseCallPrefix(UseNode use)
        {
            if (!string.IsNullOrWhiteSpace(use.Alias))
                return use.Alias!.Trim();
            var modulePath = use.ModulePath?.Trim() ?? string.Empty;
            if (modulePath.Length == 0)
                return string.Empty;
            var segments = modulePath
                .Split(new[] { '\\', '/', ':' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
            return segments.Length == 0 ? string.Empty : segments[^1];
        }

        private static string BuildSymbolAddress(ExecutableUnit unit, string symbolName) =>
            DefName.QualifyInUnit(unit, symbolName);

        private static void RewriteImportedCallReferences(ExecutableUnit executableUnit, string importPrefix, string symbolName, string qualifiedName)
        {
            var prefixedName = string.IsNullOrWhiteSpace(importPrefix)
                ? symbolName
                : $"{importPrefix}:{symbolName}";

            void RewriteAll(string fromName)
            {
                if (string.IsNullOrWhiteSpace(fromName))
                    return;
                RewriteCommandBlockReferences(executableUnit.EntryPoint, fromName, qualifiedName);
                foreach (var proc in executableUnit.Procedures.Values)
                    RewriteCommandBlockReferences(proc.Body, fromName, qualifiedName);
                foreach (var func in executableUnit.Functions.Values)
                    RewriteCommandBlockReferences(func.Body, fromName, qualifiedName);
            }

            RewriteAll(prefixedName);
            // Импорт из use { function f } — в потребителе пишут f(...) и f.sub(...), не только module1:f.
            if (!string.Equals(prefixedName, symbolName, StringComparison.Ordinal))
                RewriteAll(symbolName);
        }

        private static void RewriteCommandBlockReferences(ExecutionBlock? block, string fromName, string toName)
        {
            if (block == null || string.IsNullOrWhiteSpace(fromName) || string.IsNullOrWhiteSpace(toName))
                return;

            foreach (var command in block)
            {
                if (command.Operand1 is CallInfo callInfo &&
                    string.Equals(callInfo.FunctionName, fromName, StringComparison.Ordinal))
                {
                    callInfo.FunctionName = toName;
                    continue;
                }

                if (command.Opcode == Opcodes.Push && command.Operand1 is PushOperand pushOperand &&
                    string.Equals(pushOperand.Kind, "AddressLiteral", StringComparison.Ordinal) &&
                    pushOperand.Value is string address &&
                    string.Equals(address, fromName, StringComparison.Ordinal))
                {
                    pushOperand.Value = toName;
                }
            }
        }

        private static void MergeImportedModuleEntryPoint(
            ExecutableUnit targetUnit,
            ExecutableUnit moduleUnit,
            string modulePath,
            Dictionary<string, string> moduleQualifyMap,
            HashSet<string> mergedModuleEntryPoints)
        {
            if (moduleUnit.EntryPoint == null || moduleUnit.EntryPoint.Count == 0)
                return;

            var mergeKey = BuildModuleEntryPointMergeKey(moduleUnit, modulePath);
            if (!mergedModuleEntryPoints.Add(mergeKey))
                return;

            var importedPrelude = CloneExecutionBlockShallow(moduleUnit.EntryPoint);
            StampCommandBlockSourcePath(importedPrelude, modulePath, DefName.GetNamespace(moduleUnit));
            QualifyBareCallsInBlock(importedPrelude, moduleQualifyMap);

            // Imported module prelude (types/defs) should run before importer entrypoint.
            var merged = new ExecutionBlock();
            foreach (var cmd in importedPrelude)
                merged.Add(cmd);
            foreach (var cmd in targetUnit.EntryPoint)
                merged.Add(cmd);
            targetUnit.EntryPoint = merged;
        }

        private static string BuildModuleEntryPointMergeKey(ExecutableUnit moduleUnit, string modulePath)
        {
            var parts = new[]
            {
                moduleUnit.System?.Trim() ?? string.Empty,
                moduleUnit.Module?.Trim() ?? string.Empty,
                moduleUnit.Name?.Trim() ?? string.Empty
            };

            if (parts.Any(p => p.Length > 0))
                return string.Join(":", parts);

            return Path.GetFullPath(modulePath);
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

                    if (IsChildOrNonTypeDef(args))
                        continue;

                    var baseNames = args.Skip(1).OfType<string>().ToList();
                    var isClass = baseNames.Count > 0 && !string.Equals(baseNames[0], "type", StringComparison.OrdinalIgnoreCase);
                    var defNs = string.IsNullOrWhiteSpace(cmd.TypeDefUnitNamespace) ? unitNs : cmd.TypeDefUnitNamespace.Trim();
                    var created = Core.DefType.FromDefBytecode(typeName, defNs, isClass, isClass ? baseNames : null);
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

        /// <summary>
        /// Добавляет типы из всех <c>use</c> в индекс (коллизии коротких имён сохраняются как несколько кандидатов).
        /// </summary>
        internal async Task PopulateTypeIndexFromUsesAsync(
            string importingFilePath,
            ProgramStructure importingStructure,
            TypeResolutionIndex index,
            HashSet<string> useStack)
        {
            if (importingStructure.UseDirectives.Count == 0)
                return;

            foreach (var use in importingStructure.UseDirectives)
            {
                var modulePath = Path.GetFullPath(ResolveUseModuleFilePath(importingFilePath, use.ModulePath));
                var moduleUnit = await CompileFileInternalAsync(modulePath, useStack);
                foreach (var t in moduleUnit.Types)
                {
                    var fq = t.FullName?.Trim() ?? "";
                    if (string.IsNullOrEmpty(fq))
                        continue;
                    index.AddCandidate(fq, t is DefClass);
                }
            }
        }

        /// <summary>
        /// Парсит исходники <c>use</c>-модулей и мержит объявления полей в компилятор (для вывода типа локалов).
        /// </summary>
        internal async Task MergeDeclaredTypeFieldSpecsFromUsesAsync(
            string importingFilePath,
            ProgramStructure importingStructure,
            StatementLoweringCompiler compiler,
            HashSet<string> visited,
            IReadOnlyDictionary<string, string>? externalTypeQualification)
        {
            if (importingStructure.UseDirectives.Count == 0)
                return;

            foreach (var use in importingStructure.UseDirectives)
            {
                var modulePath = Path.GetFullPath(ResolveUseModuleFilePath(importingFilePath, use.ModulePath));
                if (!visited.Add(modulePath))
                    continue;

                try
                {
                    var moduleSource = await File.ReadAllTextAsync(modulePath).ConfigureAwait(false);
                    var parser = new Parser();
                    var moduleStructure = parser.ParseProgram(moduleSource);

                    var slice = new StatementLoweringCompiler();
                    slice.SetDefaultTypeNamespacePrefix(DefName.GetDefaultTypeNamespacePrefix(moduleStructure));
                    slice.SetLocallyDeclaredTypeNames(SemanticAnalyzer.BuildLocallyDeclaredTypeNames(moduleStructure));
                    if (externalTypeQualification is { Count: > 0 })
                    {
                        slice.SetExternalTypeQualificationMap(
                            new Dictionary<string, string>(externalTypeQualification, StringComparer.OrdinalIgnoreCase));
                    }

                    slice.MergeDeclaredTypeFieldsFromProgramTypes(moduleStructure.Types);
                    compiler.AbsorbDeclaredTypeFieldSpecsFrom(slice);

                    if (moduleStructure.UseDirectives.Count > 0)
                        await MergeDeclaredTypeFieldSpecsFromUsesAsync(modulePath, moduleStructure, compiler, visited, externalTypeQualification).ConfigureAwait(false);
                }
                finally
                {
                    visited.Remove(modulePath);
                }
            }
        }
    }
}

