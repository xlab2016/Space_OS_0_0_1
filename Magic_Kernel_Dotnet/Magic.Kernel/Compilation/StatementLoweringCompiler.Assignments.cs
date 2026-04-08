using System;
using System.Collections.Generic;
using Magic.Kernel.Compilation.Ast;
using Magic.Kernel.Processor;

namespace Magic.Kernel.Compilation
{
    /// <summary>
    /// Assignment / compound JSON ops extracted from <see cref="StatementLoweringCompiler"/>.
    /// </summary>
    internal sealed partial class StatementLoweringCompiler
    {
        private bool TryCompileAssignment(
            string line,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int shapeCounter,
            ref int vertexCounter,
            ref int memorySlotCounter,
            List<InstructionNode> instructions)
        {
            var previous = _scanner;
            _scanner = new Scanner(line);
            try
            {
                if (IsIdentifier(CurrentScanner.Current, "var"))
                {
                    CurrentScanner.Scan();
                }

                if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                    return false;
                var targetName = CurrentScanner.Scan().Value;

                string? explicitRhsTypePrefix = null;

                // var name := …  OR  var name = …
                if (CurrentScanner.Current.Kind == TokenKind.Colon &&
                    CurrentScanner.Watch(1)?.Kind == TokenKind.Assign)
                {
                    CurrentScanner.Scan();
                    CurrentScanner.Scan();
                }
                else if (CurrentScanner.Current.Kind == TokenKind.Colon &&
                         CurrentScanner.Watch(1)?.Kind == TokenKind.Identifier)
                {
                    // var world: Room := { … };  →  тот же lowering, что и  var world := Room: { … };
                    CurrentScanner.Scan();
                    if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                        return false;
                    explicitRhsTypePrefix = CurrentScanner.Scan().Value;
                    if (string.IsNullOrEmpty(explicitRhsTypePrefix))
                        return false;
                    if (CurrentScanner.Current.Kind != TokenKind.Colon ||
                        CurrentScanner.Watch(1)?.Kind != TokenKind.Assign)
                        return false;
                    CurrentScanner.Scan();
                    CurrentScanner.Scan();
                }
                else if (CurrentScanner.Current.Kind == TokenKind.Assign)
                {
                    CurrentScanner.Scan();
                }
                else
                    return false;

                SkipSemicolon();
                if (CurrentScanner.Current.IsEndOfInput)
                    return true;

                var rhsText = ExtractRemainingExpressionText(line, CurrentScanner.Current.Start);

                if (explicitRhsTypePrefix != null)
                {
                    var rhsTrim = (rhsText ?? string.Empty).Trim();
                    if (rhsTrim.Length > 0)
                    {
                        var syntheticTyped = $"{explicitRhsTypePrefix}: {rhsTrim}";
                        if (TryCompileTypedJsonAssignment(targetName, syntheticTyped, vars, ref memorySlotCounter, instructions))
                            return true;
                    }
                    // Иначе — игнорируем аннотацию и пробуем обычные ветки (например var p: Point := new Point(1,2)).
                }

                // 1) Constructor sugar:
                //    - var p := new Point(1, 2);
                //    - var p := Point.Constructor(1, 2);
                //    - var p := Point;           // empty ctor
                //
                // We normalize the last two forms to "new Point(...)" and reuse TryCompileConstructorCall.
                if (TryRewriteConstructorSugar(targetName, rhsText, vars, ref memorySlotCounter, instructions))
                {
                    return true;
                }

                // database<provider, Db>> generic: use global Db> schema when available and emit defgen.
                if (TryCompileDatabaseGenericAssignment(targetName, rhsText, vars, ref memorySlotCounter, instructions))
                {
                    return true;
                }

                // 2) Typed JSON/new initializers:
                //    var world := Room: json: { Board: { ... }, Persons: [] };
                //    var world: Room := { Board: { ... }, Persons: [] };
                //    var a     := A:      { x: 1, y: 2 };          // json by default
                //    var a     := A: new: _ => { _.X = 1; };
                //
                // For now we support the JSON-based forms and compile them into:
                //   defobj Room in targetSlot + setobj per JSON field, using the existing JSON builder
                //   for field values. This routes object construction through DefObj/SetObj so runtime
                //   type metadata (DefType.Fields) is respected.
                if (TryCompileTypedJsonAssignment(targetName, rhsText, vars, ref memorySlotCounter, instructions))
                {
                    return true;
                }

                if (LooksLikeJsonLiteral(rhsText))
                {
                    // RHS of := : allow object literal with ; (e.g. a := { key: x; }). Only forbid ; in strict JSON.
                    if (TryParseObjectLiteralWithVarRefs(rhsText, out var keyVars) && keyVars != null && keyVars.Count > 0)
                    {
                        var targetSlot = memorySlotCounter++;
                        vars[targetName] = ("memory", targetSlot);
                        instructions.Add(CreatePushStringInstruction("{}"));
                        instructions.Add(CreatePopMemoryInstruction(targetSlot));
                        foreach (var (key, varName) in keyVars)
                        {
                            if (!vars.TryGetValue(varName, out var valVar) || (valVar.Kind != "memory" && valVar.Kind != "stream" && valVar.Kind != "def"))
                                return false;
                            EmitOpJsonCall(
                                targetSlot,
                                "set",
                                key,
                                instructions,
                                dataRef: new FunctionParameterNode
                                {
                                    Name = "data",
                                    ParameterName = "data",
                                    EntityType = "memory",
                                    Index = valVar.Index
                                });
                        }
                        return true;
                    }
                    if (HasSemicolonInsideJsonLiteral(rhsText))
                        throw new CompilationException("JSON literal cannot contain ';' inside object or array.", CurrentScanner.Current.Start);
                    if (TryParseJsonArgument(rhsText, out var jsonArg))
                    {
                        var targetSlot = memorySlotCounter++;
                        vars[targetName] = ("memory", targetSlot);

                        if (jsonArg is JsonArrayNode emptyArr && emptyArr.Items.Count == 0)
                        {
                            // Empty array literal: compile as DefList — push json:[]; push "list"; push 2; def; pop slot
                            instructions.Add(CreatePushStringInstruction("[]"));
                            instructions.Add(CreatePushStringInstruction("list"));
                            instructions.Add(CreatePushIntInstruction(2));
                            instructions.Add(new InstructionNode { Opcode = "def" });
                            instructions.Add(CreatePopMemoryInstruction(targetSlot));
                            return true;
                        }

                        // Bottom-up JSON build: initialise root container and emit opjson calls.
                        if (jsonArg is JsonObjectNode)
                        {
                            instructions.Add(CreatePushStringInstruction("{}"));
                            instructions.Add(CreatePopMemoryInstruction(targetSlot));
                        }
                        else if (jsonArg is JsonArrayNode)
                        {
                            instructions.Add(CreatePushStringInstruction("[]"));
                            instructions.Add(CreatePopMemoryInstruction(targetSlot));
                        }
                        else
                        {
                            // Primitive at top level – just store raw JSON.
                            instructions.Add(CreatePushStringInstruction(rhsText));
                            instructions.Add(CreatePopMemoryInstruction(targetSlot));
                            return true;
                        }

                        EmitBuildJsonNode(jsonArg, targetSlot, "", vars, instructions, ref memorySlotCounter);
                        return true;
                    }
                }

                var isNewMemoryBinding = false;
                if (!vars.TryGetValue(targetName, out var existingVar))
                {
                    var slot = memorySlotCounter++;
                    vars[targetName] = ("memory", slot);
                    existingVar = ("memory", slot);
                    isNewMemoryBinding = true;
                }

                if (existingVar.Kind == "vertex" || existingVar.Kind == "relation" || existingVar.Kind == "shape")
                {
                    return TryCompileEntityAssignment(line, targetName, vars, ref shapeCounter, ref vertexCounter, ref memorySlotCounter, instructions);
                }

                if (existingVar.Kind == "memory" || existingVar.Kind == "stream" || existingVar.Kind == "def" || existingVar.Kind == "global")
                {
                    // Глобалы: выражение во временный слот + assign.
                    if (existingVar.Kind == "global")
                    {
                        var valueSlot = memorySlotCounter++;
                        if (!TryCompileExpressionToSlot(rhsText, vars, valueSlot, ref memorySlotCounter, instructions))
                            return false;
                        instructions.Add(CreatePushGlobalMemoryInstruction(existingVar.Index));
                        instructions.Add(CreatePushMemoryInstruction(valueSlot));
                        instructions.Add(new InstructionNode { Opcode = "assign" });
                        instructions.Add(CreatePopMemoryInstruction(existingVar.Index));
                        return true;
                    }

                    // Новая var x := rhs — слот x уже выделен; писать сразу в него, без push/pop-копии из temp.
                    var exprTarget = isNewMemoryBinding
                        ? existingVar.Index
                        : memorySlotCounter++;
                    if (!TryCompileExpressionToSlot(rhsText, vars, exprTarget, ref memorySlotCounter, instructions))
                        return false;
                    if (!isNewMemoryBinding)
                    {
                        instructions.Add(CreatePushMemoryInstruction(exprTarget));
                        instructions.Add(CreatePopMemoryInstruction(existingVar.Index));
                    }
                    else if (!string.IsNullOrWhiteSpace(explicitRhsTypePrefix))
                    {
                        var ann = explicitRhsTypePrefix.Trim();
                        if (ann.Length > 0 && char.IsLetter(ann[0]) && char.IsUpper(ann[0]))
                            RememberMemoryVarDefType(targetName, QualifyTypeNameForDefObj(ann));
                    }
                    else if (TryInferQualifiedDefTypeFromSimpleMemberRhs(rhsText, vars, out var inferredFq))
                        RememberMemoryVarDefType(targetName, inferredFq);

                    return true;
                }

                return false;
            }
            finally
            {
                _scanner = previous;
            }
        }

