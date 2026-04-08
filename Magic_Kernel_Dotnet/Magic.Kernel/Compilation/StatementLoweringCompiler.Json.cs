using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Magic.Kernel.Compilation.Ast;
using Magic.Kernel.Processor;

namespace Magic.Kernel.Compilation
{
    internal sealed partial class StatementLoweringCompiler
    {
        private static bool LooksLikeJsonLiteral(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var trimmed = text.TrimStart();
            return trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal);
        }

        /// <summary>Returns true if there is a semicolon inside the top-level JSON object or array body (between matching { } or [ ]).</summary>
        private static bool HasSemicolonInsideJsonLiteral(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var t = text.Trim();
            if (t.Length < 2 || (t[0] != '{' && t[0] != '['))
                return false;
            var depth = 1;
            var inString = false;
            var escape = false;
            var quote = '\0';
            for (var i = 1; i < t.Length && depth > 0; i++)
            {
                var ch = t[i];
                if (inString)
                {
                    if (escape) { escape = false; continue; }
                    if (ch == '\\') { escape = true; continue; }
                    if (ch == quote) { inString = false; continue; }
                    continue;
                }
                if (ch == '"' || ch == '\'')
                {
                    inString = true;
                    quote = ch;
                    continue;
                }
                if (ch == '{' || ch == '[') { depth++; continue; }
                if (ch == '}' || ch == ']') { depth--; continue; }
                if (ch == ';' && depth >= 1)
                    return true;
            }
            return false;
        }

        private static string AppendPath(string basePath, string segment)
        {
            if (string.IsNullOrEmpty(basePath))
                return segment;
            if (segment.StartsWith("[", StringComparison.Ordinal))
                return basePath + segment;
            return basePath + "." + segment;
        }

