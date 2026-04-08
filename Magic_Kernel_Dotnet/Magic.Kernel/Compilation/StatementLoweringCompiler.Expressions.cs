using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Magic.Kernel.Compilation.Ast;
using Magic.Kernel.Processor;

namespace Magic.Kernel.Compilation
{
    internal sealed partial class StatementLoweringCompiler
    {
        private static void EmitFinalConvertCall(long valueSlot, string toTypeName, List<InstructionNode> instructions)
        {
            instructions.Add(CreatePushMemoryInstruction(valueSlot));
            instructions.Add(CreatePushStringInstruction(toTypeName));
            instructions.Add(CreatePushIntInstruction(2));
            instructions.Add(CreateCallInstruction("convert"));
            instructions.Add(CreatePopMemoryInstruction(valueSlot));
        }

        private static int IndexAfterMatchingGenericClose(string expr, int ltIndex)
        {
            if (ltIndex < 0 || ltIndex >= expr.Length || expr[ltIndex] != '<')
                return -1;
            var depth = 1;
            var inString = false;
            var quote = '\0';
            var escaped = false;
            for (var j = ltIndex + 1; j < expr.Length; j++)
            {
                var c = expr[j];
                if (inString)
                {
                    if (escaped) { escaped = false; continue; }
                    if (c == '\\') { escaped = true; continue; }
                    if (c == quote) { inString = false; continue; }
                    continue;
                }
                if (c == '"' || c == '\'') { inString = true; quote = c; continue; }
                if (c == '<') { depth++; continue; }
                if (c == '>')
                {
                    depth--;
                    if (depth == 0)
                        return j + 1;
                }
            }
            return -1;
        }

        private static bool IsGenericTypeParameterOpen(string expr, int ltIndex) =>
            ltIndex > 0 && ltIndex < expr.Length && expr[ltIndex] == '<' &&
            (char.IsLetterOrDigit(expr[ltIndex - 1]) || expr[ltIndex - 1] == '_');

        private static void SkipWsArithmetic(string expr, ref int i)
        {
            while (i < expr.Length && char.IsWhiteSpace(expr[i]))
                i++;
        }

        private static void EmitCoerceSlotIfNeeded(long slot, string? leafCoerceTo, List<InstructionNode> instructions)
        {
            if (string.IsNullOrEmpty(leafCoerceTo))
                return;
            instructions.Add(CreatePushMemoryInstruction(slot));
            instructions.Add(CreatePushStringInstruction(leafCoerceTo!));
            instructions.Add(CreatePushIntInstruction(2));
            instructions.Add(CreateCallInstruction("convert"));
            instructions.Add(CreatePopMemoryInstruction(slot));
        }

        private bool TryCompileArithmeticExpressionToSlot(
            string expr,
            Dictionary<string, (string Kind, int Index)> vars,
            long targetSlot,
            ref int memorySlotCounter,
            List<InstructionNode> instructions,
            string? leafCoerceTo)
        {
            if (string.IsNullOrWhiteSpace(expr))
                return false;
            var i = 0;
            SkipWsArithmetic(expr, ref i);
            if (i >= expr.Length)
                return false;
            if (!TryCompileAddSub(expr, ref i, vars, targetSlot, ref memorySlotCounter, instructions, leafCoerceTo))
                return false;
            SkipWsArithmetic(expr, ref i);
            return i >= expr.Length;
        }

        private bool TryCompileAddSub(
            string expr,
            ref int i,
            Dictionary<string, (string Kind, int Index)> vars,
            long accumSlot,
            ref int memorySlotCounter,
            List<InstructionNode> instructions,
            string? leafCoerceTo)
        {
            if (!TryCompileMulDivTerm(expr, ref i, vars, accumSlot, ref memorySlotCounter, instructions, leafCoerceTo))
                return false;
            while (true)
            {
                SkipWsArithmetic(expr, ref i);
                if (i >= expr.Length)
                    return true;
                var op = expr[i];
                if (op != '+' && op != '-')
                    return true;
                i++;
                var rightSlot = memorySlotCounter++;
                if (!TryCompileMulDivTerm(expr, ref i, vars, rightSlot, ref memorySlotCounter, instructions, leafCoerceTo))
                    return false;
                instructions.Add(CreatePushMemoryInstruction(accumSlot));
                instructions.Add(CreatePushMemoryInstruction(rightSlot));
                instructions.Add(new InstructionNode { Opcode = op == '+' ? "add" : "sub" });
                instructions.Add(CreatePopMemoryInstruction(accumSlot));
            }
        }

        private bool TryCompileMulDivTerm(
            string expr,
            ref int i,
            Dictionary<string, (string Kind, int Index)> vars,
            long termSlot,
            ref int memorySlotCounter,
            List<InstructionNode> instructions,
            string? leafCoerceTo)
        {
            if (!TryCompileUnaryFactor(expr, ref i, vars, termSlot, ref memorySlotCounter, instructions, leafCoerceTo))
                return false;
            while (true)
            {
                SkipWsArithmetic(expr, ref i);
                if (i >= expr.Length)
                    return true;
                var op = expr[i];
                if (op != '*' && op != '/')
                    return true;
                i++;
                var rhsSlot = memorySlotCounter++;
                if (!TryCompileUnaryFactor(expr, ref i, vars, rhsSlot, ref memorySlotCounter, instructions, leafCoerceTo))
                    return false;
                instructions.Add(CreatePushMemoryInstruction(termSlot));
                instructions.Add(CreatePushMemoryInstruction(rhsSlot));
                instructions.Add(new InstructionNode { Opcode = op == '*' ? "mul" : "div" });
                instructions.Add(CreatePopMemoryInstruction(termSlot));
            }
        }

        private bool TryCompileUnaryFactor(
            string expr,
            ref int i,
            Dictionary<string, (string Kind, int Index)> vars,
            long factorSlot,
            ref int memorySlotCounter,
            List<InstructionNode> instructions,
            string? leafCoerceTo)
        {
            SkipWsArithmetic(expr, ref i);
            while (i < expr.Length && expr[i] == '+')
                i++;
            var neg = false;
            if (i < expr.Length && expr[i] == '-')
            {
                neg = true;
                i++;
                SkipWsArithmetic(expr, ref i);
            }
            if (!TryCompilePowTerm(expr, ref i, vars, factorSlot, ref memorySlotCounter, instructions, leafCoerceTo))
                return false;
            if (!neg)
                return true;
            instructions.Add(CreatePushIntInstruction(0));
            instructions.Add(CreatePushMemoryInstruction(factorSlot));
            instructions.Add(new InstructionNode { Opcode = "sub" });
            instructions.Add(CreatePopMemoryInstruction(factorSlot));
            return true;
        }