        /// <summary>
        /// Handles constructor sugar on the RHS of an assignment:
        ///   var p := new Point(1, 2);        – existing form
        ///   var p := Point.Constructor(1,2); – rewrites to new Point(1,2)
        ///   var p := Point;                  – rewrites to new Point()
        /// and then delegates to <see cref="TryCompileConstructorCall"/>.
        /// </summary>
        private bool TryRewriteConstructorSugar(
            string targetName,
            string rhsExpression,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int memorySlotCounter,
            List<InstructionNode> instructions)
        {
            var trimmed = (rhsExpression ?? string.Empty).Trim();
            if (trimmed.Length == 0)
                return false;

            // Already a normalized "new Type(...)" form – let the existing ctor pipeline handle it.
            if (trimmed.StartsWith("new", StringComparison.OrdinalIgnoreCase))
            {
                return TryCompileConstructorCall(targetName, trimmed, vars, ref memorySlotCounter, instructions);
            }

            // A.Constructor(args)
            const string ctorSuffix = ".Constructor(";
            var ctorIdx = trimmed.IndexOf(ctorSuffix, StringComparison.Ordinal);
            if (ctorIdx > 0 && trimmed.EndsWith(")", StringComparison.Ordinal))
            {
                var typeName = trimmed.Substring(0, ctorIdx).Trim();
                if (!string.IsNullOrEmpty(typeName))
                {
                    var argsText = trimmed.Substring(ctorIdx + ctorSuffix.Length,
                        trimmed.Length - (ctorIdx + ctorSuffix.Length) - 1);
                    var rewritten = $"new {typeName}({argsText})";
                    return TryCompileConstructorCall(targetName, rewritten, vars, ref memorySlotCounter, instructions);
                }
            }

            // Type(args) — same as new Type(args) when Type looks like a type name (not a function call).
            var openParen = trimmed.IndexOf('(');
            if (openParen > 0 && trimmed.EndsWith(")", StringComparison.Ordinal))
            {
                var typeNameCall = trimmed.Substring(0, openParen).Trim();
                if (IsLikelyTypeIdentifier(typeNameCall))
                {
                    var argsTextCall = trimmed.Substring(openParen + 1, trimmed.Length - openParen - 2);
                    var rewrittenCall = $"new {typeNameCall}({argsTextCall})";
                    return TryCompileConstructorCall(targetName, rewrittenCall, vars, ref memorySlotCounter, instructions);
                }
            }

            // Bare PascalCase identifier: treat as empty ctor "new Type()".
            if (IsLikelyTypeIdentifier(trimmed))
            {
                var rewritten = $"new {trimmed}()";
                return TryCompileConstructorCall(targetName, rewritten, vars, ref memorySlotCounter, instructions);
            }

            return false;
        }

