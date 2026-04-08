using System;
using System.Collections.Generic;
using System.Linq;
using Magic.Kernel.Compilation.Ast;
using Magic.Kernel.Processor;
using Magic.Kernel.Space;
using Magic.Kernel;

namespace Magic.Kernel.Compilation
{
    /// <summary>Результат анализа программы: скомпилированные entrypoint, процедуры и функции.</summary>
    public class AnalyzedProgram
    {
        public ExecutionBlock EntryPoint { get; set; } = new ExecutionBlock();
        public Dictionary<string, Processor.Procedure> Procedures { get; set; } = new Dictionary<string, Processor.Procedure>();
        public Dictionary<string, Processor.Function> Functions { get; set; } = new Dictionary<string, Processor.Function>();
    }

    public class SemanticAnalyzer
    {
        private readonly Assembler _assembler;
        private readonly StatementLoweringCompiler _statementLoweringCompiler;
        private string? _defaultTypeNamespacePrefix;

        public SemanticAnalyzer()
        {
            _assembler = new Assembler();
            _statementLoweringCompiler = new StatementLoweringCompiler();
        }

        /// <summary>
        /// Префикс для коротких имён типов в bytecode/SDK (<c>def</c>, <c>defobj</c>, методы <c>Type_ctor_1</c>):
        /// те же непустые части, что и <see cref="DefName.GetNamespace(ProgramStructure)"/>, плюс завершающее <c>:</c> для склейки коротких имён.
        /// </summary>
        private static string? BuildDefaultTypeNamespacePrefix(ProgramStructure programStructure) =>
            DefName.GetDefaultTypeNamespacePrefix(programStructure);

        /// <summary>Последний сегмент после <c>:</c> (короткое имя типа в исходнике).</summary>
        public static string ExtractUnqualifiedTypeName(string qualifiedName)
        {
            if (string.IsNullOrWhiteSpace(qualifiedName))
                return "";
            var idx = qualifiedName.LastIndexOf(':');
            return idx >= 0 ? qualifiedName.Substring(idx + 1).Trim() : qualifiedName.Trim();
        }

        internal static HashSet<string> BuildLocallyDeclaredTypeNames(ProgramStructure programStructure)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in programStructure.Types)
            {
                var line = node.Text?.Trim() ?? "";
                if (line.Length == 0)
                    continue;
                var colonIdx = line.IndexOf(':');
                if (colonIdx <= 0)
                    continue;
                var braceIdx = line.IndexOf('{');
                if (braceIdx <= colonIdx)
                    continue;
                var header = line.Substring(colonIdx + 1, braceIdx - colonIdx - 1).Trim();
                if (header.StartsWith("table", StringComparison.OrdinalIgnoreCase) ||
                    header.StartsWith("database", StringComparison.OrdinalIgnoreCase))
                    continue;
                var namePart = line.Substring(0, colonIdx).Trim();
                if (namePart.Length > 0)
                    set.Add(namePart);
            }

