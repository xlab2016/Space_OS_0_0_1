using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Magic.Kernel.Compilation.Ast;
using Magic.Kernel.Processor;

namespace Magic.Kernel.Compilation
{
    internal sealed partial class StatementLoweringCompiler
    {
        // ─── main statement dispatcher ───────────────────────────────────────────────

        private List<InstructionNode> CompileStatementLines(
            IEnumerable<string> sourceLines,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int vertexCounter,
            ref int relationCounter,
            ref int shapeCounter,
            ref int memorySlotCounter,
            ref int streamLoopCounter,
            int outerStatementSourceLine = 0)
        {
            var instructions = new List<InstructionNode>();
            var inVarBlock = false;
            var varBlockLines = new List<string>();

            foreach (var rawLine in CoalesceMultilineStatements(sourceLines).SelectMany(SplitLeadingForStatementWithTail))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line) || line == "{" || line == "}")
                    continue;

                // Vault‑переменная внутри тела процедуры: обрабатываем так же, как
                // в var‑блоке, чтобы не требовать отдельного блока только ради vault.
                if (TryParseVaultDeclaration(line, out var vaultName))
                {
                    EmitVaultDeclaration(vaultName, vars, ref memorySlotCounter, instructions);
                    continue;
                }

                // 1. Декларации schema/типа/класса на верхнем уровне (Prelude).
                //    Сначала table/database: иначе `Messages<> : table {` ошибочно разберётся как класс с базой "table".
                //      - Name: table {...}
                //      - Name: database {...}
                //      - Point: type {...}
                //      - Shape: class {...}
                //      - Circle: Shape {...}
                if (TryCompileSchemaDeclaration(line, vars, ref memorySlotCounter, instructions))
                    continue;

                if (TryCompileTypeOrClassDeclaration(line, vars, ref memorySlotCounter, instructions))
                    continue;

                if (TryParseVarKeywordOnly(line))
                {
                    inVarBlock = true;
                    varBlockLines.Clear();
                    continue;
                }

                if (TryStripInlineVarPrefix(line, out var inlineDecl) && IsDeclarationStatement(inlineDecl))
                {
                    if (!inVarBlock)
                        varBlockLines.Clear();
                    inVarBlock = true;
                    varBlockLines.Add(inlineDecl);
                    continue;
                }

                if (inVarBlock)
                {
                    if (IsDeclarationStatement(line))
                    {
                        varBlockLines.Add(line);
                        continue;
                    }

                    inVarBlock = false;
                    instructions.AddRange(CompileVarBlock(varBlockLines, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter));
                }

                if (IsDeclarationStatement(line))
                {
                    instructions.AddRange(CompileVarBlock(new List<string> { line }, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter));
                    continue;
                }

                if (TryCompileReturnStatement(line, vars, ref memorySlotCounter, instructions))
                    continue;

                if (TryCompileIfStatement(line, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter, instructions))
                    continue;

                if (TryCompileSwitchStatement(line, outerStatementSourceLine, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter, instructions))
                    continue;

                if (TryCompileStreamWaitForLoop(line, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter, instructions, outerStatementSourceLine))
                    continue;

                if (TryCompileForStatement(line, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter, instructions))
                    continue;

                if (TryCompilePlusAssign(line, vars, ref memorySlotCounter, instructions))
                    continue;

                if (TryCompileMultiplyAssign(line, vars, ref memorySlotCounter, instructions))
                    continue;

                if (TryCompileMemberAssignment(line, vars, ref memorySlotCounter, instructions))
                    continue;

                if (TryCompileMethodCall(line, vars, ref memorySlotCounter, instructions))
                    continue;

                if (TryCompileAssignment(line, vars, ref shapeCounter, ref vertexCounter, ref memorySlotCounter, instructions))
                    continue;

                if (TryCompileAwaitStatement(line, vars, instructions))
                    continue;

                if (TryCompileFunctionCall(line, vars, ref memorySlotCounter, instructions))
                    continue;