        private bool TryCompilePowTerm(
            string expr,
            ref int i,
            Dictionary<string, (string Kind, int Index)> vars,
            long termSlot,
            ref int memorySlotCounter,
            List<InstructionNode> instructions,
            string? leafCoerceTo)
        {
            if (!TryCompilePrimaryFactor(expr, ref i, vars, termSlot, ref memorySlotCounter, instructions, leafCoerceTo))
                return false;
            SkipWsArithmetic(expr, ref i);
            if (i >= expr.Length || expr[i] != '^')
                return true;
            i++;
            var rhsSlot = memorySlotCounter++;
            if (!TryCompilePowTerm(expr, ref i, vars, rhsSlot, ref memorySlotCounter, instructions, leafCoerceTo))
                return false;
            instructions.Add(CreatePushMemoryInstruction(termSlot));
            instructions.Add(CreatePushMemoryInstruction(rhsSlot));
            instructions.Add(new InstructionNode { Opcode = "pow" });
            instructions.Add(CreatePopMemoryInstruction(termSlot));
            return true;
        }

        private bool TryCompilePrimaryFactor(
            string expr,
            ref int i,
            Dictionary<string, (string Kind, int Index)> vars,
            long factorSlot,
            ref int memorySlotCounter,
            List<InstructionNode> instructions,
            string? leafCoerceTo)
        {
            SkipWsArithmetic(expr, ref i);
            if (i >= expr.Length)
                return false;
            if (expr[i] == '(')
            {
                i++;
                SkipWsArithmetic(expr, ref i);
                if (!TryCompileAddSub(expr, ref i, vars, factorSlot, ref memorySlotCounter, instructions, leafCoerceTo))
                    return false;
                SkipWsArithmetic(expr, ref i);
                if (i >= expr.Length || expr[i] != ')')
                    return false;
                i++;
                return true;
            }
            if (char.IsDigit(expr[i]))
            {
                var start = i;
                while (i < expr.Length && (char.IsDigit(expr[i]) || expr[i] == '.'))
                    i++;
                var numTxt = expr.Substring(start, i - start).Trim();
                if (!long.TryParse(numTxt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var li) &&
                    !decimal.TryParse(numTxt, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                    return false;
                if (long.TryParse(numTxt, NumberStyles.Integer, CultureInfo.InvariantCulture, out li))
                    instructions.Add(CreatePushIntInstruction(li));
                else
                {
                    instructions.Add(CreatePushStringInstruction(numTxt));
                    instructions.Add(CreatePushStringInstruction("decimal"));
                    instructions.Add(CreatePushIntInstruction(2));
                    instructions.Add(CreateCallInstruction("convert"));
                }
                instructions.Add(CreatePopMemoryInstruction(factorSlot));
                EmitCoerceSlotIfNeeded(factorSlot, leafCoerceTo, instructions);
                return true;
            }
            if (!char.IsLetter(expr[i]) && expr[i] != '_')
                return false;
            var idStart = i;
            i++;
            while (i < expr.Length && (char.IsLetterOrDigit(expr[i]) || expr[i] == '_'))
                i++;
            var name = expr.Substring(idStart, i - idStart);
            SkipWsArithmetic(expr, ref i);
            if (i < expr.Length && expr[i] == '(')
                return false;
            if (!vars.TryGetValue(name, out var v) || (v.Kind != "memory" && v.Kind != "global"))
                return false;
            instructions.Add(v.Kind == "global" ? CreatePushGlobalMemoryInstruction(v.Index) : CreatePushMemoryInstruction(v.Index));
            instructions.Add(CreatePopMemoryInstruction(factorSlot));
            EmitCoerceSlotIfNeeded(factorSlot, leafCoerceTo, instructions);
            return true;
        }

        /// <summary>Compile expression to a slot (push instructions that leave one value on stack, then pop to slot).</summary>
        private bool TryCompileExpressionToSlot(string exprText, Dictionary<string, (string Kind, int Index)> vars, long targetSlot, ref int memorySlotCounter, List<InstructionNode> instructions)
        {
            var trimmed = TrimBalancedOuterParentheses(exprText?.Trim() ?? "");
            if (string.IsNullOrEmpty(trimmed))
                return false;

            // String literal — needed for object-initializer fields (Name: "x") and avoids a cascade of
            // partial defobj from TryCompileTypeObjectInitializer + duplicate TryCompileTypeInitializerExpressionToSlot.
            if (trimmed.Length >= 2 &&
                ((trimmed[0] == '"' && trimmed[^1] == '"') || (trimmed[0] == '\'' && trimmed[^1] == '\'')))
            {
                var inner = trimmed.Substring(1, trimmed.Length - 2);
                if (trimmed[0] == '"')
                    inner = inner.Replace("\\\"", "\"", StringComparison.Ordinal);
                else
                    inner = inner.Replace("\\'", "'", StringComparison.Ordinal);
                instructions.Add(CreatePushStringInstruction(inner));
                instructions.Add(CreatePopMemoryInstruction(targetSlot));
                return true;
            }

            // Builtin: execution -> def "execution" (ExecutionDevice for current unit).
            if (string.Equals(trimmed, "execution", StringComparison.OrdinalIgnoreCase))
            {
                instructions.Add(CreatePushStringInstruction("execution"));
                instructions.Add(new InstructionNode { Opcode = "def" });
                instructions.Add(CreatePopMemoryInstruction(targetSlot));
                return true;
            }

            // Symbolic variable expression like ":time" -> get(":time", 1) into targetSlot.
            if (trimmed.Length >= 2 && trimmed[0] == ':')
            {
                var symbolicName = trimmed.Substring(1).Trim();
                if (symbolicName.Length > 0)
                {
                    var isIdent = true;
                    for (var i = 0; i < symbolicName.Length; i++)
                    {
                        var ch = symbolicName[i];
                        if (!(char.IsLetterOrDigit(ch) || ch == '_'))
                        {
                            isIdent = false;
                            break;
                        }
                    }

                    if (isIdent)
                    {
                        instructions.Add(CreatePushStringInstruction(":" + symbolicName));
                        instructions.Add(CreatePushIntInstruction(1));
                        instructions.Add(CreateCallInstruction("get"));
                        instructions.Add(CreatePopMemoryInstruction(targetSlot));
                        return true;
                    }
                }
            }

            // await expr
            if (TryParseAwaitExpressionText(trimmed, out var awaitedExpression))
            {
                var awaitSourceSlot = memorySlotCounter++;
                if (!TryCompileExpressionToSlot(awaitedExpression, vars, awaitSourceSlot, ref memorySlotCounter, instructions))
                    return false;
                instructions.Add(CreatePushMemoryInstruction(awaitSourceSlot));
                instructions.Add(new InstructionNode { Opcode = "awaitobj" });
                instructions.Add(CreatePopMemoryInstruction(targetSlot));
                return true;
            }

            // !expr
            if (trimmed.StartsWith("!", StringComparison.Ordinal) && trimmed.Length > 1)
            {
                var inner = TrimBalancedOuterParentheses(trimmed.Substring(1).Trim());
                if (inner.Length > 0)
                {
                    var notSlot = memorySlotCounter++;
                    if (TryCompileExpressionToSlot(inner, vars, notSlot, ref memorySlotCounter, instructions))
                    {
                        instructions.Add(CreatePushMemoryInstruction(notSlot));
                        instructions.Add(new InstructionNode { Opcode = "not" });
                        instructions.Add(CreatePopMemoryInstruction(targetSlot));
                        return true;
                    }
                }
            }

            // type<inner>: expr — operands coerced to cast arithmetic type, then result converted to outer.
            // For nested target types like float<decimal>, coerce operands directly to float<decimal>
            // so arithmetic runs in the same target domain.
            if (TryParseTypedCastPrefix(trimmed, out var outerTypeLit, out var innerTypeName, out var innerCastExpr))
            {
                var arithmeticCoerceType = outerTypeLit.Contains('<') ? outerTypeLit : innerTypeName;
                if (!TryCompileArithmeticExpressionToSlot(innerCastExpr, vars, targetSlot, ref memorySlotCounter, instructions, arithmeticCoerceType))
                    return false;
                if (!string.Equals(arithmeticCoerceType, outerTypeLit, StringComparison.OrdinalIgnoreCase))
                    EmitFinalConvertCall(targetSlot, outerTypeLit, instructions);
                return true;
            }

            // Top-level `a + b` so RHS can be calls (`x + add(1,2)`); inner `+` handled recursively.
            if (TrySplitBinaryPlus(trimmed, out var plusLeft, out var plusRight))
            {
                var slotLeft = memorySlotCounter++;
                var slotRight = memorySlotCounter++;
                if (!TryCompileExpressionToSlot(plusLeft, vars, slotLeft, ref memorySlotCounter, instructions))
                    return false;
                if (!TryCompileExpressionToSlot(plusRight, vars, slotRight, ref memorySlotCounter, instructions))
                    return false;
                instructions.Add(CreatePushMemoryInstruction(slotLeft));
                instructions.Add(CreatePushMemoryInstruction(slotRight));
                instructions.Add(new InstructionNode { Opcode = "add" });
                instructions.Add(CreatePopMemoryInstruction(targetSlot));
                return true;
            }

            // Arithmetic (+ - * /) with precedence when there is no top-level additive split.
            if (TryCompileArithmeticExpressionToSlot(trimmed, vars, targetSlot, ref memorySlotCounter, instructions, null))
                return true;

            // Comparisons
            if (TrySplitBinaryComparison(trimmed, out var leftStr, out var op, out var rightStr))
            {
                var slotLeft = memorySlotCounter++;
                var slotRight = memorySlotCounter++;
                if (!TryCompileExpressionToSlot(leftStr, vars, slotLeft, ref memorySlotCounter, instructions))
                    return false;
                if (!TryCompileExpressionToSlot(rightStr, vars, slotRight, ref memorySlotCounter, instructions))
                    return false;
                instructions.Add(CreatePushMemoryInstruction(slotLeft));
                instructions.Add(CreatePushMemoryInstruction(slotRight));
                if (op == "<")
                    instructions.Add(new InstructionNode { Opcode = "lt" });
                else if (op == "==")
                    instructions.Add(new InstructionNode { Opcode = "equals" });
                else
                    return false;
                instructions.Add(CreatePopMemoryInstruction(targetSlot));
                return true;
            }

            // Конструктороподобное выражение Point(1, 2) — пока трактуем как Def-типа:
            // push "Point"; push 1; def; pop slot
            if (TryCompileConstructorExpressionToSlot(trimmed, vars, targetSlot, ref memorySlotCounter, instructions))
                return true;

            // Сначала метод на экземпляре: иначе при отсутствии приёмника в vars (сбой cross-line и т.п.)
            // bare-call разберёт vault1.read(x) как vault1_read и эмитит Call — рантайм «not implemented».
            if (TryEmitMethodCallExpression(trimmed, vars, targetSlot, ref memorySlotCounter, instructions, out _))
                return true;

            // Обычный вызов функции / процедурный вызов (включая module1:calculate(...), module1:calculate.add(...) и т.п.)
            if (TryEmitBareCallExpressionToSlot(trimmed, vars, targetSlot, ref memorySlotCounter, instructions))
                return true;

            // Только теперь пробуем трактовать как type-initializer / object-initializer или XXX[]
            // (Room(Board: ..., Persons: ...), Shape[], Point[], Persons[] и т.п.).
            if (TryCompileTypeInitializerExpressionToSlot(trimmed, vars, targetSlot, ref memorySlotCounter, instructions))
                return true;

            // Простое имя переменной / глобала / vertex/shape/etc.
            if (vars.TryGetValue(trimmed, out var singleVar))
            {
                if (singleVar.Kind == "memory" || singleVar.Kind == "stream" || singleVar.Kind == "def" || singleVar.Kind == "global")
                {
                    instructions.Add(singleVar.Kind == "global"
                        ? CreatePushGlobalMemoryInstruction(singleVar.Index)
                        : CreatePushMemoryInstruction(singleVar.Index));
                    instructions.Add(CreatePopMemoryInstruction(targetSlot));
                    return true;
                }

                if (singleVar.Kind == "vertex" || singleVar.Kind == "relation" || singleVar.Kind == "shape")
                {
                    instructions.Add(CreatePushIntInstruction(singleVar.Index));
                    instructions.Add(CreatePopMemoryInstruction(targetSlot));
                    return true;
                }
            }

            // a.b.c member access → getobj
            if (TryParseMemberAccessFromString(trimmed, out var memberRoot, out var memberPath) &&
                vars.TryGetValue(memberRoot, out var memberVar))
            {
                instructions.Add(memberVar.Kind == "global"
                    ? CreatePushGlobalMemoryInstruction(memberVar.Index)
                    : CreatePushMemoryInstruction(memberVar.Index));
                instructions.Add(CreatePushStringInstruction(memberPath));
                instructions.Add(new InstructionNode { Opcode = "getobj" });
                instructions.Add(CreatePopMemoryInstruction(targetSlot));
                return true;
            }

            // list[index] on DefList → callobj "get"
            if (TryParseSimpleBracketIndexer(trimmed, out var idxBaseVar, out var idxInnerExpr) &&
                vars.TryGetValue(idxBaseVar, out var idxVar) &&
                (idxVar.Kind == "memory" || idxVar.Kind == "global"))
            {
                var idxSlot = memorySlotCounter++;
                if (!TryCompileExpressionToSlot(idxInnerExpr, vars, idxSlot, ref memorySlotCounter, instructions))
                    return false;
                instructions.Add(idxVar.Kind == "global"
                    ? CreatePushGlobalMemoryInstruction(idxVar.Index)
                    : CreatePushMemoryInstruction(idxVar.Index));
                instructions.Add(CreatePushMemoryInstruction(idxSlot));
                instructions.Add(CreatePushIntInstruction(1));
                instructions.Add(new InstructionNode
                {
                    Opcode = "callobj",
                    Parameters = new List<ParameterNode>
                    {
                        new FunctionNameParameterNode { FunctionName = "get" }
                    }
                });
                instructions.Add(CreatePopMemoryInstruction(targetSlot));
                return true;
            }

            return false;
        }

        /// <summary><c>name[expr]</c> at expression top-level (no trailing member chain).</summary>
        private static bool TryParseSimpleBracketIndexer(string expr, out string variableName, out string indexExpression)
        {
            variableName = "";
            indexExpression = "";
            expr = expr.Trim().TrimEnd(';');
            var open = expr.IndexOf('[');
            if (open <= 0)
                return false;
            var baseId = expr.Substring(0, open).Trim();
            if (string.IsNullOrEmpty(baseId))
                return false;
            foreach (var c in baseId)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_'))
                    return false;
            }

            variableName = baseId;
            var depth = 0;
            for (var i = open; i < expr.Length; i++)
            {
                var c = expr[i];
                if (c == '[') depth++;
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        indexExpression = expr.Substring(open + 1, i - open - 1).Trim();
                        return indexExpression.Length > 0 && i == expr.Length - 1;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Type-initializer expression in RHS position (constructor/object init style):
        ///   new Type(Field: expr, ...)  или  Type(Field: expr, ...)  -> defobj Type + setobj по имени поля (как у конструктора с именованными параметрами; явного ctor в unit может не быть).
        ///   new Type[]  или  Type[]  -> пустой список (defobj class + <c>list</c>) с элементным типом Type, как <c>DefList&lt;Type&gt;</c> в рантайме.
        /// </summary>
        private bool TryCompileTypeInitializerExpressionToSlot(
            string trimmed,
            Dictionary<string, (string Kind, int Index)> vars,
            long targetSlot,
            ref int memorySlotCounter,
            List<InstructionNode> instructions)
        {
            if (string.IsNullOrWhiteSpace(trimmed))
                return false;

            // Опциональный префикс `new ` — сахар присваивания уже подставляет его для Type(...),
            // но внутри вложенных выражений допускаем и голый Type[] / Type(Field: ...).
            var rest = trimmed.TrimStart();
            if (rest.StartsWith("new", StringComparison.OrdinalIgnoreCase) &&
                rest.Length >= 4 &&
                char.IsWhiteSpace(rest[3]))
            {
                rest = rest.Substring(3).TrimStart();
            }

            if (rest.Length == 0)
                return false;

            // Здесь занимаемся только:
            //   - списками XXX[]
            //   - object-initializer'ами Type(Field: expr, ...)
            var hasParens = rest.IndexOf('(') >= 0;
            var hasColon = rest.IndexOf(':') >= 0;
            var isArrayLike = rest.EndsWith("[]", StringComparison.Ordinal);
            if (!isArrayLike && !hasParens && !hasColon)
                return false;

            // XXX[] -> список XXX: push "XXX"; push "list"; defobj; pop targetSlot
            // Обрабатываем только простой идентификатор без аргументов, чтобы
            // не ловить выражения вида Board(Surface: ..., Shapes: Shape[]).
            if (rest.EndsWith("[]", StringComparison.Ordinal))
            {
                var elemTypeRaw = rest.Substring(0, rest.Length - 2);
                var elemType = elemTypeRaw.Trim();
                if (elemType.Length == 0)
                    return false;

                // Не считаем идентификатор типом, если он выглядит как
                // имя функции/процедуры (условно: начинается с маленькой буквы).
                // Это грубая эвристика, но она защищает от кейсов вроде
                // `calculate[]` / `calculate(...)` для функций.
                if (char.IsLetter(elemType[0]) && char.IsLower(elemType[0]))
                    return false;

                // Если внутри есть скобки/двоеточия/пробелы — это не просто "Type[]",
                // пропускаем этот путь (пусть обрабатывается как object-initializer).
                for (var i = 0; i < elemTypeRaw.Length; i++)
                {
                    var ch = elemTypeRaw[i];
                    if (ch is '(' or ')' or ':' || char.IsWhiteSpace(ch))
                        return false;
                }

                var qualifiedElemType = QualifyTypeNameForDefObj(elemType);
                instructions.Add(CreatePushClassInstruction(qualifiedElemType));
                instructions.Add(CreatePushStringInstruction("list"));
                instructions.Add(new InstructionNode { Opcode = "defobj" });
                instructions.Add(CreatePopMemoryInstruction(targetSlot));
                return true;
            }

            // Type(Field: expr, ...) / Type{Field: expr, ...} — object-initializer в RHS.
            // Поддерживаем как синтаксис с "()", так и с "{}":
            //   Room(Board: ..., Persons: ...)
            //   new Room{Board: ..., Persons: ...}
            var parenIdx = rest.IndexOf('(');
            var braceIdx = rest.IndexOf('{');
            var openIdx = -1;
            var openChar = '\0';
            var closeChar = '\0';

            if (parenIdx > 0 && (braceIdx <= 0 || parenIdx < braceIdx))
            {
                openIdx = parenIdx;
                openChar = '(';
                closeChar = ')';
            }
            else if (braceIdx > 0)
            {
                openIdx = braceIdx;
                openChar = '{';
                closeChar = '}';
            }

            if (openIdx <= 0)
                return false;

            var typeName = rest.Substring(0, openIdx).Trim();
            if (string.IsNullOrEmpty(typeName))
                return false;

            // Аналогичная эвристика для object-initializer:
            // Type(Field: ...) должен быть именно типом (PascalCase),
            // а не функцией/процедурой вроде `calculate(...)`.
            if (char.IsLetter(typeName[0]) && char.IsLower(typeName[0]))
                return false;

            var lastCloseIdx = rest.LastIndexOf(closeChar);
            if (lastCloseIdx <= openIdx)
                return false;

            var innerArgs = rest.Substring(openIdx + 1, lastCloseIdx - openIdx - 1).Trim();
            if (string.IsNullOrEmpty(innerArgs) || !innerArgs.Contains(":", StringComparison.Ordinal))
                return false;

            // Разбиваем innerArgs на top-level элементы с учётом вложенных скобок.
            var items = new List<string>();
            {
                var depth = 0;
                var sb = new System.Text.StringBuilder();
                var inString = false;
                char quote = '\0';
                var escaped = false;

                foreach (var ch in innerArgs)
                {
                    if (inString)
                    {
                        if (escaped)
                        {
                            escaped = false;
                            sb.Append(ch);
                            continue;
                        }
                        if (ch == '\\')
                        {
                            escaped = true;
                            sb.Append(ch);
                            continue;
                        }
                        if (ch == quote)
                        {
                            inString = false;
                            sb.Append(ch);
                            continue;
                        }

                        sb.Append(ch);
                        continue;
                    }

                    if (ch == '"' || ch == '\'')
                    {
                        inString = true;
                        quote = ch;
                        sb.Append(ch);
                        continue;
                    }

                    if (ch is '(' or '{' or '[') depth++;
                    if (ch is ')' or '}' or ']') depth--;

                    if (ch == ',' && depth == 0)
                    {
                        var part = sb.ToString().Trim();
                        if (!string.IsNullOrEmpty(part))
                            items.Add(part);
                        sb.Clear();
                        continue;
                    }

                    sb.Append(ch);
                }

                var last = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(last))
                    items.Add(last);
            }

            if (items.Count == 0)
                return false;

            // new TypeName в targetSlot (как объектная конструкция)
            var qualifiedTypeName = QualifyTypeNameForDefObj(typeName);
            instructions.Add(CreatePushClassInstruction(qualifiedTypeName));
            instructions.Add(new InstructionNode { Opcode = "defobj" });
            instructions.Add(CreatePopMemoryInstruction(targetSlot));

            // setobj по каждому полю
            foreach (var item in items)
            {
                var colonIdx = item.IndexOf(':');
                if (colonIdx <= 0 || colonIdx >= item.Length - 1)
                    return false;

                var fieldName = item.Substring(0, colonIdx).Trim();
                var expr = item.Substring(colonIdx + 1).Trim();
                if (string.IsNullOrEmpty(fieldName) || string.IsNullOrEmpty(expr))
                    return false;

                var fieldSlot = memorySlotCounter++;
                if (!TryCompileExpressionToSlot(expr, vars, fieldSlot, ref memorySlotCounter, instructions))
                    return false;

                var discardSlot = memorySlotCounter++;
                instructions.Add(CreatePushMemoryInstruction(targetSlot));
                instructions.Add(CreatePushStringInstruction(fieldName));
                instructions.Add(CreatePushMemoryInstruction(fieldSlot));
                instructions.Add(new InstructionNode { Opcode = "setobj" });
                instructions.Add(CreatePopMemoryInstruction(discardSlot));
            }

            return true;
        }

        private bool TryCompileConstructorExpressionToSlot(string expr, Dictionary<string, (string Kind, int Index)> vars, long targetSlot, ref int memorySlotCounter, List<InstructionNode> instructions)
        {
            var trimmed = (expr ?? string.Empty).TrimStart();
            if (trimmed.Length == 0)
                return false;

            // Support constructor sugar in expression position as well:
            //   Type.Constructor(1, 2)  -> new Type(1, 2)
            //   Type                    -> new Type()
            // before falling back to the strict "new Type(...)" parsing.
            if (!trimmed.StartsWith("new", StringComparison.OrdinalIgnoreCase))
            {
                const string ctorSuffix = ".Constructor(";
                var ctorIdx = trimmed.IndexOf(ctorSuffix, StringComparison.Ordinal);
                if (ctorIdx > 0 && trimmed.EndsWith(")", StringComparison.Ordinal))
                {
                    var typeNameForCtor = trimmed.Substring(0, ctorIdx).Trim();
                    if (!string.IsNullOrEmpty(typeNameForCtor))
                    {
                        var argsTextForCtor = trimmed.Substring(ctorIdx + ctorSuffix.Length,
                            trimmed.Length - (ctorIdx + ctorSuffix.Length) - 1);
                        trimmed = $"new {typeNameForCtor}({argsTextForCtor})";
                    }
                }
                else
                {
                    var t2 = trimmed.Trim();
                    var openParenSugar = t2.IndexOf('(');
                    if (openParenSugar > 0 && t2.EndsWith(")", StringComparison.Ordinal))
                    {
                        var typeCandidate = t2.Substring(0, openParenSugar).Trim();
                        if (IsLikelyTypeIdentifier(typeCandidate))
                        {
                            var argsSugar = t2.Substring(openParenSugar + 1, t2.Length - openParenSugar - 2);
                            trimmed = $"new {typeCandidate}({argsSugar})";
                        }
                    }
                    else if (IsLikelyTypeIdentifier(t2))
                        trimmed = $"new {t2}()";
                }
            }

            // Constructor-call (positional args) in the canonical form: new TypeName(...)
            if (!trimmed.StartsWith("new", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Length < 4 ||
                !char.IsWhiteSpace(trimmed[3]))
            {
                return false;
            }

            var afterNew = trimmed.Substring(3).TrimStart();
            if (afterNew.Length == 0)
                return false;

            var parenIdx = afterNew.IndexOf('(');
            if (parenIdx <= 0)
                return false;

            var typeName = afterNew.Substring(0, parenIdx).Trim();
            if (string.IsNullOrEmpty(typeName))
                return false;

            // Heuristic: конструктор должен быть для типа (PascalCase).
            if (typeName.Length > 0 && char.IsLetter(typeName[0]) && char.IsLower(typeName[0]))
                return false;

            var lastParenIdx = afterNew.LastIndexOf(')');
            if (lastParenIdx <= parenIdx || lastParenIdx != afterNew.Length - 1)
                return false;

            var argsText = afterNew.Substring(parenIdx + 1, lastParenIdx - parenIdx - 1).Trim();

            // Именованные аргументы (как поля): defobj + setobj; при отсутствии отдельной функции ctor в unit это совпадает с «присвоить параметры полям 1:1».
            if (argsText.Contains(":", StringComparison.Ordinal))
                return TryCompileTypeInitializerExpressionToSlot(trimmed, vars, targetSlot, ref memorySlotCounter, instructions);

            // Split top-level args (с учётом строк и вложенных скобок).
            var argList = new List<string>();
            if (!string.IsNullOrEmpty(argsText))
            {
                var depth = 0;
                var sb = new System.Text.StringBuilder();
                var inString = false;
                char quote = '\0';
                var escaped = false;

                foreach (var ch in argsText)
                {
                    if (inString)
                    {
                        sb.Append(ch);
                        if (escaped)
                        {
                            escaped = false;
                            continue;
                        }
                        if (ch == '\\')
                        {
                            escaped = true;
                            continue;
                        }
                        if (ch == quote)
                            inString = false;
                        continue;
                    }

                    if (ch == '"' || ch == '\'')
                    {
                        inString = true;
                        quote = ch;
                        sb.Append(ch);
                        continue;
                    }

                    if (ch == ',' && depth == 0)
                    {
                        var arg = sb.ToString().Trim();
                        if (!string.IsNullOrEmpty(arg))
                            argList.Add(arg);
                        sb.Clear();
                        continue;
                    }

                    if (ch is '(' or '{' or '[') depth++;
                    if (ch is ')' or '}' or ']') depth--;
                    sb.Append(ch);
                }

                var last = sb.ToString().Trim();
                if (!string.IsNullOrEmpty(last))
                    argList.Add(last);
            }

            var qualifiedCtorTypeName = QualifyTypeNameForDefObj(typeName);
            return TryEmitQualifiedPositionalConstructorToSlot(
                qualifiedCtorTypeName,
                argList,
                vars,
                targetSlot,
                ref memorySlotCounter,
                instructions);
        }

        /// <summary>
        /// Уже квалифицированное имя типа: <c>push class</c>, <c>defobj</c>, <c>callobj …_ctor_1</c>, <c>pop</c> (arity только аргументы, без <c>this</c>).
        /// </summary>
        private bool TryEmitQualifiedPositionalConstructorToSlot(
            string qualifiedTypeName,
            List<string> argExprs,
            Dictionary<string, (string Kind, int Index)> vars,
            long targetSlot,
            ref int memorySlotCounter,
            List<InstructionNode> instructions)
        {
            var preparedArgs = new List<(string Kind, int MemIndex, long IntValue, string? StrValue)>(argExprs.Count);
            foreach (var argExpr in argExprs)
            {
                var ae = argExpr.Trim();
                if (ae.Length == 0)
                    continue;

                if (vars.TryGetValue(ae, out var v) && v.Kind == "memory")
                {
                    preparedArgs.Add(("memory", v.Index, 0L, null));
                    continue;
                }

                if (int.TryParse(ae, out var intVal))
                {
                    preparedArgs.Add(("int", -1, intVal, null));
                    continue;
                }

                if ((ae.Length >= 2 && ae[0] == '"' && ae[^1] == '"') ||
                    (ae.Length >= 2 && ae[0] == '\'' && ae[^1] == '\''))
                {
                    var s = ae.Substring(1, ae.Length - 2);
                    preparedArgs.Add(("string", -1, 0L, s));
                    continue;
                }

                return false;
            }

            var ctorName = $"{qualifiedTypeName}_ctor_1";
            instructions.Add(CreatePushClassInstruction(qualifiedTypeName));
            instructions.Add(new InstructionNode { Opcode = "defobj" });
            instructions.Add(CreatePopMemoryInstruction(targetSlot));

            instructions.Add(CreatePushMemoryInstruction(targetSlot));
            foreach (var prepared in preparedArgs)
            {
                switch (prepared.Kind)
                {
                    case "memory":
                        instructions.Add(CreatePushMemoryInstruction(prepared.MemIndex));
                        break;
                    case "int":
                        instructions.Add(CreatePushIntInstruction(prepared.IntValue));
                        break;
                    case "string":
                        instructions.Add(CreatePushStringInstruction(prepared.StrValue ?? ""));
                        break;
                    default:
                        return false;
                }
            }

            instructions.Add(CreatePushIntInstruction(preparedArgs.Count));
            instructions.Add(new InstructionNode
            {
                Opcode = "callobj",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = ctorName }
                }
            });
            instructions.Add(CreatePopInstruction());

            return true;
        }

        private static bool TrySplitBinaryPlus(string expr, out string left, out string right)
        {
            left = "";
            right = "";
            if (string.IsNullOrWhiteSpace(expr))
                return false;

            var depth = 0;
            var inString = false;
            var quote = '\0';
            var escaped = false;

            for (var i = 0; i < expr.Length; i++)
            {
                var ch = expr[i];

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }
                    if (ch == '\\')
                    {
                        escaped = true;
                        continue;
                    }
                    if (ch == quote)
                        inString = false;
                    continue;
                }

                if (ch == '"' || ch == '\'')
                {
                    inString = true;
                    quote = ch;
                    continue;
                }

                if (ch == '(') { depth++; continue; }
                if (ch == ')') { depth = Math.Max(0, depth - 1); continue; }

                if (depth == 0 && ch == '+')
                {
                    left = expr.Substring(0, i).Trim();
                    right = expr.Substring(i + 1).Trim();
                    return left.Length > 0 && right.Length > 0;
                }
            }