        /// <summary>
        /// Very small heuristic: identifier that looks like a type name (PascalCase, no spaces or punctuation).
        /// </summary>
        private static bool IsLikelyTypeIdentifier(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            text = text.Trim();
            if (!char.IsLetter(text[0]) || char.IsLower(text[0]))
                return false;
            for (var i = 1; i < text.Length; i++)
            {
                var ch = text[i];
                if (!(char.IsLetterOrDigit(ch) || ch == '_'))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Typed JSON object initializer on the RHS of :=:
        ///   var world := Room: json: { Board: {...}, Persons: [] };
        ///   var world: Room := { Board: {...}, Persons: [] };
        ///   var a     := A:      { X: 1, Y: 2 };    // json implied
        ///
        /// Compiles as:
        ///   - push class; defobj; pop [obj]
        ///   - build JSON literal into [json] (TryParseJsonArgument / EmitBuildJsonNode)
        ///   - push class; push "json"; push [obj]; push [json]; push 4; call materialize; pop
        /// Runtime: <see cref="Magic.Kernel.Core.OS.Hal.Materialize"/> + return value on stack (same DefObject).
        /// </summary>
        private bool TryCompileTypedJsonAssignment(
            string targetName,
            string rhsExpression,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int memorySlotCounter,
            List<InstructionNode> instructions)
        {
            var trimmed = (rhsExpression ?? string.Empty).Trim();
            if (trimmed.Length == 0)
                return false;

            var scanner = new Scanner(trimmed);
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;

            var typeName = scanner.Scan().Value;
            if (string.IsNullOrEmpty(typeName))
                return false;

            if (scanner.Current.Kind != TokenKind.Colon)
                return false;
            scanner.Scan(); // consume ':'

            // Optional "json:" / "new:" discriminator, default is json.
            var mode = "json";
            if (scanner.Current.Kind == TokenKind.Identifier)
            {
                var ident = scanner.Scan().Value;
                if (string.Equals(ident, "json", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ident, "new", StringComparison.OrdinalIgnoreCase))
                {
                    mode = ident.ToLowerInvariant();
                    if (scanner.Current.Kind == TokenKind.Colon)
                        scanner.Scan();
                }
                else
                {
                    // Not a known discriminator, so this is more likely "Field: expr" — bail out.
                    return false;
                }
            }

            if (!string.Equals(mode, "json", StringComparison.OrdinalIgnoreCase))
            {
                // "Type: new: _ => { ... }" is reserved for a future lambda-based initializer.
                // For now we don't handle it here.
                return false;
            }

            var bodyStart = scanner.Current.Start;
            if (bodyStart < 0 || bodyStart >= trimmed.Length)
                return false;

            var jsonText = trimmed.Substring(bodyStart).Trim();
            if (jsonText.Length == 0)
                return false;

            if (!LooksLikeJsonLiteral(jsonText))
                return false;

            if (HasSemicolonInsideJsonLiteral(jsonText))
                throw new CompilationException("Typed JSON literal cannot contain ';' inside object or array.", scanner.Current.Start);

            if (!TryParseJsonArgument(jsonText, out var rootNode))
                return false;

            if (rootNode is not JsonObjectNode)
                return false; // typed JSON root must be an object

            // Allocate slot for the typed object and bind the variable.
            var targetSlot = memorySlotCounter++;
            vars[targetName] = ("memory", targetSlot);

            var qualifiedType = QualifyTypeNameForDefObj(typeName);
            instructions.Add(CreatePushClassInstruction(qualifiedType));
            instructions.Add(new InstructionNode { Opcode = "defobj" });
            instructions.Add(CreatePopMemoryInstruction(targetSlot));

            var jsonSlot = memorySlotCounter++;
            instructions.Add(CreatePushStringInstruction("{}"));
            instructions.Add(CreatePopMemoryInstruction(jsonSlot));
            EmitBuildJsonNode(rootNode, jsonSlot, "", vars, instructions, ref memorySlotCounter);

            instructions.Add(CreatePushClassInstruction(qualifiedType));
            instructions.Add(CreatePushStringInstruction("json"));
            instructions.Add(CreatePushMemoryInstruction(targetSlot));
            instructions.Add(CreatePushMemoryInstruction(jsonSlot));
            instructions.Add(CreatePushIntInstruction(4));
            instructions.Add(CreateCallInstruction("materialize"));
            instructions.Add(CreatePopInstruction());

            RememberMemoryVarDefType(targetName, qualifiedType);
            return true;
        }

        private bool TryCompilePlusAssign(
            string line,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int memorySlotCounter,
            List<InstructionNode> instructions)
        {
            var opIdx = line.IndexOf("+=", StringComparison.Ordinal);
            if (opIdx <= 0)
                return false;

            var left = line.Substring(0, opIdx).Trim();
            var right = line.Substring(opIdx + 2).Trim();
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;
            if (right.EndsWith(";", StringComparison.Ordinal))
                right = right.Substring(0, right.Length - 1).Trim();

            if (!TryParseJsonArgument(right, out var jsonArg))
                return false;

            var dotIdx = left.IndexOf('.', StringComparison.Ordinal);
            if (dotIdx <= 0 || dotIdx >= left.Length - 1)
                return false;

            var rootName = left.Substring(0, dotIdx).Trim();
            var memberPath = left.Substring(dotIdx + 1).Trim();
            if (!vars.TryGetValue(rootName, out var rootVar))
                throw new UndeclaredVariableException(rootName);
            if (rootVar.Kind != "memory" && rootVar.Kind != "stream" && rootVar.Kind != "def")
                return true;

            long objectSlot;
            var useExistingValueSlot = false;
            if (jsonArg is JsonIdentifierNode idNode &&
                vars.TryGetValue(idNode.Name, out var existingVar) &&
                (existingVar.Kind == "memory" || existingVar.Kind == "stream" || existingVar.Kind == "def"))
            {
                objectSlot = existingVar.Index;
                useExistingValueSlot = true;
            }
            else
            {
                objectSlot = memorySlotCounter++;
            }
            var collectionSlot = memorySlotCounter++;
            var discardSlot = memorySlotCounter++;

            if (!useExistingValueSlot)
            {
                var init = jsonArg is JsonArrayNode ? "[]" : "{}";
                instructions.Add(CreatePushStringInstruction(init));
                instructions.Add(CreatePopMemoryInstruction(objectSlot));
                EmitBuildJsonNode(jsonArg, objectSlot, "", vars, instructions, ref memorySlotCounter);
            }

            instructions.Add(CreatePushMemoryInstruction(rootVar.Index));
            instructions.Add(CreatePushStringInstruction(memberPath));
            instructions.Add(new InstructionNode { Opcode = "getobj" });
            instructions.Add(CreatePopMemoryInstruction(collectionSlot));

            instructions.Add(CreatePushMemoryInstruction(collectionSlot));
            instructions.Add(CreatePushMemoryInstruction(objectSlot));
            instructions.Add(CreatePushIntInstruction(1));
            instructions.Add(new InstructionNode
            {
                Opcode = "callobj",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = "add" }
                }
            });
            instructions.Add(CreatePopMemoryInstruction(collectionSlot));

            instructions.Add(CreatePushMemoryInstruction(rootVar.Index));
            instructions.Add(CreatePushStringInstruction(memberPath));
            instructions.Add(CreatePushMemoryInstruction(collectionSlot));
            instructions.Add(new InstructionNode { Opcode = "setobj" });
            instructions.Add(CreatePopMemoryInstruction(discardSlot));
            return true;
        }