                if (TryParseProcedureName(line, out var procName))
                    instructions.Add(CreateCallInstruction(procName));
            }

            if (inVarBlock && varBlockLines.Count > 0)
                instructions.AddRange(CompileVarBlock(varBlockLines, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter));

            return instructions;
        }

        /// <summary>
        /// When a single <see cref="StatementLineNode"/> contains a C-style <c>for (...)</c> <b>and</b> further
        /// statements after the loop's closing <c>}</c>, split so <c>TryCompileForStatement</c> runs on the loop only.
        /// </summary>
        private static IEnumerable<string> SplitLeadingForStatementWithTail(string rawLine)
        {
            var trimmed = (rawLine ?? string.Empty).Trim();
            if (trimmed.Length < 5 ||
                !trimmed.StartsWith("for", StringComparison.OrdinalIgnoreCase) ||
                (trimmed.Length > 3 && char.IsLetterOrDigit(trimmed[3])))
            {
                yield return rawLine;
                yield break;
            }

            if (!TryParseForStatement(trimmed, out _, out _, out _, out _, out var endExclusive) ||
                endExclusive >= trimmed.Length)
            {
                yield return rawLine;
                yield break;
            }

            var head = trimmed.Substring(0, endExclusive).TrimEnd();
            var tail = trimmed.Substring(endExclusive).TrimStart();
            if (tail.Length == 0)
            {
                yield return rawLine;
                yield break;
            }

            yield return head;
            yield return tail;
        }

        // ─── streamwait loop ────────────────────────────────────────────────────────

        /// <summary>
        /// Слоты памяти &lt; <paramref name="exclusiveUpperSlot"/> из тела цикла (кроме aggregate/delta),
        /// которые нужно продублировать в новый кадр через стек перед acall/call на streamwait_loop_*_delta.
        /// </summary>
        private static long[] CollectStreamWaitLoopCaptureToSlots(
            List<InstructionNode> bodyInstructions,
            long exclusiveUpperSlot,
            long deltaSlot,
            long aggregateSlot)
        {
            var set = new HashSet<long>();

            void ConsiderLocalSlot(long index)
            {
                if (index >= exclusiveUpperSlot)
                    return;
                if (index == deltaSlot || index == aggregateSlot)
                    return;
                set.Add(index);
            }

            void VisitParameter(ParameterNode? node)
            {
                if (node == null)
                    return;
                switch (node)
                {
                    case MemoryParameterNode mem when !string.Equals(mem.Name, "global", StringComparison.OrdinalIgnoreCase):
                        ConsiderLocalSlot(mem.Index);
                        break;
                    case FunctionParameterNode fp when string.Equals(fp.EntityType, "memory", StringComparison.OrdinalIgnoreCase):
                        ConsiderLocalSlot(fp.Index);
                        break;
                }
            }

            foreach (var insn in bodyInstructions)
            {
                foreach (var p in insn.Parameters)
                    VisitParameter(p);
            }

            return set.OrderBy(x => x).ToArray();
        }

        private bool TryCompileStreamWaitForLoop(
            string line,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int vertexCounter,
            ref int relationCounter,
            ref int shapeCounter,
            ref int memorySlotCounter,
            ref int streamLoopCounter,
            List<InstructionNode> instructions,
            int outerStatementSourceLine = 0)
        {
            if (!TryParseStreamWaitForLoop(line, out var isSync, out var streamName, out var waitType, out var deltaVarName, out var aggregateVarName, out var bodyText, out var openBraceIndex))
                return false;

            if (!vars.TryGetValue(streamName, out var streamVar))
                throw new UndeclaredVariableException(streamName);
            if (streamVar.Kind != "stream" && streamVar.Kind != "def" && streamVar.Kind != "memory" && streamVar.Kind != "history")
                return true;

            var loopCounter = ++streamLoopCounter;
            var loopStartLabel = $"streamwait_loop_{loopCounter}";
            var loopEndLabel = $"streamwait_loop_{loopCounter}_end";
            var loopBodyLabel = $"streamwait_loop_{loopCounter}_delta";

            var endSlot = memorySlotCounter++;
            var deltaSlot = memorySlotCounter++;
            var aggregateSlot = memorySlotCounter++;
            vars[deltaVarName] = ("memory", deltaSlot);
            if (!string.IsNullOrWhiteSpace(aggregateVarName))
                vars[aggregateVarName] = ("memory", aggregateSlot);

            instructions.Add(CreateLabelInstruction(loopStartLabel));
            instructions.Add(CreatePushMemoryInstruction(streamVar.Index));
            instructions.Add(CreatePushStringInstruction(waitType));
            instructions.Add(new InstructionNode { Opcode = "streamwaitobj" });
            // Только end в память родительского кадра (cmp до call). Delta/aggregate остаются на стеке:
            // call/acall на метку делает PushLocalScope — слоты, заполненные до вызова, не видны телу.
            instructions.Add(CreatePopMemoryInstruction(endSlot));
            instructions.Add(CreateCmpInstruction(endSlot, 1));
            instructions.Add(CreateJumpIfEqualInstruction(loopEndLabel));

            var bodyCompileSlotUpperBound = memorySlotCounter;
            var bodyInstructions = CompileStreamWaitLoopBodyInstructions(
                bodyText,
                line,
                openBraceIndex,
                outerStatementSourceLine,
                vars,
                ref vertexCounter,
                ref relationCounter,
                ref shapeCounter,
                ref memorySlotCounter,
                ref streamLoopCounter);
            var captureToSlots = CollectStreamWaitLoopCaptureToSlots(bodyInstructions, bodyCompileSlotUpperBound, deltaSlot, aggregateSlot);
            var useSyncLoop = isSync || string.Equals(streamVar.Kind, "history", StringComparison.OrdinalIgnoreCase);
            // Конвенция call/acall: сверху arity, ниже аргументы: aggregate, delta, затем захваченные слоты родителя.
            foreach (var capSlot in captureToSlots)
                instructions.Add(CreatePushMemoryInstruction(capSlot));
            instructions.Add(CreatePushIntInstruction(2 + captureToSlots.Length));
            instructions.Add(CreateStreamWaitLoopDeltaCallInstruction(loopBodyLabel, async: !useSyncLoop, aggregateSlot, deltaSlot, captureToSlots));
            instructions.Add(CreateJumpInstruction(loopStartLabel));
            instructions.Add(CreateLabelInstruction(loopBodyLabel));
            instructions.AddRange(bodyInstructions);
            instructions.Add(new InstructionNode { Opcode = "ret" });
            instructions.Add(CreateLabelInstruction(loopEndLabel));
            // je сюда при IsEnd: delta/aggregate так и лежат на стеке (тело не вызывалось). Иначе следующий
            // streamwait/def с arity на вершине увидит DeltaWeakDisposable и упадёт.
            instructions.Add(CreatePopInstruction());
            instructions.Add(CreatePopInstruction());
            return true;
        }

        /// <summary>
        /// Тело <c>for streamwait</c> часто в одном <see cref="StatementLineNode"/> с заголовком (сохранение переводов из‑за <c>//</c> внутри блока).
        /// Без отдельных <see cref="InstructionNode.SourceLine"/> на строки тела все инструкции получают строку заголовка и AGI breakpoints внутри цикла не срабатывают.
        /// </summary>
        private List<InstructionNode> CompileStreamWaitLoopBodyInstructions(
            string bodyText,
            string fullLoopText,
            int openBraceIndex,
            int outerStatementSourceLine,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int vertexCounter,
            ref int relationCounter,
            ref int shapeCounter,
            ref int memorySlotCounter,
            ref int streamLoopCounter)
        {
            var bodyLineChunks = SplitStatementLines(bodyText);
            var bodyInstructions = new List<InstructionNode>();
            var bodyFirstSourceLine = outerStatementSourceLine > 0 && openBraceIndex >= 0
                ? outerStatementSourceLine + CountNewlinesInRange(fullLoopText, 0, openBraceIndex + 1)
                : 0;
            var searchPos = 0;
            foreach (var rawLine in CoalesceMultilineStatements(bodyLineChunks).SelectMany(SplitLeadingForStatementWithTail))
            {
                var stmtText = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(stmtText) || stmtText == "{" || stmtText == "}")
                    continue;

                var idx = bodyText.IndexOf(stmtText, searchPos, StringComparison.Ordinal);
                if (idx < 0)
                    idx = bodyText.IndexOf(stmtText, 0, StringComparison.Ordinal);
                var srcLine = bodyFirstSourceLine > 0 && idx >= 0
                    ? bodyFirstSourceLine + CountNewlinesInRange(bodyText, 0, idx)
                    : 0;
                if (idx >= 0)
                    searchPos = idx + Math.Max(1, stmtText.Length);

                var chunk = CompileStatementLines(
                    new[] { rawLine },
                    vars,
                    ref vertexCounter,
                    ref relationCounter,
                    ref shapeCounter,
                    ref memorySlotCounter,
                    ref streamLoopCounter,
                    srcLine);
                if (srcLine > 0)
                {
                    foreach (var inst in chunk)
                    {
                        if (inst.SourceLine <= 0)
                            inst.SourceLine = srcLine;
                    }
                }

                bodyInstructions.AddRange(chunk);
            }

            return bodyInstructions;
        }

        // ─── C-style for (init; cond; incr) { body } ─────────────────────────────────

        private bool TryCompileForStatement(
            string line,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int vertexCounter,
            ref int relationCounter,
            ref int shapeCounter,
            ref int memorySlotCounter,
            ref int streamLoopCounter,
            List<InstructionNode> instructions)
        {
            if (!TryParseForStatement(line, out var initText, out var condText, out var incrText, out var bodyText, out _))
                return false;

            if (string.IsNullOrWhiteSpace(condText))
                throw new CompilationException("for-loop condition is required.", -1);

            var forCounter = ++streamLoopCounter + 5000;
            var startLabel = $"for_start_{forCounter}";
            var endLabel = $"for_end_{forCounter}";

            TryCompileForInit(initText, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter, instructions);

            instructions.Add(CreateLabelInstruction(startLabel));

            var condSlot = memorySlotCounter++;
            if (!TryCompileExpressionToSlot(condText, vars, condSlot, ref memorySlotCounter, instructions))
                throw new CompilationException($"Cannot compile for-loop condition: '{condText.Trim()}'", -1);

            instructions.Add(CreateCmpInstruction(condSlot, 0L));
            instructions.Add(CreateJumpIfEqualInstruction(endLabel));

            var bodyLines = SplitStatementLines(bodyText);
            instructions.AddRange(CompileStatementLines(bodyLines, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter));

            TryCompileForIncrement(incrText, vars, ref shapeCounter, ref vertexCounter, ref memorySlotCounter, instructions);

            instructions.Add(CreateJumpInstruction(startLabel));
            instructions.Add(CreateLabelInstruction(endLabel));
            return true;
        }

        private void TryCompileForInit(
            string initText,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int vertexCounter,
            ref int relationCounter,
            ref int shapeCounter,
            ref int memorySlotCounter,
            ref int streamLoopCounter,
            List<InstructionNode> instructions)
        {
            var init = initText.Trim();
            if (string.IsNullOrEmpty(init))
                return;
            if (IsDeclarationStatement(init))
            {
                instructions.AddRange(CompileVarBlock(
                    new List<string> { init },
                    vars,
                    ref vertexCounter,
                    ref relationCounter,
                    ref shapeCounter,
                    ref memorySlotCounter));
                return;
            }
            if (TryCompileAssignment(init, vars, ref shapeCounter, ref vertexCounter, ref memorySlotCounter, instructions))
                return;
            throw new CompilationException($"Unsupported for-loop initializer: '{init}'", -1);
        }

        private void TryCompileForIncrement(
            string incrText,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int shapeCounter,
            ref int vertexCounter,
            ref int memorySlotCounter,
            List<InstructionNode> instructions)
        {
            var incr = incrText.Trim();
            if (string.IsNullOrEmpty(incr))
                return;
            if (TryParsePostIncrement(incr, out var name))
            {
                var synthetic = $"{name} := {name} + 1";
                if (!TryCompileAssignment(synthetic, vars, ref shapeCounter, ref vertexCounter, ref memorySlotCounter, instructions))
                    throw new CompilationException($"Cannot compile for-loop increment: '{incr}'", -1);
                return;
            }
            if (TryCompileAssignment(incr, vars, ref shapeCounter, ref vertexCounter, ref memorySlotCounter, instructions))
                return;
            throw new CompilationException($"Unsupported for-loop increment: '{incr}'", -1);
        }

        // ─── if/else statement ──────────────────────────────────────────────────────

        private bool TryCompileIfStatement(
            string line,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int vertexCounter,
            ref int relationCounter,
            ref int shapeCounter,
            ref int memorySlotCounter,
            ref int streamLoopCounter,
            List<InstructionNode> instructions)
        {
            if (!TryParseIfStatement(line, out var condText, out var negated, out var bodyText, out var elseText, out var elseIsBlock))
                return false;

            var ifCounter = ++streamLoopCounter + 1000;
            var elseLabel = $"if_else_{ifCounter}";
            var endLabel = $"if_end_{ifCounter}";
            var condSlot = memorySlotCounter++;

            if (!TryCompileExpressionToSlot(condText, vars, condSlot, ref memorySlotCounter, instructions))
                throw new CompilationException($"Cannot compile if condition: '{condText?.Trim()}'", -1);

            var slotToCompare = condSlot;
            if (negated)
            {
                var notSlot = memorySlotCounter++;
                instructions.Add(CreatePushMemoryInstruction(condSlot));
                instructions.Add(new InstructionNode { Opcode = "not" });
                instructions.Add(CreatePopMemoryInstruction(notSlot));
                slotToCompare = notSlot;
            }

            // Jump to else/end when the final condition value is false.
            instructions.Add(CreateCmpInstruction(slotToCompare, 0L));
            instructions.Add(CreateJumpIfEqualInstruction(string.IsNullOrWhiteSpace(elseText) ? endLabel : elseLabel));

            var bodyLines = SplitStatementLines(bodyText);
            var bodyInstructions = CompileStatementLines(bodyLines, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter);
            instructions.AddRange(bodyInstructions);

            if (!string.IsNullOrWhiteSpace(elseText))
            {
                instructions.Add(CreateJumpInstruction(endLabel));
                instructions.Add(CreateLabelInstruction(elseLabel));

                var elseLines = elseIsBlock
                    ? SplitStatementLines(elseText)
                    : new List<string> { elseText };
                var elseInstructions = CompileStatementLines(elseLines, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter);
                instructions.AddRange(elseInstructions);
            }

            instructions.Add(CreateLabelInstruction(endLabel));
            return true;
        }

        // ─── return statement ────────────────────────────────────────────────────────

        /// <summary>
        /// Compiles:
        /// - `return;` / `return`
        /// - `return <expr>;` (leaves return value on stack, then emits `ret`)
        /// </summary>
        private bool TryCompileReturnStatement(
            string line,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int memorySlotCounter,
            List<InstructionNode> instructions)
        {
            var t = line?.Trim() ?? "";
            if (!t.StartsWith("return", StringComparison.OrdinalIgnoreCase))
                return false;

            var after = t.Substring("return".Length).Trim();
            if (after.Length == 0 || after == ";")
            {
                instructions.Add(new InstructionNode { Opcode = "ret" });
                return true;
            }

            if (after.EndsWith(";", StringComparison.Ordinal))
                after = after.TrimEnd(';').Trim();

            if (string.IsNullOrWhiteSpace(after))
            {
                instructions.Add(new InstructionNode { Opcode = "ret" });
                return true;
            }

            // Compile expr into a temp slot, then push it so it stays on stack as return value.
            var valueSlot = memorySlotCounter++;
            if (!TryCompileExpressionToSlot(after, vars, valueSlot, ref memorySlotCounter, instructions))
                return false;

            instructions.Add(CreatePushMemoryInstruction(valueSlot));
            instructions.Add(new InstructionNode { Opcode = "ret" });
            return true;
        }

        // ─── switch statement ────────────────────────────────────────────────────────

        private bool TryCompileSwitchStatement(
            string line,
            int switchKeywordSourceLine,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int vertexCounter,
            ref int relationCounter,
            ref int shapeCounter,
            ref int memorySlotCounter,
            ref int streamLoopCounter,
            List<InstructionNode> instructions)
        {
            if (!TryParseSwitchStatement(line, out var switchExprs, out var bodyText, out var openBraceIndex))
                return false;

            var t = line?.Trim() ?? "";
            var bodyFirstSourceLine = switchKeywordSourceLine > 0
                ? switchKeywordSourceLine + CountNewlinesInRange(t, 0, openBraceIndex + 1)
                : 0;

            var switchCounter = ++streamLoopCounter + 2000;
            var endLabel = $"switch_end_{switchCounter}";

            // Compile each switch expression to a slot
            var exprSlots = new List<int>();
            foreach (var exprStr in switchExprs)
            {
                var slot = memorySlotCounter++;
                if (!TryCompileExpressionToSlot(exprStr, vars, slot, ref memorySlotCounter, instructions))
                    throw new CompilationException($"Cannot compile switch expression: '{exprStr}'", -1);
                exprSlots.Add(slot);
            }

            var arms = ParseSwitchArmsWithSourceLines(bodyText, bodyFirstSourceLine);

            foreach (var (matchValues, armBody, ifLine, bodyStartLine, typePatSubject, typePatType, typePatBinding) in arms)
            {
                var armCounter = ++streamLoopCounter + 3000;
                var armEndLabel = $"switch_arm_end_{armCounter}";

                if (!string.IsNullOrEmpty(typePatSubject))
                {
                    if (switchExprs.Count != 1)
                        throw new CompilationException("Type-pattern switch arms require a single switch expression.", -1);
                    var swExpr = switchExprs[0].Trim();
                    if (!string.Equals(swExpr, typePatSubject, StringComparison.OrdinalIgnoreCase))
                        throw new CompilationException(
                            $"switch type pattern: subject '{typePatSubject}' must match switch expression '{swExpr}'.",
                            -1);

                    var exprSlot = exprSlots[0];
                    var resultSlot = memorySlotCounter++;
                    var bindingSlot = memorySlotCounter++;
                    var matchLabel = $"switch_arm_{armCounter}";
                    var afterArmLabel = $"switch_after_{armCounter}";
                    var cmpStart = instructions.Count;
                    instructions.Add(CreatePushMemoryInstruction(exprSlot));
                    instructions.Add(CreatePushClassInstruction(QualifyTypeNameForDefObj(typePatType!)));
                    instructions.Add(CreatePushIntInstruction(2));
                    instructions.Add(CreateCallInstruction("is"));
                    // is: push binding value, then 1/0 on top → pop result, then binding
                    instructions.Add(CreatePopMemoryInstruction(resultSlot));
                    instructions.Add(CreatePopMemoryInstruction(bindingSlot));
                    instructions.Add(CreateCmpInstruction(resultSlot, 1L));
                    instructions.Add(CreateJumpIfEqualInstruction(matchLabel));
                    instructions.Add(CreateJumpInstruction(afterArmLabel));
                    instructions.Add(CreateLabelInstruction(matchLabel));

                    ApplySourceLineToNewInstructions(instructions, cmpStart, ifLine);

                    var bind = typePatBinding!.Trim();
                    RegisterPersistentVarDefType(bind, QualifyTypeNameForDefObj(typePatType!));
                    try
                    {
                        vars[bind] = ("memory", bindingSlot);
                        CompileSwitchArmBodyWithSourceLines(armBody, ifLine, bodyStartLine, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter, instructions);
                    }
                    finally
                    {
                        UnregisterPersistentVarDefType(bind);
                        vars.Remove(bind);
                    }

                    instructions.Add(CreateJumpInstruction(endLabel));
                    instructions.Add(CreateLabelInstruction(afterArmLabel));
                    continue;
                }

                if (matchValues.Count == 0)
                {
                    CompileSwitchArmBodyWithSourceLines(armBody, ifLine, bodyStartLine, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter, instructions);
                    instructions.Add(CreateJumpInstruction(endLabel));
                    instructions.Add(CreateLabelInstruction(armEndLabel));
                    continue;
                }

                var compareCount = Math.Min(exprSlots.Count, matchValues.Count);
                var cmpStart2 = instructions.Count;
                for (var ci = 0; ci < compareCount; ci++)
                {
                    var valSlot = memorySlotCounter++;
                    var cmpSlot = memorySlotCounter++;
                    instructions.Add(CreatePushStringInstruction(matchValues[ci]));
                    instructions.Add(CreatePopMemoryInstruction(valSlot));
                    instructions.Add(CreatePushMemoryInstruction(exprSlots[ci]));
                    instructions.Add(CreatePushMemoryInstruction(valSlot));
                    instructions.Add(new InstructionNode { Opcode = "equals" });
                    instructions.Add(CreatePopMemoryInstruction(cmpSlot));
                    instructions.Add(CreateCmpInstruction(cmpSlot, 0L));
                    instructions.Add(CreateJumpIfEqualInstruction(armEndLabel));
                }

                ApplySourceLineToNewInstructions(instructions, cmpStart2, ifLine);

                CompileSwitchArmBodyWithSourceLines(armBody, ifLine, bodyStartLine, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter, instructions);
                instructions.Add(CreateJumpInstruction(endLabel));
                instructions.Add(CreateLabelInstruction(armEndLabel));
            }

            instructions.Add(CreateLabelInstruction(endLabel));
            return true;
        }

        private static int CountNewlinesInRange(string s, int start, int endExclusive)
        {
            var n = 0;
            var hi = Math.Min(endExclusive, s.Length);
            for (var i = Math.Max(0, start); i < hi; i++)
                if (s[i] == '\n') n++;
            return n;
        }

        private static void ApplySourceLineToNewInstructions(List<InstructionNode> instructions, int startIndex, int line)
        {
            if (line <= 0 || startIndex >= instructions.Count) return;
            for (var i = startIndex; i < instructions.Count; i++)
            {
                if (instructions[i].SourceLine <= 0)
                    instructions[i].SourceLine = line;
            }
        }

        private void CompileSwitchArmBodyWithSourceLines(
            string armBody,
            int ifLine,
            int bodyStartLine,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int vertexCounter,
            ref int relationCounter,
            ref int shapeCounter,
            ref int memorySlotCounter,
            ref int streamLoopCounter,
            List<InstructionNode> instructions)
        {
            static int ResolveBodyLine(int bodyStartLine0, int ifLine0, int bi)
            {
                if (bodyStartLine0 > 0) return bodyStartLine0 + bi;
                if (ifLine0 > 0 && bi == 0) return ifLine0;
                return 0;
            }

            var armBodyLines = SplitStatementLines(armBody);
            for (var bi = 0; bi < armBodyLines.Count; bi++)
            {
                var lineBefore = instructions.Count;
                var ln = ResolveBodyLine(bodyStartLine, ifLine, bi);
                instructions.AddRange(CompileStatementLines(new[] { armBodyLines[bi] }, vars, ref vertexCounter, ref relationCounter, ref shapeCounter, ref memorySlotCounter, ref streamLoopCounter));
                ApplySourceLineToNewInstructions(instructions, lineBefore, ln);
            }
        }

        private static bool TryParseSwitchStatement(
            string line,
            out List<string> switchExprs,
            out string bodyText,
            out int openBraceIndex)
        {
            switchExprs = new List<string>();
            bodyText = "";
            openBraceIndex = -1;

            var t = line?.Trim() ?? "";
            if (t.Length < 8 || !t.StartsWith("switch", StringComparison.OrdinalIgnoreCase))
                return false;

            var i = 6;
            if (i < t.Length && char.IsLetterOrDigit(t[i])) return false; // not "switch" keyword
            while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
            if (i >= t.Length) return false;

            // Parse expression list (comma-separated) until '{'
            var exprStart = i;
            var braceStart = -1;
            var parenDepth = 0;
            var inStr = false;
            var quote = '\0';
            while (i < t.Length)
            {
                var ch = t[i];
                if (inStr)
                {
                    if (ch == '\\' && i + 1 < t.Length) { i += 2; continue; }
                    if (ch == quote) { inStr = false; i++; continue; }
                    i++;
                    continue;
                }
                if (ch == '"' || ch == '\'') { inStr = true; quote = ch; i++; continue; }
                if (ch == '(') { parenDepth++; i++; continue; }
                if (ch == ')') { parenDepth--; i++; continue; }
                if (ch == '{' && parenDepth == 0) { braceStart = i; break; }
                i++;
            }
            if (braceStart < 0) return false;

            var exprText = t.Substring(exprStart, braceStart - exprStart).Trim();
            if (string.IsNullOrEmpty(exprText)) return false;

            // Split by comma
            foreach (var part in exprText.Split(','))
            {
                var expr = part.Trim();
                if (!string.IsNullOrEmpty(expr))
                    switchExprs.Add(expr);
            }
            if (switchExprs.Count == 0) return false;

            // Extract body between outer braces
            var bodyDepth = 1;
            var j = braceStart + 1;
            while (j < t.Length && bodyDepth > 0)
            {
                var c = t[j];
                if (c == '"' || c == '\'')
                {
                    var q = c;
                    j++;
                    while (j < t.Length && t[j] != q) j++;
                    if (j < t.Length) j++;
                    continue;
                }
                if (c == '{') { bodyDepth++; j++; continue; }
                if (c == '}') { bodyDepth--; if (bodyDepth == 0) break; j++; continue; }
                j++;
            }
            if (bodyDepth != 0) return false;
            bodyText = t.Substring(braceStart + 1, j - braceStart - 1);
            openBraceIndex = braceStart;
            return true;
        }

        // ─── assignment and compound JSON ops ───────────────────────────────────────

        // ─── entity (vertex/relation/shape) reassignment ────────────────────────────


    }
}