            return set;
        }

        public Command Analyze(InstructionNode instruction)
        {
            var opcode = MapOpcode(instruction.Opcode);
            return _assembler.Emit(opcode, instruction.Parameters);
        }

        /// <summary>
        /// Собирает карту полей def-типов из этого файла и из транзитивных <c>use</c> (нужно до <see cref="AnalyzeProgram"/> для <c>var b := x.Field</c>).
        /// </summary>
        internal async Task PrepareDeclaredTypeFieldsAsync(
            ProgramStructure programStructure,
            string? resolvedMainPath,
            Linker? linker,
            IReadOnlyDictionary<string, string>? externalTypeQualification = null)
        {
            _statementLoweringCompiler.MergeDeclaredTypeFieldsFromProgramTypes(programStructure.Types);
            if (string.IsNullOrEmpty(resolvedMainPath) || linker == null || programStructure.UseDirectives.Count == 0)
                return;

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string>? extDict = null;
            if (externalTypeQualification is { Count: > 0 })
                extDict = new Dictionary<string, string>(externalTypeQualification, StringComparer.OrdinalIgnoreCase);

            await linker.MergeDeclaredTypeFieldSpecsFromUsesAsync(
                    resolvedMainPath,
                    programStructure,
                    _statementLoweringCompiler,
                    visited,
                    extDict)
                .ConfigureAwait(false);
        }

        /// <summary>Analyze instruction and add resulting command(s) to block. For zero-arg Call, prepends Push(arity=0) unless arity was already pushed manually right before Call.</summary>
        private void AddAnalyzedCommand(ExecutionBlock block, InstructionNode instructionNode)
        {
            var cmd = Analyze(instructionNode);
            if (instructionNode.SourceLine > 0)
            {
                cmd.SourceLine = instructionNode.SourceLine;
            }

            if (ShouldInjectZeroArity(block, instructionNode, cmd))
                block.Add(_assembler.EmitPushIntLiteral(0));
            block.Add(cmd);
        }

        private static bool ShouldInjectZeroArity(ExecutionBlock block, InstructionNode instructionNode, Command cmd)
        {
            if (cmd.Opcode != Opcodes.Call)
                return false;

            var parameters = instructionNode.Parameters;
            if (parameters == null || parameters.Count == 0)
                return true;

            // Call syntax always includes FunctionNameParameterNode.
            // If there are extra params, arity is supplied by caller/lowering.
            var hasOnlyFunctionName = parameters.TrueForAll(p => p is FunctionNameParameterNode);
            if (!hasOnlyFunctionName)
                return false;

            // Lowered patterns like ":time -> get" already emit explicit arity push.
            if (block.Count > 0 &&
                block[^1].Opcode == Opcodes.Push &&
                block[^1].Operand1 is PushOperand pushOperand &&
                pushOperand.Kind == "IntLiteral")
            {
                return false;
            }

            return true;
        }

        /// <summary>Компилирует полную структуру программы (AST) в команды. Если entrypoint пуст — парсит sourceCode как плоский набор инструкций (fallback).
        /// Проверка консистентности переменных: при использовании необъявленной переменной выбрасывается <see cref="UndeclaredVariableException"/>.</summary>
        public AnalyzedProgram AnalyzeProgram(
            ProgramStructure programStructure,
            Parser parser,
            string sourceCode,
            IReadOnlyDictionary<string, string>? externalTypeQualification = null,
            TypeResolutionIndex? typeResolutionIndex = null)
        {
            var result = new AnalyzedProgram();
            _defaultTypeNamespacePrefix = BuildDefaultTypeNamespacePrefix(programStructure);
            _statementLoweringCompiler.SetDefaultTypeNamespacePrefix(_defaultTypeNamespacePrefix);
            _statementLoweringCompiler.SetLocallyDeclaredTypeNames(BuildLocallyDeclaredTypeNames(programStructure));
            Dictionary<string, string>? extDict = null;
            if (externalTypeQualification is { Count: > 0 })
                extDict = new Dictionary<string, string>(externalTypeQualification, StringComparer.OrdinalIgnoreCase);
            _statementLoweringCompiler.SetExternalTypeQualificationMap(extDict);
            _statementLoweringCompiler.SetTypeResolutionIndex(typeResolutionIndex);
            if (programStructure.Types.Count > 0)
            {
                _statementLoweringCompiler.BeginStatementSequence();
                foreach (var instructionNode in LowerToInstructions(programStructure.Types, registerGlobals: true))
                    AddAnalyzedCommand(result.EntryPoint, instructionNode);

                // После обработки прелюдии StatementLoweringCompiler мог собрать методы типов
                // (constructor/method внутри Point: type { ... }). Для них генерируем отдельные
                // функции с заголовком "method" в AGIASM (семантически как function), пытаясь
                // скомпилировать тело конструктора/метода. При ошибке компиляции gracefully
                // откатываемся к пустому stub'у (только ret), чтобы не ломать существующие программы.
                var typeMethods = _statementLoweringCompiler.TakeTypeMethods();
                var instanceOverloadTable = BuildInstanceMethodOverloadTable(typeMethods);
                foreach (var (typeName, name, bodyLines, fieldSpecs) in typeMethods)
                {
                    var function = new Processor.Function { Name = name };
                    try
                    {
                        ExtractTypeMethodSignatureAndBody(bodyLines, out var paramNames, out var paramTypeSpecs, out var bodyStatements);
                        var isCtor = name.IndexOf("_ctor_", StringComparison.OrdinalIgnoreCase) >= 0;
                        var methodCompiler = new StatementLoweringCompiler();
                        methodCompiler.InheritGlobalSlots(_statementLoweringCompiler);
                        methodCompiler.BeginStatementSequence();
                        methodCompiler.ResetLocals();
                        methodCompiler.SetInstanceMethodOverloads(instanceOverloadTable);

                        // Пролог: Pop(arity) + Pop параметров
                        if (paramNames.Count > 0)
                        {
                            // 1) выделяем слоты последовательно: _this -> 0, x -> 1, y -> 2
                            foreach (var paramName in paramNames)
                                methodCompiler.AllocateLocalSlot(paramName);

                            methodCompiler.RegisterPersistentVarDefType("_this", typeName.Trim());
                            for (var ti = 0; ti < paramNames.Count && ti < paramTypeSpecs.Count; ti++)
                            {
                                var spec = paramTypeSpecs[ti];
                                if (string.IsNullOrWhiteSpace(spec) || IsBuiltinParameterTypeSpec(spec))
                                    continue;
                                methodCompiler.RegisterPersistentVarDefType(paramNames[ti], methodCompiler.QualifyTypeReferenceForDefObj(spec.Trim()));
                            }

                            // 2) снимаем arity (N), как и раньше
                            function.Body.Add(_assembler.Emit(Opcodes.Pop, null));

                            // 3) Как у procedure: на стеке [p0, p1, ..., pN-1, N] — pN-1 у вершины; снимаем с конца списка параметров.
                            for (var pi = paramNames.Count - 1; pi >= 0; pi--)
                            {
                                var paramName = paramNames[pi];
                                methodCompiler.TryResolveLocalSlot(paramName, out var physical, out var logicalIndex);

                                function.Body.Add(_assembler.Emit(Opcodes.Pop,
                                    new List<ParameterNode>
                                    {
                                        new Ast.MemoryParameterNode
                                        {
                                            Name = "index",
                                            Index = physical,
                                            LogicalIndex = logicalIndex
                                        }
                                    }));
                            }
                        }

                        // Для обычных методов типов (не конструкторов) поднимаем значения полей в локальные переменные,
                        // чтобы тело метода могло использовать их как X/Y и т.п. (например, в интерполяции строк).
                        if (!isCtor && fieldSpecs != null && fieldSpecs.Count > 0 &&
                            methodCompiler.TryResolveLocalSlot("_this", out var thisPhys, out var thisLog))
                        {
                            foreach (var fieldName in fieldSpecs.Keys)
                            {
                                // Создаём локальный слот с тем же именем поля, если его ещё нет.
                                if (!methodCompiler.TryResolveLocalSlot(fieldName, out var fieldPhys, out var fieldLog))
                                {
                                    fieldPhys = methodCompiler.AllocateLocalSlot(fieldName);
                                    methodCompiler.TryResolveLocalSlot(fieldName, out fieldPhys, out fieldLog);
                                }

                                // push _this
                                function.Body.Add(_assembler.Emit(Opcodes.Push,
                                    new List<ParameterNode>
                                    {
                                        new Ast.MemoryParameterNode
                                        {
                                            Name = "index",
                                            Index = thisPhys,
                                            LogicalIndex = thisLog
                                        }
                                    }));

                                // push "FieldName"
                                function.Body.Add(_assembler.Emit(Opcodes.Push,
                                    new List<ParameterNode>
                                    {
                                        new Ast.StringParameterNode
                                        {
                                            Name = "string",
                                            Value = fieldName
                                        }
                                    }));

                                // getobj -> значение поля
                                function.Body.Add(_assembler.Emit(Opcodes.GetObj, null));

                                // pop в локальную переменную fieldName
                                function.Body.Add(_assembler.Emit(Opcodes.Pop,
                                    new List<ParameterNode>
                                    {
                                        new Ast.MemoryParameterNode
                                        {
                                            Name = "index",
                                            Index = fieldPhys,
                                            LogicalIndex = fieldLog
                                        }
                                    }));
                            }
                        }

                        // Префикс namespace для методов типов: полное имя типа -> "samples:modularity:module2:"
                        var nsPrefix = DefName.ParseQualified(typeName).NamespaceWithTrailingColon;

                        // Тело: склеиваем многострочные операторы (switch/if/… с «висячими» «{»), как в CompileStatementLines.
                        var coalescedBody = StatementLoweringCompiler.CoalesceMultilineStatements(bodyStatements).ToList();

                        foreach (var rawLine in coalescedBody)
                        {
                            if (string.IsNullOrWhiteSpace(rawLine))
                                continue;

                            // Сначала ctor-присваивания полей вида "X:= x;"
                            if (isCtor && methodCompiler.TryLowerFieldAssign(rawLine, fieldSpecs, _assembler, function.Body))
                                continue;

                            // Затем: базовый конструктор вида Shape(origin); в теле конструктора Circle.
                            if (isCtor && methodCompiler.TryLowerBaseCtorCall(rawLine, _assembler, function.Body, nsPrefix))
                                continue;

                            // Потом плюс-assign для методов типов: "X += p.X;" и т.п.
                            if (!isCtor && methodCompiler.TryLowerFieldPlusAssign(rawLine, fieldSpecs, _assembler, function.Body))
                                continue;

                            // Затем простой "+=" для локальных переменных/полей, уже поднятых в локалы:
                            // Shapes += circle; -> push [Shapes]; push [circle]; callobj add; pop [Shapes]; (для T[])
                            if (!isCtor && methodCompiler.TryLowerLocalPlusAssign(rawLine, _assembler, function.Body, fieldSpecs))
                                continue;

                            // Вызов базового метода вида Shape.Draw(); в методах Circle.Draw и т.п.
                            if (!isCtor && methodCompiler.TryLowerBaseMethodCall(rawLine, _assembler, function.Body, nsPrefix))
                                continue;

                            var lowered = methodCompiler.Lower(new[] { rawLine }, registerGlobals: false, statementStartSourceLine: 0);
                            foreach (var instructionNode in lowered)
                                AddAnalyzedCommand(function.Body, instructionNode);
                        }

                        if (function.Body.Count == 0 || function.Body[^1].Opcode != Opcodes.Ret)
                            function.Body.Add(_assembler.Emit(Opcodes.Ret, null));
                        result.Functions[name] = function;
                    }
                    catch (CompilationException)
                    {
                        function.Body.Clear();
                        function.Body.Add(_assembler.Emit(Opcodes.Ret, null));
                    }

                    result.Functions[name] = function;
                }
            }
            if (programStructure.EntryPoint != null && programStructure.EntryPoint.Count > 0)
            {
                _statementLoweringCompiler.BeginStatementSequence();
                foreach (var instructionNode in LowerToInstructions(programStructure.EntryPoint))
                    AddAnalyzedCommand(result.EntryPoint, instructionNode);
            }
            else
            {
                // If this is a structured module (no entrypoint block), keep EntryPoint empty.
                // Only fall back to parsing the whole file as a flat instruction set when
                // the parser failed to recognize any structured program headers.
                if (!programStructure.IsProgramStructure)
                    result.EntryPoint = AnalyzeInstructions(parser.ParseAsmStructure(sourceCode));
            }
            foreach (var proc in programStructure.Procedures)
            {
                var procNode = proc.Value;
                var procedure = new Processor.Procedure { Name = procNode.Name };

                // Each procedure uses a fresh compiler so parameter slots are local to the procedure
                // and body instructions use push [N] (local memory) instead of push global: [N].
                // Global variables declared in the prelude/entrypoint are inherited so they remain
                // accessible inside the procedure body (e.g. schema references like Db>).
                var procCompiler = new StatementLoweringCompiler();
                procCompiler.InheritGlobalSlots(_statementLoweringCompiler);
                procCompiler.BeginStatementSequence();
                procCompiler.ResetLocals();

                // If the procedure has named parameters, prepend parameter-binding instructions.
                // Calling convention: stack has [arg0, arg1, ..., argN-1, N] before procedure body.
                // We allocate local memory slots for each parameter and emit Pop instructions to bind them.
                var paramNames = procNode.Parameters;
                if (paramNames != null && paramNames.Count > 0)
                {
                    // Calling convention: stack has [arg0, arg1, ..., argN-1, N] before procedure body.
                    // 1. Pop arity (N) and discard it.
                    // 2. Pop each argument in reverse order (argN-1 first) into a dedicated local memory slot.
                    // 3. Register those slots as local ("memory" kind) so the body uses push [N] (not global).

                    // Discard arity (top of stack)
                    procedure.Body.Add(_assembler.Emit(Opcodes.Pop, null));

                    // Allocate local slots and pop args (last param is nearest to arity on stack)
                    var paramSlots = new int[paramNames.Count];
                    for (var pi = paramNames.Count - 1; pi >= 0; pi--)
                    {
                        var paramName = paramNames[pi];
                        var slot = procCompiler.AllocateLocalSlot(paramName);
                        paramSlots[pi] = slot;
                        var logicalIndex = pi; // logical 0-based per procedure
                        procedure.Body.Add(_assembler.Emit(Opcodes.Pop,
                            new List<ParameterNode>
                            {
                                new Ast.MemoryParameterNode
                                {
                                    Name = "index",
                                    Index = slot,
                                    LogicalIndex = logicalIndex
                                }
                            }));
                    }
                }

                CompileProcedureBodyWithNestedFunctions(procNode, procedure, procCompiler, result);
                // Явный ret в конце процедуры (для корректного asm-дампа и локальных подпрограмм).
                if (procedure.Body.Count == 0 || procedure.Body[procedure.Body.Count - 1].Opcode != Opcodes.Ret)
                    procedure.Body.Add(_assembler.Emit(Opcodes.Ret, null));
                result.Procedures[proc.Key] = procedure;
            }
            foreach (var func in programStructure.Functions)
            {
                var funcNode = func.Value;
                var paramList = funcNode.Parameters ?? new List<string>();
                CompileFunctionAst(func.Key, paramList, funcNode.Body, result, _statementLoweringCompiler, enclosingCallScope: null);
            }
            return result;
        }

        /// <summary>Компилирует список AST-инструкций в блок команд (для fallback без структуры программы).</summary>
        public ExecutionBlock AnalyzeInstructions(IEnumerable<InstructionNode> nodes)
        {
            var block = new ExecutionBlock();
            foreach (var node in nodes)
                AddAnalyzedCommand(block, node);
            return block;
        }

        private bool TryLowerFieldPlusAssign(
            string rawLine,
            HashSet<string> fields,
            StatementLoweringCompiler compiler,
            ExecutionBlock body)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                return false;

            // Ищем "X += p.X;" / "Y += p.Y;"
            var idx = line.IndexOf("+=", StringComparison.Ordinal);
            if (idx <= 0)
                return false;

            var left = line.Substring(0, idx).Trim();  // "X"
            if (!fields.Contains(left))
                return false;

            var rhs = line.Substring(idx + 2).Trim().TrimEnd(';').Trim(); // "p.X"
            if (!compiler.TryResolveLocalSlot("_this", out var thisPhys, out var thisLog))
                return false;

            // RHS вида "<var>.<field>"
            var dot = rhs.IndexOf('.');
            if (dot <= 0)
                return false;
            var rhsVar = rhs.Substring(0, dot).Trim();           // "p"
            var rhsField = rhs.Substring(dot + 1).Trim();        // "X"

            if (!compiler.TryResolveLocalSlot(rhsVar, out var rhsPhys, out var rhsLog))
                return false;

            // this.<left> + rhsVar.<rhsField>:

            // push this
            // this.<left>
            body.Add(_assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
        new Ast.MemoryParameterNode
        {
            Name = "index",
            Index = thisPhys,
            LogicalIndex = thisLog
        }
                }));
            body.Add(_assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
        new Ast.StringParameterNode
        {
            Name = "string",
            Value = left      // "X" или "Y"
        }
                }));
            var getThisField = _assembler.Emit(Opcodes.GetObj, null);
            //getThisField.Operand1 = left; // только для красивого AGIASM, рантайм имя возьмёт со стека
            body.Add(getThisField);

            // p.<rhsField>
            body.Add(_assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
        new Ast.MemoryParameterNode
        {
            Name = "index",
            Index = rhsPhys,
            LogicalIndex = rhsLog
        }
                }));
            body.Add(_assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
        new Ast.StringParameterNode
        {
            Name = "string",
            Value = rhsField   // тоже "X" или "Y"
        }
                }));
            var getOtherField = _assembler.Emit(Opcodes.GetObj, null);
            //getOtherField.Operand1 = rhsField;
            body.Add(getOtherField);

            // add
            // add
            body.Add(_assembler.Emit(Opcodes.Add, null));

            // tmp слот под результат
            var tmpName = $"__tmp_{left}";
            if (!compiler.TryResolveLocalSlot(tmpName, out var tmpPhys, out var tmpLog))
            {
                tmpPhys = compiler.AllocateLocalSlot(tmpName);
                compiler.TryResolveLocalSlot(tmpName, out tmpPhys, out tmpLog);
            }

            // pop tmp
            body.Add(_assembler.Emit(Opcodes.Pop,
                new List<ParameterNode>
                {
        new Ast.MemoryParameterNode
        {
            Name = "index",
            Index = tmpPhys,
            LogicalIndex = tmpLog
        }
                }));

            // push this
            body.Add(_assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
        new Ast.MemoryParameterNode
        {
            Name = "index",
            Index = thisPhys,
            LogicalIndex = thisLog
        }
                }));

            // push field name
            body.Add(_assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
        new Ast.StringParameterNode
        {
            Name = "string",
            Value = left
        }
                }));

            // push tmp
            body.Add(_assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
        new Ast.MemoryParameterNode
        {
            Name = "index",
            Index = tmpPhys,
            LogicalIndex = tmpLog
        }
                }));

            // setobj "X"/"Y" (для AGIASM)
            var set = _assembler.Emit(Opcodes.SetObj, null);
            // set.Operand1 = left;  // УБРАТЬ
            body.Add(set);
            body.Add(_assembler.Emit(Opcodes.Pop, null));

            return true;
        }

        /// <summary>
        /// Понижает простой "+=" для локальных переменных (включая поля, поднятые в локалы методом):
        /// left += right;
        /// -> push [left]; push [right]; add; pop [left];
        /// </summary>
        private bool TryLowerLocalPlusAssign(
            string rawLine,
            StatementLoweringCompiler compiler,
            ExecutionBlock body)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                return false;

            var opIdx = line.IndexOf("+=", StringComparison.Ordinal);
            if (opIdx <= 0)
                return false;

            var left = line.Substring(0, opIdx).Trim();
            var right = line.Substring(opIdx + 2).Trim();
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            if (right.EndsWith(";", StringComparison.Ordinal))
                right = right.Substring(0, right.Length - 1).Trim();

            // Не лезем в сложные выражения/доступы через точку.
            if (left.Contains('.') || right.Contains('.'))
                return false;

            // Оба должны быть локалами в текущем методе.
            if (!compiler.TryResolveLocalSlot(left, out var leftPhys, out var leftLog))
                return false;
            if (!compiler.TryResolveLocalSlot(right, out var rightPhys, out var rightLog))
                return false;

            // push [left]
            body.Add(_assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = leftPhys,
                        LogicalIndex = leftLog
                    }
                }));

            // push [right]
            body.Add(_assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = rightPhys,
                        LogicalIndex = rightLog
                    }
                }));

            // add
            body.Add(_assembler.Emit(Opcodes.Add, null));

            // pop [left]
            body.Add(_assembler.Emit(Opcodes.Pop,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = leftPhys,
                        LogicalIndex = leftLog
                    }
                }));

            return true;
        }

        /// <summary>
        /// Понижает вызов базового конструктора в конструкторе дочернего типа:
        /// Shape(origin);  ->  push this; push origin; push 1; call {nsPrefix}Shape_ctor_1; pop
        /// </summary>
        private bool TryLowerBaseCtorCall(
            string rawLine,
            StatementLoweringCompiler compiler,
            ExecutionBlock body,
            string nsPrefix)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                return false;

            // Ожидаем простой вызов вида BaseType(args...);
            if (!line.EndsWith(";", StringComparison.Ordinal))
                return false;
            line = line.Substring(0, line.Length - 1).Trim();
            var parenIdx = line.IndexOf('(');
            var lastParenIdx = line.LastIndexOf(')');
            if (parenIdx <= 0 || lastParenIdx <= parenIdx)
                return false;

            var baseName = line.Substring(0, parenIdx).Trim();
            if (string.IsNullOrEmpty(baseName))
                return false;

            var argsText = line.Substring(parenIdx + 1, lastParenIdx - parenIdx - 1).Trim();
            var args = new List<string>();
            if (!string.IsNullOrEmpty(argsText))
            {
                var depth = 0;
                var sb = new System.Text.StringBuilder();
                foreach (var ch in argsText)
                {
                    if (ch == ',' && depth == 0)
                    {
                        var arg = sb.ToString().Trim();
                        if (!string.IsNullOrEmpty(arg))
                            args.Add(arg);
                        sb.Clear();
                        continue;
                    }
                    if (ch is '(' or '{' or '[') depth++;
                    if (ch is ')' or '}' or ']') depth--;
                    sb.Append(ch);
                }
                var last = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(last))
                    args.Add(last);
            }

            // Нужен _this
            if (!compiler.TryResolveLocalSlot("_this", out var thisPhys, out var thisLog))
                return false;

            // push this
            body.Add(_assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = thisPhys,
                        LogicalIndex = thisLog
                    }
                }));

            // push args (простые имена локальных переменных или литералы)
            foreach (var argExpr in args)
            {
                var ae = argExpr.Trim();
                if (ae.Length == 0)
                    continue;

                // локальная переменная / параметр
                if (compiler.TryResolveLocalSlot(ae, out var phys, out var log))
                {
                    body.Add(_assembler.Emit(Opcodes.Push,
                        new List<ParameterNode>
                        {
                            new Ast.MemoryParameterNode
                            {
                                Name = "index",
                                Index = phys,
                                LogicalIndex = log
                            }
                        }));
                    continue;
                }

                // целочисленный литерал
                if (int.TryParse(ae, out var intVal))
                {
                    body.Add(_assembler.Emit(Opcodes.Push,
                        new List<ParameterNode>
                        {
                            new Ast.IndexParameterNode { Name = "int", Value = intVal }
                        }));
                    continue;
                }

                // строковый литерал "..."
                if (ae.Length >= 2 && ae[0] == '"' && ae[^1] == '"')
                {
                    var s = ae.Substring(1, ae.Length - 2);
                    body.Add(_assembler.Emit(Opcodes.Push,
                        new List<ParameterNode>
                        {
                            new Ast.StringParameterNode { Name = "string", Value = s }
                        }));
                    continue;
                }

                // Сложные выражения пока не поддерживаем
                return false;
            }

            // arity = args (this не входит в arity для callobj)
            body.Add(_assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.IndexParameterNode { Name = "int", Value = args.Count }
                }));

            // Вызов базового конструктора как метода объекта: callobj "Base_ctor_1"
            var methodName = $"{baseName}_ctor_1";
            var callCmd = _assembler.Emit(Opcodes.CallObj, null);
            callCmd.Operand1 = methodName;
            body.Add(callCmd);

            // pop (результат конструктора, если есть)
            body.Add(_assembler.Emit(Opcodes.Pop, null));

            return true;
        }

        /// <summary>
        /// Понижает вызов базового метода в методе дочернего типа:
        /// Shape.Draw(); -> push this; push 0; call {nsPrefix}Shape_Draw; pop
        /// </summary>
        private bool TryLowerBaseMethodCall(
            string rawLine,
            StatementLoweringCompiler compiler,
            ExecutionBlock body,
            string nsPrefix)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                return false;

            if (!line.EndsWith(";", StringComparison.Ordinal))
                return false;
            line = line.Substring(0, line.Length - 1).Trim();

            // Ожидаем "BaseType.Method(args...)"
            var dotIdx = line.IndexOf('.');
            if (dotIdx <= 0)
                return false;

            var baseType = line.Substring(0, dotIdx).Trim();
            var rest = line.Substring(dotIdx + 1).Trim();

            var parenIdx = rest.IndexOf('(');
            var lastParenIdx = rest.LastIndexOf(')');
            if (parenIdx <= 0 || lastParenIdx <= parenIdx)
                return false;

            var methodName = rest.Substring(0, parenIdx).Trim();
            var argsText = rest.Substring(parenIdx + 1, lastParenIdx - parenIdx - 1).Trim();

            // Нужен _this
            if (!compiler.TryResolveLocalSlot("_this", out var thisPhys, out var thisLog))
                return false;

            // Разбор аргументов (простые имена/литералы)
            var args = new List<string>();
            if (!string.IsNullOrEmpty(argsText))
            {
                var depth = 0;
                var sb = new System.Text.StringBuilder();
                foreach (var ch in argsText)
                {
                    if (ch == ',' && depth == 0)
                    {
                        var arg = sb.ToString().Trim();
                        if (!string.IsNullOrEmpty(arg))
                            args.Add(arg);
                        sb.Clear();
                        continue;
                    }
                    if (ch is '(' or '{' or '[') depth++;
                    if (ch is ')' or '}' or ']') depth--;
                    sb.Append(ch);
                }
                var last = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(last))
                    args.Add(last);
            }

            // push this
            body.Add(_assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = thisPhys,
                        LogicalIndex = thisLog
                    }
                }));

            // push args
            foreach (var argExpr in args)
            {
                var ae = argExpr.Trim();
                if (ae.Length == 0)
                    continue;

                if (compiler.TryResolveLocalSlot(ae, out var phys, out var log))
                {
                    body.Add(_assembler.Emit(Opcodes.Push,
                        new List<ParameterNode>
                        {
                            new Ast.MemoryParameterNode
                            {
                                Name = "index",
                                Index = phys,
                                LogicalIndex = log
                            }
                        }));
                    continue;
                }

                if (int.TryParse(ae, out var intVal))
                {
                    body.Add(_assembler.Emit(Opcodes.Push,
                        new List<ParameterNode>
                        {
                            new Ast.IndexParameterNode { Name = "int", Value = intVal }
                        }));
                    continue;
                }

                if (ae.Length >= 2 && ae[0] == '"' && ae[^1] == '"')
                {
                    var s = ae.Substring(1, ae.Length - 2);
                    body.Add(_assembler.Emit(Opcodes.Push,
                        new List<ParameterNode>
                        {
                            new Ast.StringParameterNode { Name = "string", Value = s }
                        }));
                    continue;
                }

                return false;
            }

            // arity = args (this не входит в arity для callobj)
            body.Add(_assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.IndexParameterNode { Name = "int", Value = args.Count }
                }));

            // Вызов базового метода как метода объекта: callobj "Base_Method"
            var objMethodName = $"{baseType}_{methodName}";
            var callObjCmd = _assembler.Emit(Opcodes.CallObj, null);
            callObjCmd.Operand1 = objMethodName;
            body.Add(callObjCmd);

            // pop возможного возвращаемого значения
            body.Add(_assembler.Emit(Opcodes.Pop, null));

            return true;
        }

        private IEnumerable<InstructionNode> LowerToInstructions(IEnumerable<AstNode> bodyNodes, bool registerGlobals = false)
            => LowerToInstructions(bodyNodes, _statementLoweringCompiler, registerGlobals);

        private IEnumerable<InstructionNode> LowerToInstructions(IEnumerable<AstNode> bodyNodes, StatementLoweringCompiler compiler, bool registerGlobals = false)
        {
            var instructions = new List<InstructionNode>();
            var statementLines = new List<(string Text, int SourceLine)>();

            foreach (var node in bodyNodes)
            {
                if (node is StatementLineNode statementLineNode)
                {
                    statementLines.Add((statementLineNode.Text, statementLineNode.SourceLine));
                    continue;
                }

                FlushStatementLines(statementLines, instructions, compiler, registerGlobals);

                if (node is NestedFunctionNode)
                    throw new CompilationException(
                        "Nested 'function' / 'procedure' is only allowed inside a procedure or function body (not entrypoint asm).", -1);

                if (node is InstructionNode instructionNode)
                {
                    instructions.Add(instructionNode);
                }
            }

            FlushStatementLines(statementLines, instructions, compiler, registerGlobals);
            return instructions;
        }

        private void FlushStatementLines(List<string> statementLines, List<InstructionNode> instructions, bool registerGlobals = false)
        {
            var wrapped = statementLines.Select(t => (Text: t, SourceLine: 0)).ToList();
            FlushStatementLines(wrapped, instructions, _statementLoweringCompiler, registerGlobals);
        }

        private static void FlushStatementLines(List<(string Text, int SourceLine)> statementLines, List<InstructionNode> instructions, StatementLoweringCompiler compiler, bool registerGlobals = false)
        {
            if (statementLines.Count == 0)
                return;

            // По одной AGI-строке за раз — иначе весь блок получал SourceLine только первой строки и ломались breakpoints.
            foreach (var (text, srcLine) in statementLines)
            {
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var lowered = compiler.Lower(new[] { text }, registerGlobals, srcLine);
                if (srcLine > 0)
                {
                    foreach (var inst in lowered)
                    {
                        if (inst.SourceLine <= 0)
                            inst.SourceLine = srcLine;
                    }
                }

                instructions.AddRange(lowered);
            }

            statementLines.Clear();
        }

        private Opcodes MapOpcode(string opcode)
        {
            return opcode.ToLower() switch
            {
                "addvertex" => Opcodes.AddVertex,
                "addrelation" => Opcodes.AddRelation,
                "addshape" => Opcodes.AddShape,
                "call" => Opcodes.Call,
                "acall" => Opcodes.ACall,
                "push" => Opcodes.Push,
                "pop" => Opcodes.Pop,
                "syscall" => Opcodes.SysCall,
                "ret" => Opcodes.Ret,
                "move" => Opcodes.Move,
                "getvertex" => Opcodes.GetVertex,
                "def" => Opcodes.Def,
                "defobj" => Opcodes.DefObj,
                "defgen" => Opcodes.DefGen,
                "callobj" => Opcodes.CallObj,
                "awaitobj" => Opcodes.AwaitObj,
                "streamwaitobj" => Opcodes.StreamWaitObj,
                "await" => Opcodes.Await,
                "label" => Opcodes.Label,
                "cmp" => Opcodes.Cmp,
                "je" => Opcodes.Je,
                "jmp" => Opcodes.Jmp,
                "getobj" => Opcodes.GetObj,
                "setobj" => Opcodes.SetObj,
                "streamwait" => Opcodes.StreamWait,
                "expr" => Opcodes.Expr,
                "defexpr" => Opcodes.DefExpr,
                "lambda" => Opcodes.Lambda,
                "equals" => Opcodes.Equals,
                "not" => Opcodes.Not,
                "lt" => Opcodes.Lt,
                "add" => Opcodes.Add,
                "sub" => Opcodes.Sub,
                "mul" => Opcodes.Mul,
                "div" => Opcodes.Div,
                "pow" => Opcodes.Pow,
                _ => Opcodes.Nop
            };
        }

        /// <summary>
        /// Тело процедуры: вложенные <c>function</c> → <see cref="AnalyzedProgram.Functions"/>, вложенные <c>procedure</c> → <see cref="AnalyzedProgram.Procedures"/>,
        /// имена <c>Parent_nestedName</c>; вызовы резолвятся через <see cref="StatementLoweringCompiler.SetNestedCallScope"/>.
        /// </summary>
        private void CompileProcedureBodyWithNestedFunctions(
            ProcedureNode procNode,
            Processor.Procedure procedure,
            StatementLoweringCompiler procCompiler,
            AnalyzedProgram result)
        {
            var siblings = procNode.Body.Statements.OfType<NestedFunctionNode>().ToList();
            var siblingMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in siblings)
                siblingMap[s.Name] = procNode.Name + "_" + s.Name;

            NestedCallScope? myScope = null;
            if (siblingMap.Count > 0)
            {
                myScope = new NestedCallScope(null, siblingMap);
                procCompiler.SetNestedCallScope(myScope);
                foreach (var s in siblings)
                {
                    CompileSubprogramAst(
                        siblingMap[s.Name],
                        s.Parameters,
                        s.Body,
                        result,
                        procCompiler,
                        myScope,
                        asProcedure: s.IsProcedure);
                }
            }

            var bodyWithoutNested = procNode.Body.Statements.Where(n => n is not NestedFunctionNode).ToList();
            foreach (var instructionNode in LowerToInstructions(bodyWithoutNested, procCompiler))
                AddAnalyzedCommand(procedure.Body, instructionNode);
        }

        /// <summary>
        /// Компилирует одну функцию (топ-уровень или вложенную как <c>function</c>). Подпрограммы получают свои memory-слоты после слотов родителя.
        /// </summary>
        private void CompileFunctionAst(
            string mangleName,
            List<string> paramNames,
            BodyNode body,
            AnalyzedProgram result,
            StatementLoweringCompiler slotParent,
            NestedCallScope? enclosingCallScope)
        {
            CompileSubprogramAst(mangleName, paramNames, body, result, slotParent, enclosingCallScope, asProcedure: false);
        }

        /// <summary>
        /// Вложенная или топ-уровневая подпрограмма: <paramref name="asProcedure"/> выбирает словарь <see cref="AnalyzedProgram.Procedures"/> vs <see cref="AnalyzedProgram.Functions"/>.
        /// </summary>
        private void CompileSubprogramAst(
            string mangleName,
            List<string> paramNames,
            BodyNode body,
            AnalyzedProgram result,
            StatementLoweringCompiler slotParent,
            NestedCallScope? enclosingCallScope,
            bool asProcedure)
        {
            var siblings = body.Statements.OfType<NestedFunctionNode>().ToList();
            var siblingMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in siblings)
                siblingMap[s.Name] = mangleName + "_" + s.Name;

            var myScope = siblingMap.Count > 0
                ? new NestedCallScope(enclosingCallScope, siblingMap)
                : enclosingCallScope;

            var funcCompiler = new StatementLoweringCompiler();
            funcCompiler.InheritGlobalSlots(slotParent);
            funcCompiler.BeginStatementSequence();
            funcCompiler.ResetLocals();
            funcCompiler.SetNestedCallScope(myScope);

            var execBlock = new ExecutionBlock();

            if (paramNames.Count > 0)
            {
                execBlock.Add(_assembler.Emit(Opcodes.Pop, null));
                for (var pi = paramNames.Count - 1; pi >= 0; pi--)
                {
                    var paramName = paramNames[pi];
                    var slot = funcCompiler.AllocateLocalSlot(paramName);
                    var logicalIndex = pi; // logical 0-based per unit
                    execBlock.Add(_assembler.Emit(Opcodes.Pop,
                        new List<ParameterNode>
                        {
                            new Ast.MemoryParameterNode
                            {
                                Name = "index",
                                Index = slot,
                                LogicalIndex = logicalIndex
                            }
                        }));
                }
            }

            foreach (var s in siblings)
            {
                CompileSubprogramAst(
                    siblingMap[s.Name],
                    s.Parameters,
                    s.Body,
                    result,
                    funcCompiler,
                    myScope,
                    asProcedure: s.IsProcedure);
            }

            var bodyWithoutNested = body.Statements.Where(n => n is not NestedFunctionNode).ToList();
            foreach (var instructionNode in LowerToInstructions(bodyWithoutNested, funcCompiler))
                AddAnalyzedCommand(execBlock, instructionNode);

            if (execBlock.Count == 0 || execBlock[execBlock.Count - 1].Opcode != Opcodes.Ret)
                execBlock.Add(_assembler.Emit(Opcodes.Ret, null));

            if (asProcedure)
                result.Procedures[mangleName] = new Processor.Procedure { Name = mangleName, Body = execBlock };
            else
                result.Functions[mangleName] = new Processor.Function { Name = mangleName, Body = execBlock };

            slotParent.InheritGlobalSlots(funcCompiler);
        }

        /// <summary>
        /// Разбирает список строк метода типа/класса (constructor/method внутри Point: type { ... })
        /// на список имён параметров, спеки типов (параллельно именам, до вставки <c>_this</c>) и тело.
        /// </summary>
        private static void ExtractTypeMethodSignatureAndBody(
            List<string> bodyLines,
            out List<string> paramNames,
            out List<string?> paramTypeSpecs,
            out List<string> bodyStatements)
        {
            paramNames = new List<string>();
            paramTypeSpecs = new List<string?>();
            bodyStatements = new List<string>();
            if (bodyLines == null || bodyLines.Count == 0)
                return;

            var header = (bodyLines[0] ?? string.Empty).Trim();
            if (header.Length == 0)
                return;

            // Обрезаем ключевое слово constructor/method.
            var rest = header;
            var isCtor = false;
            if (rest.StartsWith("constructor ", StringComparison.OrdinalIgnoreCase))
            {
                rest = rest.Substring("constructor".Length).TrimStart();
                isCtor = true;
            }
            else if (rest.StartsWith("method ", StringComparison.OrdinalIgnoreCase))
            {
                rest = rest.Substring("method".Length).TrimStart();
            }

            if (TryExtractParenthesizedParameterList(rest, out var insideParens))
                ParseAgiFormalParameterList(insideParens, paramNames, paramTypeSpecs);

            // Для конструкторов и методов типов первый скрытый параметр — this.
            if (isCtor || header.StartsWith("method ", StringComparison.OrdinalIgnoreCase))
            {
                paramNames.Insert(0, "_this");
                paramTypeSpecs.Insert(0, null);
            }

            // Тело: все строки после заголовка, включая «}» (нужны для switch/if с многострочными «{» при CoalesceMultilineStatements).
            // Одиночные «}» в CompileStatementLines по-прежнему пропускаются.
            for (var i = 1; i < bodyLines.Count; i++)
                bodyStatements.Add(bodyLines[i] ?? string.Empty);
        }

        private static bool TryExtractParenthesizedParameterList(string textAfterKeywordAndName, out string insideParens)
        {
            insideParens = "";
            var open = textAfterKeywordAndName.IndexOf('(');
            if (open < 0)
                return false;
            var depth = 0;
            for (var j = open; j < textAfterKeywordAndName.Length; j++)
            {
                var c = textAfterKeywordAndName[j];
                if (c == '(') depth++;
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        insideParens = textAfterKeywordAndName.Substring(open + 1, j - open - 1);
                        return true;
                    }
                }
            }

            return false;
        }

        private static void ParseAgiFormalParameterList(string insideParens, List<string> names, List<string?> types)
        {
            if (string.IsNullOrWhiteSpace(insideParens))
                return;
            var s = insideParens;
            var idx = 0;
            while (idx < s.Length)
            {
                while (idx < s.Length && char.IsWhiteSpace(s[idx])) idx++;
                if (idx >= s.Length) break;

                var start = idx;
                while (idx < s.Length &&
                       !char.IsWhiteSpace(s[idx]) &&
                       s[idx] != ':' &&
                       s[idx] != ',')
                    idx++;

                var pname = start < idx ? s.Substring(start, idx - start).Trim() : "";
                if (string.IsNullOrEmpty(pname))
                    break;
                names.Add(pname);

                while (idx < s.Length && char.IsWhiteSpace(s[idx])) idx++;

                string? typeSpec = null;
                if (idx < s.Length && s[idx] == ':')
                {
                    idx++;
                    while (idx < s.Length && char.IsWhiteSpace(s[idx])) idx++;
                    var tStart = idx;
                    var depthAngle = 0;
                    while (idx < s.Length)
                    {
                        var c = s[idx];
                        if (c == '<') depthAngle++;
                        else if (c == '>' && depthAngle > 0) depthAngle--;
                        else if (depthAngle == 0 && c == ',')
                            break;
                        idx++;
                    }

                    typeSpec = s.Substring(tStart, idx - tStart).Trim();
                }

                types.Add(typeSpec);

                while (idx < s.Length && char.IsWhiteSpace(s[idx])) idx++;
                if (idx < s.Length && s[idx] == ',')
                {
                    idx++;
                    continue;
                }

                break;
            }
        }

        private static bool IsBuiltinParameterTypeSpec(string spec)
        {
            var t = (spec ?? "").Trim();
            if (t.Length == 0) return true;
            if (t.IndexOf('<') >= 0)
            {
                var baseName = t.Substring(0, t.IndexOf('<')).Trim();
                return IsBuiltinParameterTypeSpec(baseName);
            }

            return t.Equals("int", StringComparison.OrdinalIgnoreCase) ||
                   t.Equals("long", StringComparison.OrdinalIgnoreCase) ||
                   t.Equals("string", StringComparison.OrdinalIgnoreCase) ||
                   t.Equals("bool", StringComparison.OrdinalIgnoreCase) ||
                   t.Equals("float", StringComparison.OrdinalIgnoreCase) ||
                   t.Equals("double", StringComparison.OrdinalIgnoreCase) ||
                   t.Equals("decimal", StringComparison.OrdinalIgnoreCase) ||
                   t.Equals("void", StringComparison.OrdinalIgnoreCase) ||
                   t.Equals("any", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, Dictionary<string, List<(string MangledProcedure, string? FirstParamTypeShort)>>> BuildInstanceMethodOverloadTable(
            IReadOnlyList<(string HostTypeFq, string MangledProcedure, List<string> BodyLines, Dictionary<string, string> FieldSpecs)> typeMethods)
        {
            var root = new Dictionary<string, Dictionary<string, List<(string, string?)>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (hostFq, mangled, lines, _) in typeMethods)
            {
                if (lines == null || lines.Count == 0 ||
                    mangled.IndexOf("_ctor_", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                var hdr = (lines[0] ?? "").Trim();
                if (!hdr.StartsWith("method ", StringComparison.OrdinalIgnoreCase))
                    continue;

                var rest = hdr.Substring("method".Length).TrimStart();
                if (!TryExtractParenthesizedParameterList(rest, out var inside))
                    continue;

                var rawEnd = 0;
                while (rawEnd < rest.Length && !char.IsWhiteSpace(rest[rawEnd]) && rest[rawEnd] != '(')
                    rawEnd++;
                var rawMethod = rest.Substring(0, rawEnd).Trim();
                if (string.IsNullOrEmpty(rawMethod))
                    continue;

                var pNames = new List<string>();
                var pTypes = new List<string?>();
                ParseAgiFormalParameterList(inside, pNames, pTypes);
                var p0 = pTypes.Count > 0 ? pTypes[0] : null;

                if (!root.TryGetValue(hostFq.Trim(), out var byMethod))
                {
                    byMethod = new Dictionary<string, List<(string, string?)>>(StringComparer.OrdinalIgnoreCase);
                    root[hostFq.Trim()] = byMethod;
                }

                if (!byMethod.TryGetValue(rawMethod, out var list))
                {
                    list = new List<(string, string?)>();
                    byMethod[rawMethod] = list;
                }

                list.Add((mangled, p0));
            }

            return root;
        }

        private bool TryLowerFieldAssign(
            string rawLine,
            HashSet<string> fields,
            StatementLoweringCompiler compiler,
            ExecutionBlock body)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line))
                return false;

            var idx = line.IndexOf(":=", StringComparison.Ordinal);
            if (idx <= 0)
                return false;

            var left = line.Substring(0, idx).Trim();
            if (!fields.Contains(left))
                return false;

            var rhs = line.Substring(idx + 2).Trim().TrimEnd(';').Trim();
            if (rhs.Length == 0)
                return false;

            // Пока поддерживаем только простой случай: RHS — имя параметра/локальной переменной,
            // без сложных выражений. Для ctor Point(x,y): "X:= x;" / "Y:= y;".
            if (!compiler.TryResolveLocalSlot(rhs, out var rhsPhysical, out var rhsLogical))
                return false;
            if (!compiler.TryResolveLocalSlot("_this", out var thisPhysical, out var thisLogical))
                return false;

            // push this
            // после того как нашли left, rhs и слоты this/rhs:

            // push this
            body.Add(_assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = thisPhysical,
                        LogicalIndex = thisLogical
                    }
                }));

            // push field name (string: "X" / "Y")
            body.Add(_assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.StringParameterNode
                    {
                        Name = "string",
                        Value = left
                    }
                }));

            // push rhs (значение параметра/локала)
            body.Add(_assembler.Emit(Opcodes.Push,
                new List<ParameterNode>
                {
                    new Ast.MemoryParameterNode
                    {
                        Name = "index",
                        Index = rhsPhysical,
                        LogicalIndex = rhsLogical
                    }
                }));

            // setobj (Operand1 можно оставить для красоты AGIASM, но рантайм его не использует)
            var set = _assembler.Emit(Opcodes.SetObj, null);
            // Operand1 не трогаем, чтобы AGIASM показывал просто "setobj"
            body.Add(set);
            body.Add(_assembler.Emit(Opcodes.Pop, null));

            return true;
        }
    }
}