        private bool TryCompileMultiplyAssign(
            string line,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int memorySlotCounter,
            List<InstructionNode> instructions)
        {
            var opIdx = line.IndexOf("*=", StringComparison.Ordinal);
            if (opIdx <= 0)
                return false;

            var left = line.Substring(0, opIdx).Trim();
            var right = line.Substring(opIdx + 2).Trim();
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;
            if (right.EndsWith(";", StringComparison.Ordinal))
                right = right.Substring(0, right.Length - 1).Trim();

            if (!TryParseJsonArgument(right, out var jsonArg))
                return false;

            var dotIdx = left.IndexOf('.', StringComparison.Ordinal);
            if (dotIdx <= 0 || dotIdx >= left.Length - 1)
                return false;

            var rootName = left.Substring(0, dotIdx).Trim();
            var memberPath = left.Substring(dotIdx + 1).Trim();
            if (!vars.TryGetValue(rootName, out var rootVar))
                throw new UndeclaredVariableException(rootName);
            if (rootVar.Kind != "memory" && rootVar.Kind != "stream" && rootVar.Kind != "def")
                return true;

            long objectSlot;
            var useExistingValueSlot = false;
            if (jsonArg is JsonIdentifierNode idNode &&
                vars.TryGetValue(idNode.Name, out var existingVar) &&
                (existingVar.Kind == "memory" || existingVar.Kind == "stream" || existingVar.Kind == "def"))
            {
                objectSlot = existingVar.Index;
                useExistingValueSlot = true;
            }
            else
            {
                objectSlot = memorySlotCounter++;
            }
            var collectionSlot = memorySlotCounter++;
            var discardSlot = memorySlotCounter++;

            if (!useExistingValueSlot)
            {
                var init = jsonArg is JsonArrayNode ? "[]" : "{}";
                instructions.Add(CreatePushStringInstruction(init));
                instructions.Add(CreatePopMemoryInstruction(objectSlot));
                EmitBuildJsonNode(jsonArg, objectSlot, "", vars, instructions, ref memorySlotCounter);
            }

            instructions.Add(CreatePushMemoryInstruction(rootVar.Index));
            instructions.Add(CreatePushStringInstruction(memberPath));
            instructions.Add(new InstructionNode { Opcode = "getobj" });
            instructions.Add(CreatePopMemoryInstruction(collectionSlot));

            instructions.Add(CreatePushMemoryInstruction(collectionSlot));
            instructions.Add(CreatePushMemoryInstruction(objectSlot));
            instructions.Add(CreatePushIntInstruction(1));
            instructions.Add(new InstructionNode
            {
                Opcode = "callobj",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { FunctionName = "mul" }
                }
            });
            instructions.Add(CreatePopMemoryInstruction(collectionSlot));