        private static string JsonLiteralToRaw(object? value)
        {
            if (value == null)
                return "null";
            if (value is bool b)
                return b ? "true" : "false";
            if (value is long l)
                return l.ToString(CultureInfo.InvariantCulture);
            if (value is double d)
                return d.ToString(CultureInfo.InvariantCulture);
            if (value is string s)
                return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            return "\"" + value.ToString()?.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static void EmitOpJsonCall(long sourceIndex, string operation, string path, List<InstructionNode> instructions, FunctionParameterNode? dataRef = null, string? dataJson = null)
        {
            var parameters = new List<ParameterNode>
            {
                new FunctionNameParameterNode { Name = "function", FunctionName = "opjson" },
                new FunctionParameterNode { Name = "source", ParameterName = "source", EntityType = "memory", Index = sourceIndex },
                new StringParameterNode { Name = "operation", Value = operation },
                new StringParameterNode { Name = "path", Value = path }
            };

            if (dataRef != null)
                parameters.Add(dataRef);
            if (dataJson != null)
                parameters.Add(new StringParameterNode { Name = "dataJson", Value = dataJson });

            instructions.Add(new InstructionNode { Opcode = "call", Parameters = parameters });
            // opjson возвращает корень JSON на вершину стека.
            // Везде, где мы его эмитим из компилятора, он используется только по side‑effect'у,
            // поэтому сразу очищаем стек от результата.
            instructions.Add(CreatePopInstruction());
        }

        private static void EmitBuildJsonNode(JsonArgumentNode node, long sourceIndex, string path, Dictionary<string, (string Kind, int Index)> vars, List<InstructionNode> instructions)
        {
            var unused = 0;
            EmitBuildJsonNode(node, sourceIndex, path, vars, instructions, ref unused);
        }

        private static void EmitBuildJsonNode(JsonArgumentNode node, long sourceIndex, string path, Dictionary<string, (string Kind, int Index)> vars, List<InstructionNode> instructions, ref int memorySlotCounter)
        {
            if (node is JsonObjectNode objNode)
            {
                if (!string.IsNullOrEmpty(path))
                {
                    var objSlot = memorySlotCounter++;
                    instructions.Add(CreatePushStringInstruction("{}"));
                    instructions.Add(CreatePopMemoryInstruction(objSlot));
                    EmitBuildObjectProperties(objNode, objSlot, vars, instructions, ref memorySlotCounter);
                    EmitOpJsonCall(sourceIndex, "set", path, instructions,
                        dataRef: new FunctionParameterNode { Name = "data", ParameterName = "data", EntityType = "memory", Index = objSlot });
                }
                else
                {
                    EmitBuildObjectProperties(objNode, sourceIndex, vars, instructions, ref memorySlotCounter);
                }
                return;
            }

            if (node is JsonArrayNode arrNode)
            {
                if (!string.IsNullOrEmpty(path))
                {
                    var arrSlot = memorySlotCounter++;
                    instructions.Add(CreatePushStringInstruction("[]"));
                    instructions.Add(CreatePopMemoryInstruction(arrSlot));
                    EmitBuildArrayItems(arrNode, arrSlot, vars, instructions, ref memorySlotCounter);
                    EmitOpJsonCall(sourceIndex, "set", path, instructions,
                        dataRef: new FunctionParameterNode { Name = "data", ParameterName = "data", EntityType = "memory", Index = arrSlot });
                }
                else
                {
                    EmitBuildArrayItems(arrNode, sourceIndex, vars, instructions, ref memorySlotCounter);
                }
                return;
            }

            if (node is JsonIdentifierNode id && TryResolveJsonIdentifierToSlot(id, vars, instructions, ref memorySlotCounter, out var dataSlot))
            {
                EmitOpJsonCall(
                    sourceIndex,
                    "set",
                    path,
                    instructions,
                    dataRef: new FunctionParameterNode { Name = "data", ParameterName = "data", EntityType = "memory", Index = dataSlot });
                return;
            }

            if (node is JsonSymbolicNode symbolic)
            {
                var tmpSlot = memorySlotCounter++;
                instructions.Add(CreatePushStringInstruction(":" + symbolic.SymbolicName));
                instructions.Add(CreatePushIntInstruction(1));
                instructions.Add(CreateCallInstruction("get"));
                instructions.Add(CreatePopMemoryInstruction(tmpSlot));
                EmitOpJsonCall(
                    sourceIndex,
                    "set",
                    path,
                    instructions,
                    dataRef: new FunctionParameterNode { Name = "data", ParameterName = "data", EntityType = "memory", Index = tmpSlot });
                return;
            }

            if (node is JsonPrimitiveNode primitive)
            {
                EmitOpJsonCall(sourceIndex, "set", path, instructions, dataJson: JsonLiteralToRaw(primitive.Value));
            }
        }

        private static bool TryResolveJsonIdentifierToSlot(
            JsonIdentifierNode idNode,
            Dictionary<string, (string Kind, int Index)> vars,
            List<InstructionNode> instructions,
            ref int memorySlotCounter,
            out int valueSlot)
        {
            valueSlot = -1;
            if (vars.TryGetValue(idNode.Name, out var directVar))
            {
                valueSlot = directVar.Index;
                return true;
            }

            if (!TryParseMemberAccessFromString(idNode.Name, out var memberRoot, out var memberPath))
                return false;
            if (!vars.TryGetValue(memberRoot, out var memberVar))
                return false;

            valueSlot = memorySlotCounter++;
            instructions.Add(memberVar.Kind == "global"
                ? CreatePushGlobalMemoryInstruction(memberVar.Index)
                : CreatePushMemoryInstruction(memberVar.Index));
            instructions.Add(CreatePushStringInstruction(memberPath));
            instructions.Add(new InstructionNode { Opcode = "getobj" });
            instructions.Add(CreatePopMemoryInstruction(valueSlot));
            return true;
        }

        private static void EmitBuildObjectProperties(JsonObjectNode objNode, long objSlot, Dictionary<string, (string Kind, int Index)> vars, List<InstructionNode> instructions, ref int memorySlotCounter)
        {
            foreach (var (key, value) in objNode.Properties)
            {
                EmitBuildJsonNode(value, objSlot, key, vars, instructions, ref memorySlotCounter);
            }
        }

        private static void EmitBuildArrayItems(JsonArrayNode arrNode, long arrSlot, Dictionary<string, (string Kind, int Index)> vars, List<InstructionNode> instructions, ref int memorySlotCounter)
        {
            foreach (var item in arrNode.Items)
            {
                if (item is JsonObjectNode nestedObj)
                {
                    var childSlot = memorySlotCounter++;
                    instructions.Add(CreatePushStringInstruction("{}"));
                    instructions.Add(CreatePopMemoryInstruction(childSlot));
                    EmitBuildObjectProperties(nestedObj, childSlot, vars, instructions, ref memorySlotCounter);
                    EmitOpJsonCall(arrSlot, "append", "", instructions,
                        dataRef: new FunctionParameterNode { Name = "data", ParameterName = "data", EntityType = "memory", Index = childSlot });
                }
                else if (item is JsonArrayNode nestedArr)
                {
                    var childSlot = memorySlotCounter++;
                    instructions.Add(CreatePushStringInstruction("[]"));
                    instructions.Add(CreatePopMemoryInstruction(childSlot));
                    EmitBuildArrayItems(nestedArr, childSlot, vars, instructions, ref memorySlotCounter);
                    EmitOpJsonCall(arrSlot, "append", "", instructions,
                        dataRef: new FunctionParameterNode { Name = "data", ParameterName = "data", EntityType = "memory", Index = childSlot });
                }
                else if (item is JsonIdentifierNode idNode && TryResolveJsonIdentifierToSlot(idNode, vars, instructions, ref memorySlotCounter, out var valueSlot))
                {
                    EmitOpJsonCall(
                        arrSlot,
                        "append",
                        "",
                        instructions,
                        dataRef: new FunctionParameterNode { Name = "data", ParameterName = "data", EntityType = "memory", Index = valueSlot });
                }
                else if (item is JsonSymbolicNode symbolicItem)
                {
                    var tmpSlot = memorySlotCounter++;
                    instructions.Add(CreatePushStringInstruction(":" + symbolicItem.SymbolicName));
                    instructions.Add(CreatePushIntInstruction(1));
                    instructions.Add(CreateCallInstruction("get"));
                    instructions.Add(CreatePopMemoryInstruction(tmpSlot));
                    EmitOpJsonCall(
                        arrSlot,
                        "append",
                        "",
                        instructions,
                        dataRef: new FunctionParameterNode { Name = "data", ParameterName = "data", EntityType = "memory", Index = tmpSlot });
                }
                else if (item is JsonPrimitiveNode primitiveItem)
                {
                    EmitOpJsonCall(arrSlot, "append", "", instructions, dataJson: JsonLiteralToRaw(primitiveItem.Value));
                }
            }
        }

        private static bool TryParseJsonArgument(string text, out JsonArgumentNode root)
        {
            root = new JsonPrimitiveNode { Value = null };
            var i = 0;
            SkipWs(text, ref i);
            if (!TryParseJsonValue(text, ref i, out root))
                return false;
            SkipWs(text, ref i);
            return i == text.Length;
        }

        private static bool TryParseJsonValue(string text, ref int i, out JsonArgumentNode node)
        {
            node = new JsonPrimitiveNode { Value = null };
            SkipWs(text, ref i);
            if (i >= text.Length)
                return false;

            var ch = text[i];
            if (ch == '{')
                return TryParseJsonObject(text, ref i, out node);
            if (ch == '[')
                return TryParseJsonArray(text, ref i, out node);
            if (ch == '"')
            {
                if (!TryParseQuotedString(text, ref i, out var str))
                    return false;
                node = new JsonPrimitiveNode { Value = str };
                return true;
            }
            if (ch == '-' || char.IsDigit(ch))
            {
                if (!TryParseNumber(text, ref i, out var num))
                    return false;
                node = new JsonPrimitiveNode { Value = num };
                return true;
            }

            if (ch == ':')
            {
                i++;
                if (!TryParseIdentifier(text, ref i, out var symbolicIdent))
                    return false;
                node = new JsonSymbolicNode { SymbolicName = symbolicIdent };
                return true;
            }

            if (!TryParseIdentifier(text, ref i, out var ident))
                return false;

            // Allow member-access references inside AGI object literals, e.g. reply.id / reply.text.
            while (i < text.Length && text[i] == '.')
            {
                var dotPos = i;
                i++;
                if (!TryParseIdentifier(text, ref i, out var segment))
                {
                    i = dotPos;
                    break;
                }
                ident += "." + segment;
            }

            if (string.Equals(ident, "true", StringComparison.OrdinalIgnoreCase))
            {
                node = new JsonPrimitiveNode { Value = true };
                return true;
            }
            if (string.Equals(ident, "false", StringComparison.OrdinalIgnoreCase))
            {
                node = new JsonPrimitiveNode { Value = false };
                return true;
            }
            if (string.Equals(ident, "null", StringComparison.OrdinalIgnoreCase))
            {
                node = new JsonPrimitiveNode { Value = null };
                return true;
            }

            node = new JsonIdentifierNode { Name = ident };
            return true;
        }

        private static bool TryParseJsonObject(string text, ref int i, out JsonArgumentNode node)
        {
            node = new JsonPrimitiveNode { Value = null };
            if (i >= text.Length || text[i] != '{')
                return false;
            i++;
            SkipWs(text, ref i);

            var obj = new JsonObjectNode();
            if (i < text.Length && text[i] == '}')
            {
                i++;
                node = obj;
                return true;
            }

            while (i < text.Length)
            {
                string key;
                if (i < text.Length && text[i] == '"')
                {
                    if (!TryParseQuotedString(text, ref i, out key))
                        return false;
                }
                else
                {
                    if (!TryParseIdentifier(text, ref i, out key))
                        return false;
                }

                SkipWs(text, ref i);
                if (i >= text.Length || text[i] != ':')
                    return false;
                i++;

                if (!TryParseJsonValue(text, ref i, out var valueNode))
                    return false;
                obj.Properties.Add((key, valueNode));

                SkipWs(text, ref i);
                if (i < text.Length && text[i] == ',')
                {
                    i++;
                    SkipWs(text, ref i);
                    continue;
                }
                if (i < text.Length && text[i] == '}')
                {
                    i++;
                    node = obj;
                    return true;
                }
                return false;
            }

            return false;
        }

        private static bool TryParseJsonArray(string text, ref int i, out JsonArgumentNode node)
        {
            node = new JsonPrimitiveNode { Value = null };
            if (i >= text.Length || text[i] != '[')
                return false;
            i++;
            SkipWs(text, ref i);

            var arr = new JsonArrayNode();
            if (i < text.Length && text[i] == ']')
            {
                i++;
                node = arr;
                return true;
            }

            while (i < text.Length)
            {
                if (!TryParseJsonValue(text, ref i, out var valueNode))
                    return false;
                arr.Items.Add(valueNode);

                SkipWs(text, ref i);
                if (i < text.Length && text[i] == ',')
                {
                    i++;
                    SkipWs(text, ref i);
                    continue;
                }
                if (i < text.Length && text[i] == ']')
                {
                    i++;
                    node = arr;
                    return true;
                }
                return false;
            }

            return false;
        }

        private static void SkipWs(string text, ref int i)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
        }