            return false;
        }

        private static bool TrySplitBinaryComparison(string expr, out string left, out string op, out string right)
        {
            left = "";
            op = "";
            right = "";
            var depth = 0;
            var inString = false;
            var quote = '\0';
            var escaped = false;
            for (var i = 0; i < expr.Length; i++)
            {
                var ch = expr[i];
                if (inString)
                {
                    if (escaped) { escaped = false; continue; }
                    if (ch == '\\') { escaped = true; continue; }
                    if (ch == quote) { inString = false; continue; }
                    continue;
                }
                if (ch == '"' || ch == '\'') { inString = true; quote = ch; continue; }
                if (ch == '(') { depth++; continue; }
                if (ch == ')') { depth--; continue; }
                if (depth != 0) continue;
                if (ch == '<')
                {
                    if (IsGenericTypeParameterOpen(expr, i))
                    {
                        var afterGen = IndexAfterMatchingGenericClose(expr, i);
                        if (afterGen >= 0)
                        {
                            i = afterGen - 1;
                            continue;
                        }
                    }
                    var j = i + 1;
                    while (j < expr.Length && char.IsWhiteSpace(expr[j])) j++;
                    if (j < expr.Length && expr[j] == '>')
                        continue;
                    if ((i == 0 || expr[i - 1] != '=') && (i + 1 >= expr.Length || expr[i + 1] != '='))
                    {
                        left = expr.Substring(0, i).Trim();
                        op = "<";
                        right = expr.Substring(i + 1).Trim();
                        return left.Length > 0 && right.Length > 0;
                    }
                }
                if (i + 1 < expr.Length && ch == '=' && expr[i + 1] == '=')
                {
                    left = expr.Substring(0, i).Trim();
                    op = "==";
                    right = expr.Substring(i + 2).Trim();
                    return left.Length > 0 && right.Length > 0;
                }
            }
            return false;
        }