            instructions.Add(CreatePushMemoryInstruction(rootVar.Index));
            instructions.Add(CreatePushStringInstruction(memberPath));
            instructions.Add(CreatePushMemoryInstruction(collectionSlot));
            instructions.Add(new InstructionNode { Opcode = "setobj" });
            instructions.Add(CreatePopMemoryInstruction(discardSlot));
            return true;
        }

        private bool TryCompileMemberAssignment(
            string line,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int memorySlotCounter,
            List<InstructionNode> instructions)
        {
            var scanner = new Scanner(line);
            if (IsIdentifier(scanner.Current, "var"))
                return false;
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;

            var rootName = scanner.Scan().Value;
            var pathSegments = new List<string>();
            while (true)
            {
                if (scanner.Current.Kind == TokenKind.Identifier && scanner.Current.Value == "!")
                    scanner.Scan();
                if (scanner.Current.Kind != TokenKind.Dot)
                    break;
                scanner.Scan();
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                pathSegments.Add(scanner.Scan().Value);
            }

            if (pathSegments.Count == 0)
                return false;

            var hasAssign = scanner.Current.Kind == TokenKind.Assign;
            var hasDefineAssign = scanner.Current.Kind == TokenKind.Colon && scanner.Watch(1)?.Kind == TokenKind.Assign;
            if (!hasAssign && !hasDefineAssign)
                return false;

            scanner.Scan();
            if (hasDefineAssign)
                scanner.Scan();

            var rhsStart = scanner.Current.Start;
            var rhsEnd = rhsStart;
            if (scanner.Current.Kind == TokenKind.LBrace)
            {
                var depth = 1;
                scanner.Scan();
                while (!scanner.Current.IsEndOfInput && depth > 0)
                {
                    if (scanner.Current.Kind == TokenKind.LBrace) depth++;
                    else if (scanner.Current.Kind == TokenKind.RBrace) depth--;
                    rhsEnd = scanner.Current.End;
                    scanner.Scan();
                }
            }
            else
            {
                while (!scanner.Current.IsEndOfInput && scanner.Current.Kind != TokenKind.Semicolon)
                {
                    rhsEnd = scanner.Current.End;
                    scanner.Scan();
                }
            }
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
            if (!scanner.Current.IsEndOfInput)
                return false;

            if (!vars.TryGetValue(rootName, out var rootVar))
                throw new UndeclaredVariableException(rootName);
            if (rootVar.Kind != "memory" && rootVar.Kind != "stream" && rootVar.Kind != "def")
                return true;

            if (rhsEnd <= rhsStart || rhsStart < 0 || rhsEnd > line.Length)
                return true;

            var rhsText = line.Substring(rhsStart, rhsEnd - rhsStart).Trim();
            if (string.IsNullOrWhiteSpace(rhsText))
                return true;

            instructions.Add(CreatePushMemoryInstruction(rootVar.Index));
            for (var i = 0; i < pathSegments.Count - 1; i++)
            {
                instructions.Add(CreatePushStringInstruction(pathSegments[i]));
                instructions.Add(new InstructionNode { Opcode = "getobj" });
            }
            instructions.Add(CreatePushStringInstruction(pathSegments[pathSegments.Count - 1]));
            if (!TryEmitValuePush(rhsText, vars, ref memorySlotCounter, instructions))
                return true;
            instructions.Add(new InstructionNode { Opcode = "setobj" });
            instructions.Add(CreatePopMemoryInstruction(memorySlotCounter++));
            return true;
        }