        private static bool TryParseIdentifier(string text, ref int i, out string ident)
        {
            ident = "";
            if (i >= text.Length || !(char.IsLetter(text[i]) || text[i] == '_'))
                return false;
            var start = i++;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                i++;
            ident = text.Substring(start, i - start);
            return true;
        }

        private static bool TryParseQuotedString(string text, ref int i, out string value)
        {
            value = "";
            if (i >= text.Length || text[i] != '"')
                return false;
            i++;
            var sb = new StringBuilder();
            while (i < text.Length)
            {
                var ch = text[i++];
                if (ch == '\\' && i < text.Length)
                {
                    var esc = text[i++];
                    sb.Append(esc switch
                    {
                        '"' => '"',
                        '\\' => '\\',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => esc
                    });
                    continue;
                }
                if (ch == '"')
                {
                    value = sb.ToString();
                    return true;
                }
                sb.Append(ch);
            }
            return false;
        }

        private static bool TryParseNumber(string text, ref int i, out object number)
        {
            number = 0L;
            var start = i;
            if (text[i] == '-') i++;
            while (i < text.Length && char.IsDigit(text[i])) i++;
            if (i < text.Length && text[i] == '.')
            {
                i++;
                while (i < text.Length && char.IsDigit(text[i])) i++;
            }
            if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
            {
                i++;
                if (i < text.Length && (text[i] == '+' || text[i] == '-')) i++;
                while (i < text.Length && char.IsDigit(text[i])) i++;
            }

            var raw = text.Substring(start, i - start);
            if (raw.IndexOf('.') >= 0 || raw.IndexOf('e') >= 0 || raw.IndexOf('E') >= 0)
            {
                if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    number = d;
                    return true;
                }
                return false;
            }

            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            {
                number = l;
                return true;
            }
            return false;
        }

        private static bool TryParseObjectLiteralWithVarRefs(string valueText, out List<(string Key, string VarName)>? keyVars)
        {
            keyVars = null;
            var trimmed = valueText.Trim();
            var scanner = new Scanner(trimmed);
            if (scanner.Current.Kind != TokenKind.LBrace)
                return false;
            scanner.Scan();
            var list = new List<(string, string)>();
            while (!scanner.Current.IsEndOfInput && scanner.Current.Kind != TokenKind.RBrace)
            {
                while (scanner.Current.Kind == TokenKind.Semicolon || scanner.Current.Kind == TokenKind.Comma)
                    scanner.Scan();
                if (scanner.Current.Kind == TokenKind.RBrace)
                    break;
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                var key = scanner.Scan().Value ?? "";
                if (scanner.Current.Kind != TokenKind.Colon)
                    return false;
                scanner.Scan();
                while (scanner.Current.Kind == TokenKind.Semicolon)
                    scanner.Scan();
                if (scanner.Current.Kind != TokenKind.Identifier)
                    return false;
                var varName = scanner.Scan().Value ?? "";
                list.Add((key, varName));
                while (scanner.Current.Kind == TokenKind.Semicolon || scanner.Current.Kind == TokenKind.Comma)
                    scanner.Scan();
            }
            if (scanner.Current.Kind != TokenKind.RBrace)
                return false;
            scanner.Scan();
            while (scanner.Current.Kind == TokenKind.Semicolon)
                scanner.Scan();
            if (!scanner.Current.IsEndOfInput)
                return false;
            keyVars = list;
            return true;
        }
    }
}