        /// <summary>
        /// <c>vault1.read(x)</c> разбирается <see cref="TryParseBareCall"/> как имя <c>vault1_read</c> (Call).
        /// Если <c>vault1</c> — локальный/global слот экземпляра (memory/stream/def), нужен <c>callobj read</c> через
        /// <see cref="TryEmitMethodCallExpression"/> — не перехватываем bare-call.
        /// Префикс <c>module:</c> не трогаем (<c>mod:f(x)</c>).
        /// </summary>
        private static bool ShouldDeferBareCallToInstanceMethod(string trimmed, Dictionary<string, (string Kind, int Index)> vars)
        {
            var scanner = new Scanner(trimmed);
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            var receiver = scanner.Scan().Value ?? "";
            if (scanner.Current.Kind == TokenKind.Colon)
                return false;
            if (!vars.TryGetValue(receiver, out var recvVar))
                return false;
            if (recvVar.Kind != "memory" && recvVar.Kind != "stream" && recvVar.Kind != "def" && recvVar.Kind != "global")
                return false;
            return scanner.Current.Kind == TokenKind.Dot;
        }

        private bool TryEmitBareCallExpressionToSlot(string expr, Dictionary<string, (string Kind, int Index)> vars, long targetSlot, ref int memorySlotCounter, List<InstructionNode> instructions)
        {
            var trimmedExpr = expr.Trim();
            if (ShouldDeferBareCallToInstanceMethod(trimmedExpr, vars))
                return false;

            if (!TryParseBareCall(expr, out var funcName, out var argExprs))
                return false;

            // Имя совпадает с единственным типом в области (локальный + use) → конструктор, не вызов функции.
            if (funcName.IndexOf(':') < 0 &&
                _typeResolutionIndex != null &&
                !argExprs.Any(a => a.Contains(":", StringComparison.Ordinal)) &&
                _typeResolutionIndex.TryGetUniqueType(funcName, out var qualifiedCtorType, out _))
            {
                if (!TryEmitQualifiedPositionalConstructorToSlot(
                        qualifiedCtorType,
                        argExprs,
                        vars,
                        targetSlot,
                        ref memorySlotCounter,
                        instructions))
                {
                    throw new CompilationException(
                        $"Cannot compile constructor call '{expr.Trim()}': unsupported arguments for type '{qualifiedCtorType}'.");
                }

                return true;
            }

            // Calling convention for user-defined functions/procedures:
            // - stack: [arg0, arg1, ..., argN-1, N]
            // - call opcode provides only the function name (no callInfo.Parameters template),
            //   so interpreter populates parameters/binds locals via procedure/function prologue.
            for (var i = 0; i < argExprs.Count; i++)
            {
                var arg = argExprs[i].Trim();
                if (long.TryParse(arg, out var numVal))
                    instructions.Add(CreatePushIntInstruction(numVal));
                else if (arg.Length >= 2 && (arg.StartsWith("\"", StringComparison.Ordinal) && arg.EndsWith("\"", StringComparison.Ordinal) || arg.StartsWith("'", StringComparison.Ordinal) && arg.EndsWith("'", StringComparison.Ordinal)))
                    instructions.Add(CreatePushStringInstruction(arg.Substring(1, arg.Length - 2).Replace("\\\"", "\"", StringComparison.Ordinal)));
                else if (TryParseTypeLiteralArgument(arg, out var typeLiteral))
                    instructions.Add(CreatePushTypeInstruction(typeLiteral));
                else if (vars.TryGetValue(arg, out var argVar) && (argVar.Kind == "memory" || argVar.Kind == "global"))
                    instructions.Add(argVar.Kind == "global" ? CreatePushGlobalMemoryInstruction(argVar.Index) : CreatePushMemoryInstruction(argVar.Index));
                else if (vars.TryGetValue(arg, out var argVar2) && (argVar2.Kind == "vertex" || argVar2.Kind == "relation" || argVar2.Kind == "shape"))
                    instructions.Add(CreatePushIntInstruction(argVar2.Index));
                else
                    return false;
            }

            instructions.Add(CreatePushIntInstruction(argExprs.Count));

            instructions.Add(CreateCallInstruction(ResolveUserCallTarget(funcName)));
            instructions.Add(CreatePopMemoryInstruction(targetSlot));
            return true;
        }