        private bool TryEmitValuePush(
            string valueText,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int memorySlotCounter,
            List<InstructionNode> instructions)
        {
            var trimmed = valueText.Trim();

            if (TryParseFormatStringLiteral(trimmed, out var formatTemplate, out var formatExpressions))
            {
                var parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode { Name = "function", FunctionName = "format" },
                    new StringParameterNode { Name = "0", Value = formatTemplate }
                };
                for (var i = 0; i < formatExpressions.Count; i++)
                {
                    var argumentSlot = memorySlotCounter++;
                    if (!TryCompileExpressionToSlot(formatExpressions[i], vars, argumentSlot, ref memorySlotCounter, instructions))
                        return false;

                    parameters.Add(new FunctionParameterNode
                    {
                        Name = (i + 1).ToString(),
                        ParameterName = (i + 1).ToString(),
                        EntityType = "memory",
                        Index = argumentSlot
                    });
                }
                instructions.Add(new InstructionNode { Opcode = "call", Parameters = parameters });
                return true;
            }

            // JSON only in valid context: RHS of := or inside (). Object literal with ; (e.g. { data: photoData; }) is allowed.
            if (LooksLikeJsonLiteral(trimmed))
            {
                // Prefer object literal with var refs (allows ; as separator). Only forbid ; in strict JSON literal.
                if (TryParseObjectLiteralWithVarRefs(trimmed, out var keyVars) && keyVars != null && keyVars.Count > 0)
                {
                    var objSlot = memorySlotCounter++;
                    instructions.Add(CreatePushStringInstruction("{}"));
                    instructions.Add(CreatePopMemoryInstruction(objSlot));
                    foreach (var (key, varName) in keyVars)
                    {
                        if (!vars.TryGetValue(varName, out var valVar) || (valVar.Kind != "memory" && valVar.Kind != "stream" && valVar.Kind != "def"))
                            return false;
                        EmitOpJsonCall(
                            objSlot,
                            "set",
                            key,
                            instructions,
                            dataRef: new FunctionParameterNode
                            {
                                Name = "data",
                                ParameterName = "data",
                                EntityType = "memory",
                                Index = valVar.Index
                            });
                    }
                    instructions.Add(CreatePushMemoryInstruction(objSlot));
                    return true;
                }

                if (HasSemicolonInsideJsonLiteral(trimmed))
                    throw new CompilationException("JSON literal cannot contain ';' inside object or array.", -1);

                if (TryParseJsonArgument(trimmed, out var jsonArg))
                {
                    var targetSlot = memorySlotCounter++;
                    var initJson = jsonArg is JsonArrayNode ? "[]" : "{}";
                    instructions.Add(CreatePushStringInstruction(initJson));
                    instructions.Add(CreatePopMemoryInstruction(targetSlot));
                    EmitBuildJsonNode(jsonArg, targetSlot, "", vars, instructions, ref memorySlotCounter);
                    instructions.Add(CreatePushMemoryInstruction(targetSlot));
                    return true;
                }
            }

            var scanner = new Scanner(valueText);
            if (scanner.Current.Kind == TokenKind.StringLiteral)
            {
                var literal = scanner.Scan().Value;
                while (scanner.Current.Kind == TokenKind.Semicolon)
                    scanner.Scan();
                if (!scanner.Current.IsEndOfInput)
                    return false;
                instructions.Add(CreatePushStringInstruction(literal));
                return true;
            }

            if (scanner.Current.Kind == TokenKind.Number && long.TryParse(scanner.Current.Value, out var intLiteral))
            {
                scanner.Scan();
                while (scanner.Current.Kind == TokenKind.Semicolon)
                    scanner.Scan();
                if (!scanner.Current.IsEndOfInput)
                    return false;
                instructions.Add(CreatePushIntInstruction(intLiteral));
                return true;
            }

            if (scanner.Current.Kind == TokenKind.Identifier)
            {
                var baseName = scanner.Scan().Value;
                var segments = new List<string>();
                while (true)
                {
                    if (scanner.Current.Kind == TokenKind.Identifier && scanner.Current.Value == "!")
                        scanner.Scan();
                    if (scanner.Current.Kind != TokenKind.Dot)
                        break;
                    scanner.Scan();
                    if (scanner.Current.Kind != TokenKind.Identifier)
                        return false;
                    segments.Add(scanner.Scan().Value);
                }
                while (scanner.Current.Kind == TokenKind.Semicolon)
                    scanner.Scan();
                if (!scanner.Current.IsEndOfInput)
                    return false;

                if (!vars.TryGetValue(baseName, out var baseVar))
                    throw new UndeclaredVariableException(baseName);
                if (baseVar.Kind != "memory" && baseVar.Kind != "stream" && baseVar.Kind != "def" && baseVar.Kind != "global")
                    return false;

                instructions.Add(baseVar.Kind == "global"
                    ? CreatePushGlobalMemoryInstruction(baseVar.Index)
                    : CreatePushMemoryInstruction(baseVar.Index));
                foreach (var segment in segments)
                {
                    instructions.Add(CreatePushStringInstruction(segment));
                    instructions.Add(new InstructionNode { Opcode = "getobj" });
                }
                return true;
            }