        private static bool TryParseTypeLiteralArgument(string text, out string typeLiteral)
        {
            typeLiteral = "";
            var trimmed = text.Trim();
            if (trimmed.Length == 0)
                return false;

            var scanner = new Scanner(trimmed);
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;

            var baseType = scanner.Scan().Value.ToLowerInvariant();
            if (scanner.Current.Kind != TokenKind.LessThan)
                return false;

            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier && scanner.Current.Kind != TokenKind.StringLiteral)
                return false;

            var genericType = scanner.Scan().Value.ToLowerInvariant();
            if (scanner.Current.Kind != TokenKind.GreaterThan)
                return false;

            scanner.Scan();
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();

            if (!scanner.Current.IsEndOfInput)
                return false;

            typeLiteral = $"{baseType}<{genericType}>";
            return true;
        }

        private static bool TryConsumeNestedFunctionPathSuffix(Scanner scanner, ref string pathSoFar)
        {
            while (scanner.Current.Kind == TokenKind.Dot)
            {
                scanner.Scan();
                while (scanner.Current.Kind == TokenKind.Newline)
                    scanner.Scan();
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                pathSoFar += "_" + (scanner.Scan().Value ?? "");
            }
            return true;
        }

        private static bool TryParseBareCall(string expr, out string funcName, out List<string> argExprs)
        {
            funcName = "";
            argExprs = new List<string>();
            var trimmed = expr.Trim();
            var scanner = new Scanner(trimmed);
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            var firstIdent = scanner.Scan().Value ?? "";
            // Optional module-prefixed call: module1:div(x, y) or module1:calculate.add(x, y)
            // Nested path a.b.c before '(' → манглированное имя a_b_c (как при компиляции вложенных function).
            string namePath;
            if (scanner.Current.Kind == TokenKind.Colon)
            {
                scanner.Scan();
                while (scanner.Current.Kind == TokenKind.Newline)
                    scanner.Scan();
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                namePath = scanner.Scan().Value ?? "";
                if (!TryConsumeNestedFunctionPathSuffix(scanner, ref namePath))
                    return false;
                funcName = $"{firstIdent}:{namePath}";
            }
            else
            {
                namePath = firstIdent;
                if (!TryConsumeNestedFunctionPathSuffix(scanner, ref namePath))
                    return false;
                funcName = namePath;
            }
            if (scanner.Current.Kind != TokenKind.LParen)
                return false;
            var openParenStart = scanner.Current.Start;
            scanner.Scan();
            if (scanner.Current.Kind == TokenKind.RParen)
            {
                scanner.Scan();
                while (scanner.Current.Kind == TokenKind.Semicolon)
                    scanner.Scan();
                return scanner.Current.IsEndOfInput;
            }
            var start = scanner.Current.Start;
            var depth = 1;
            var inString = false;
            var quote = '\0';
            var escaped = false;
            var segmentStart = start;
            var closeParenIndex = -1;
            for (var i = openParenStart + 1; i < trimmed.Length; i++)
            {
                var ch = trimmed[i];
                if (inString)
                {
                    if (escaped) { escaped = false; continue; }
                    if (ch == '\\') { escaped = true; continue; }
                    if (ch == quote) { inString = false; continue; }
                    continue;
                }
                if (ch == '"' || ch == '\'') { inString = true; quote = ch; continue; }
                if (ch == '(') { depth++; continue; }
                if (ch == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        if (segmentStart < i)
                            argExprs.Add(trimmed.Substring(segmentStart, i - segmentStart).Trim());
                        closeParenIndex = i;
                        break;
                    }
                    continue;
                }
                if (depth == 1 && ch == ',')
                {
                    argExprs.Add(trimmed.Substring(segmentStart, i - segmentStart).Trim());
                    segmentStart = i + 1;
                }
            }
            if (closeParenIndex < 0)
                return false;
            for (var i = closeParenIndex + 1; i < trimmed.Length; i++)
            {
                if (!char.IsWhiteSpace(trimmed[i]) && trimmed[i] != ';')
                    return false;
            }
            return true;
        }

        private static bool TryParseMemberAccessFromString(string expr, out string rootVariableName, out string path)
        {
            rootVariableName = "";
            path = "";
            var scanner = new Scanner(expr.Trim());
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            rootVariableName = scanner.Scan().Value;
            var segments = new List<string>();
            // Allow `data!.field` — `!` is tokenized as Identifier("!"); same as assignment lowering.
            while (!scanner.Current.IsEndOfInput && scanner.Current.Kind != TokenKind.Semicolon)
            {
                if (scanner.Current.Kind == TokenKind.Identifier && scanner.Current.Value == "!")
                    scanner.Scan();
                if (scanner.Current.Kind != TokenKind.Dot)
                    break;
                scanner.Scan();
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                var segment = scanner.Scan().Value;
                if (scanner.Current.Kind == TokenKind.LessThan && scanner.Watch(1)?.Kind == TokenKind.GreaterThan)
                {
                    scanner.Scan();
                    scanner.Scan();
                    segment += "<>";
                }
                segments.Add(segment);
            }
            if (segments.Count == 0)
                return false;
            path = string.Join(".", segments);
            return scanner.Current.IsEndOfInput || scanner.Current.Kind == TokenKind.Semicolon;
        }
    }
}