            return false;
        }

        private bool TryCompileDatabaseGenericAssignment(
            string targetName,
            string rhsText,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int memorySlotCounter,
            List<InstructionNode> instructions)
        {
            var trimmed = rhsText.Trim();
            if (!trimmed.StartsWith("database", StringComparison.OrdinalIgnoreCase))
                return false;

            var scanner = new Scanner(trimmed);
            if (!IsIdentifier(scanner.Current, "database"))
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.LessThan)
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            var providerTypeName = scanner.Scan().Value;
            if (scanner.Current.Kind != TokenKind.Comma)
                return false;
            scanner.Scan();
            if (scanner.Current.Kind != TokenKind.Identifier)
                return false;
            var schemaName = scanner.Scan().Value;
            if (scanner.Current.Kind != TokenKind.GreaterThan)
                return false;

            // В прелюде схемы БД могут объявляться с суффиксом ">" или "<>",
            // например: "Db> : database { ... }", "Messages<> : table { ... }".
            // При этом generic‑выражение в коде использует форму database<postgres, Db>>,
            // где сканер читает идентификатор "Db", а символы '>' идут отдельными токенами.
            // Чтобы корректно связать generic‑выражение с уже объявленной схемой,
            // пытаемся найти как точное имя, так и варианты с ">" / "<>".
            // Если схемы нет в vars, трактуем второй аргумент как чисто типовой:
            // database<postgres, Db>> даёт тип "db>" без обязательного runtime‑слота.
            (bool hasSchemaVar, (string Kind, int Index) schemaVar) schemaLookup;
            if (vars.TryGetValue(schemaName, out var direct))
            {
                schemaLookup = (true, direct);
            }
            else if (vars.TryGetValue(schemaName + ">", out var withArrow))
            {
                schemaLookup = (true, withArrow);
            }
            else if (vars.TryGetValue(schemaName + "<>", out var withGeneric))
            {
                schemaLookup = (true, withGeneric);
            }
            else
            {
                schemaLookup = (false, default);
            }

            var baseSlot = memorySlotCounter++;
            var dbSlot = memorySlotCounter++;
            vars[targetName] = ("memory", dbSlot);

            // def database
            instructions.Add(CreatePushTypeInstruction("database"));
            instructions.Add(CreatePushIntInstruction(1));
            instructions.Add(new InstructionNode { Opcode = "def" });
            instructions.Add(CreatePopMemoryInstruction(baseSlot));

            // defgen database<provider, Db>>
            instructions.Add(CreatePushMemoryInstruction(baseSlot));
            instructions.Add(CreatePushTypeInstruction(providerTypeName));
            if (schemaLookup.hasSchemaVar)
            {
                var sv = schemaLookup.schemaVar;
                instructions.Add(sv.Kind == "global"
                    ? CreatePushGlobalMemoryInstruction(sv.Index)
                    : CreatePushMemoryInstruction(sv.Index));
            }
            else
            {
                // Нет явной runtime‑схемы: используем чисто типовое имя с ровно одним ">" на конце.
                // Примеры:
                //   "Db"   -> "db>"
                //   "Db>"  -> "db>"
                //   "Db>>" -> "db>"
                var core = schemaName.TrimEnd('>');
                var schemaTypeName = (core + ">").ToLowerInvariant();
                instructions.Add(CreatePushTypeInstruction(schemaTypeName));
            }
            instructions.Add(CreatePushIntInstruction(2));
            instructions.Add(new InstructionNode { Opcode = "defgen" });
            instructions.Add(CreatePopMemoryInstruction(dbSlot));

            return true;
        }

        private bool TryCompileEntityAssignment(
            string line,
            string targetName,
            Dictionary<string, (string Kind, int Index)> vars,
            ref int shapeCounter,
            ref int vertexCounter,
            ref int memorySlotCounter,
            List<InstructionNode> instructions)
        {
            if (!vars.TryGetValue(targetName, out var existingVar))
                return false;
            if (existingVar.Kind != "vertex" && existingVar.Kind != "relation" && existingVar.Kind != "shape")
                return false;

            var previous = _scanner;
            _scanner = new Scanner(line);
            try
            {
                if (IsIdentifier(CurrentScanner.Current, "var"))
                    CurrentScanner.Scan();

                if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                    return false;

                var ident = CurrentScanner.Scan().Value;
                if (!string.Equals(ident, targetName, StringComparison.OrdinalIgnoreCase))
                    return false;

                var hasAssign = CurrentScanner.Current.Kind == TokenKind.Assign;
                var hasDefineAssign = CurrentScanner.Current.Kind == TokenKind.Colon && CurrentScanner.Watch(1)?.Kind == TokenKind.Assign;
                if (!hasAssign && !hasDefineAssign)
                    return false;

                if (hasDefineAssign)
                {
                    CurrentScanner.Scan();
                    CurrentScanner.Scan();
                }
                else
                {
                    CurrentScanner.Scan();
                }

                SkipSemicolon();
                if (CurrentScanner.Current.IsEndOfInput)
                    return false;

                var rhsText = ExtractRemainingExpressionText(line, CurrentScanner.Current.Start);
                if (string.IsNullOrWhiteSpace(rhsText))
                    return false;

                if (existingVar.Kind == "vertex")
                {
                    if (!TryParseVertexObject(rhsText, out var spec))
                        return false;

                    var index = existingVar.Index;
                    var vertexInstr = new List<InstructionNode>();
                    var parameters = new List<ParameterNode>
                    {
                        new IndexParameterNode { Name = "index", Value = index },
                        new DimensionsParameterNode { Name = "dimensions", Values = spec.Dimensions.ConvertAll(v => (float)v) },
                        new WeightParameterNode { Name = "weight", Value = (float)spec.Weight }
                    };

                    if (!string.IsNullOrEmpty(spec.BinaryData))
                    {
                        parameters.Add(new DataParameterNode
                        {
                            Name = "data",
                            Type = "binary:base64",
                            Types = new List<string> { "binary", "base64" },
                            Value = spec.BinaryData,
                            HasColon = true
                        });
                    }
                    else if (!string.IsNullOrEmpty(spec.TextData))
                    {
                        parameters.Add(new DataParameterNode
                        {
                            Name = "data",
                            Type = "text",
                            Types = new List<string> { "text" },
                            Value = spec.TextData,
                            HasColon = true
                        });
                    }

                    vertexInstr.Add(new InstructionNode { Opcode = "addvertex", Parameters = parameters });
                    instructions.AddRange(vertexInstr);
                    return true;
                }

                if (existingVar.Kind == "relation")
                {
                    if (!TryParseRelationObject(rhsText, out var spec) || string.IsNullOrEmpty(spec.From) || string.IsNullOrEmpty(spec.To))
                        return false;

                    if (!vars.TryGetValue(spec.From, out var fromVar))
                        throw new UndeclaredVariableException(spec.From);
                    if (!vars.TryGetValue(spec.To, out var toVar))
                        throw new UndeclaredVariableException(spec.To);

                    var fromType = fromVar.Kind == "relation" ? "relation" : "vertex";
                    var toType = toVar.Kind == "relation" ? "relation" : "vertex";

                    instructions.Add(new InstructionNode
                    {
                        Opcode = "addrelation",
                        Parameters = new List<ParameterNode>
                        {
                            new IndexParameterNode { Name = "index", Value = existingVar.Index },
                            new FromParameterNode { Name = "from", EntityType = fromType, Index = fromVar.Index },
                            new ToParameterNode { Name = "to", EntityType = toType, Index = toVar.Index },
                            new WeightParameterNode { Name = "weight", Value = (float)spec.Weight }
                        }
                    });
                    return true;
                }

                if (existingVar.Kind == "shape")
                {
                    if (!TryParseShapeObject(rhsText, out var spec))
                        return false;

                    var index = existingVar.Index;
                    var indices = new List<long>();

                    if (spec.UsesInlineVertices)
                    {
                        foreach (var vertex in spec.InlineVertices)
                        {
                            var tempIndex = vertexCounter++;
                            var parameters = new List<ParameterNode>
                            {
                                new IndexParameterNode { Name = "index", Value = tempIndex },
                                new DimensionsParameterNode { Name = "dimensions", Values = vertex.Dimensions.ConvertAll(v => (float)v) }
                            };
                            if (vertex.Weight.HasValue)
                                parameters.Add(new WeightParameterNode { Name = "weight", Value = (float)vertex.Weight.Value });
                            instructions.Add(new InstructionNode { Opcode = "addvertex", Parameters = parameters });
                            indices.Add(tempIndex);
                        }
                    }
                    else
                    {
                        foreach (var name in spec.VertexNames)
                        {
                            if (vars.TryGetValue(name, out var vertexVar) && vertexVar.Kind == "vertex")
                                indices.Add(vertexVar.Index);
                        }
                    }

                    if (indices.Count > 0)
                    {
                        instructions.Add(new InstructionNode
                        {
                            Opcode = "addshape",
                            Parameters = new List<ParameterNode>
                            {
                                new IndexParameterNode { Name = "index", Value = index },
                                new VerticesParameterNode { Name = "vertices", Indices = indices }
                            }
                        });
                    }

                    return true;
                }

                return false;
            }
            finally
            {
                _scanner = previous;
            }
        }
    }
}
