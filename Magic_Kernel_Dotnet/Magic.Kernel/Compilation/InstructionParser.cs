using Magic.Kernel.Compilation.Ast;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Magic.Kernel.Compilation
{
    /// <summary>
    /// External entry point for parsing a single AGI instruction.
    /// Keeps instruction-level parsing API separate from program-structure parser.
    /// </summary>
    public class InstructionParser
    {
        private Scanner? _scanner;

        public InstructionParser()
        {
            
        }

        public Token CurrentToken() => _scanner?.Watch(0) ?? default;

        public Token? Watch(int offset) => _scanner?.Watch(offset);

        private Token Expect(TokenKind kind)
        {
            var t = _scanner!.Scan();
            if (t.Kind != kind)
                throw new CompilationException($"Expected {kind}, got {t.Kind} at position {t.Start}.", t.Start);
            return t;
        }

        private Token ExpectOneOf(params string[] values)
        {
            var t = _scanner!.Scan();
            if (t.Kind != TokenKind.Identifier)
                throw new CompilationException($"Expected one of [{string.Join(", ", values)}], got {t.Kind} at position {t.Start}.", t.Start);
            var lower = t.Value.ToLowerInvariant();
            foreach (var v in values)
                if (v.Equals(lower, StringComparison.OrdinalIgnoreCase))
                    return t;
            throw new CompilationException($"Expected one of [{string.Join(", ", values)}], got '{t.Value}' at position {t.Start}.", t.Start);
        }

        private bool Or(params Func<bool>[] alternatives)
        {
            if (_scanner == null) return false;
            var pos = _scanner.Save();
            foreach (var tryBranch in alternatives)
            {
                _scanner.Restore(pos);
                if (tryBranch())
                    return true;
            }
            _scanner.Restore(pos);
            return false;
        }

        private void Sequence(params (TokenKind kind, string? value)[] pattern)
        {
            foreach (var (kind, value) in pattern)
            {
                var t = _scanner!.Scan();
                if (t.Kind != kind)
                    throw new CompilationException($"Expected {kind}, got {t.Kind} at position {t.Start}.", t.Start);
                if (value != null && !t.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
                    throw new CompilationException($"Expected '{value}', got '{t.Value}' at position {t.Start}.", t.Start);
            }
        }

        private void SkipOptionalComma()
        {
            while (_scanner!.Current.Kind == TokenKind.Comma)
                _scanner.Scan();
        }

        private InstructionNode ParseCurrentInstruction()
        {
            var instruction = new InstructionNode();
            if (_scanner == null || _scanner.Current.IsEndOfInput)
            {
                instruction.Opcode = "";
                return instruction;
            }

            var opcodeTok = Expect(TokenKind.Identifier);
            instruction.Opcode = opcodeTok.Value.ToLowerInvariant();

            if (_scanner.Current.IsEndOfInput)
                return instruction;

            switch (instruction.Opcode)
            {
                case "call":
                    instruction.Parameters = ParseCallParametersFromTokens();
                    break;
                case "pop":
                    instruction.Parameters = ParseMemoryParametersFromTokens();
                    break;
                case "push":
                    instruction.Parameters = ParsePushParametersFromTokens();
                    break;
                case "def":
                case "awaitobj":
                case "await":
                case "streamwaitobj":
                case "defgen":
                case "getobj":
                case "setobj":
                case "streamwait":
                    instruction.Parameters = new List<ParameterNode>();
                    break;
                case "callobj":
                    instruction.Parameters = ParseCallObjParametersFromTokens();
                    break;
                case "label":
                case "je":
                case "jmp":
                    instruction.Parameters = ParseLabelLikeParametersFromTokens();
                    break;
                case "cmp":
                    instruction.Parameters = ParseCmpParametersFromTokens();
                    break;
                default:
                    instruction.Parameters = ParseParametersFromTokens();
                    break;
            }

            return instruction;
        }

        public InstructionNode Parse(string source)
        {
            var trimmed = source?.Trim() ?? "";
            if (string.IsNullOrEmpty(trimmed))
                return new InstructionNode { Opcode = "" };

            _scanner = new Scanner(trimmed);
            return ParseCurrentInstruction();
        }

        private List<ParameterNode> ParseCallObjParametersFromTokens()
        {
            SkipOptionalComma();
            if (_scanner!.Current.IsEndOfInput)
                return new List<ParameterNode>();

            Token nameToken = default;
            var parsed = Or(
                () =>
                {
                    if (_scanner.Current.Kind != TokenKind.StringLiteral) return false;
                    nameToken = _scanner.Scan();
                    return true;
                },
                () =>
                {
                    if (_scanner.Current.Kind != TokenKind.Identifier) return false;
                    nameToken = _scanner.Scan();
                    return true;
                });

            if (!parsed)
                throw new CompilationException($"Expected string or identifier for callobj at position {_scanner.Current.Start}.", _scanner.Current.Start);
            var name = nameToken.Value;
            return new List<ParameterNode> { new FunctionNameParameterNode { FunctionName = name } };
        }

        private List<ParameterNode> ParseLabelLikeParametersFromTokens()
        {
            SkipOptionalComma();
            if (_scanner!.Current.IsEndOfInput)
                return new List<ParameterNode>();

            Token labelToken = default;
            var parsed = Or(
                () =>
                {
                    if (_scanner.Current.Kind != TokenKind.StringLiteral) return false;
                    labelToken = _scanner.Scan();
                    return true;
                },
                () =>
                {
                    if (_scanner.Current.Kind != TokenKind.Identifier) return false;
                    labelToken = _scanner.Scan();
                    return true;
                });

            if (!parsed)
                throw new CompilationException($"Expected label identifier at position {_scanner.Current.Start}.", _scanner.Current.Start);

            return new List<ParameterNode> { new FunctionNameParameterNode { FunctionName = labelToken.Value } };
        }

        private List<ParameterNode> ParseCmpParametersFromTokens()
        {
            var parameters = new List<ParameterNode>();
            SkipOptionalComma();

            if (_scanner!.Current.Kind != TokenKind.LBracket)
                return parameters;

            parameters.AddRange(ParseMemoryParametersFromTokens());
            SkipOptionalComma();

            if (_scanner.Current.Kind == TokenKind.Number || _scanner.Current.Kind == TokenKind.Float)
            {
                var numTok = _scanner.Scan();
                if (long.TryParse(numTok.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    parameters.Add(new IndexParameterNode { Name = "int", Value = value });
            }

            return parameters;
        }

        private List<ParameterNode> ParseMemoryParametersFromTokens()
        {
            SkipOptionalComma();
            if (_scanner!.Current.IsEndOfInput || _scanner.Current.Kind != TokenKind.LBracket)
                return new List<ParameterNode>();
            Expect(TokenKind.LBracket);
            var numTok = _scanner.Scan();
            if (numTok.Kind != TokenKind.Number && numTok.Kind != TokenKind.Float)
                throw new CompilationException($"Expected number in memory slot at position {numTok.Start}.", numTok.Start);
            if (!long.TryParse(numTok.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var address))
                throw new CompilationException($"Invalid number in memory slot at position {numTok.Start}.", numTok.Start);
            Expect(TokenKind.RBracket);
            return new List<ParameterNode> { new MemoryParameterNode { Name = "index", Index = address } };
        }

        private List<ParameterNode> ParsePushParametersFromTokens()
        {
            SkipOptionalComma();
            if (_scanner!.Current.IsEndOfInput)
                return new List<ParameterNode>();
            if (_scanner.Current.Kind == TokenKind.Identifier && _scanner.Current.Value.Equals("global", StringComparison.OrdinalIgnoreCase))
            {
                _scanner.Scan();
                Expect(TokenKind.Colon);
                var mem = ParseMemoryParametersFromTokens();
                if (mem.Count == 1 && mem[0] is MemoryParameterNode mp)
                    mp.Name = "global";
                return mem;
            }
            if (_scanner.Current.Kind == TokenKind.LBracket)
                return ParseMemoryParametersFromTokens();
            if (_scanner.Current.Kind == TokenKind.Identifier && _scanner.Current.Value.Equals("string", StringComparison.OrdinalIgnoreCase))
            {
                Sequence((TokenKind.Identifier, "string"), (TokenKind.Colon, null));
                var strTok = Expect(TokenKind.StringLiteral);
                return new List<ParameterNode> { new StringParameterNode { Name = "string", Value = strTok.Value } };
            }
            if (_scanner.Current.Kind == TokenKind.Number)
            {
                var t = _scanner.Scan();
                if (long.TryParse(t.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numVal))
                    return new List<ParameterNode> { new IndexParameterNode { Name = "int", Value = numVal } };
            }
            if (_scanner.Current.Kind == TokenKind.Identifier)
            {
                var typeName = _scanner.Scan().Value;
                return new List<ParameterNode> { new TypeLiteralParameterNode { TypeName = typeName } };
            }
            if (_scanner.Current.Kind == TokenKind.Float)
            {
                var t = _scanner.Scan();
                if (long.TryParse(t.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numVal))
                    return new List<ParameterNode> { new IndexParameterNode { Name = "int", Value = numVal } };
            }
            return new List<ParameterNode>();
        }

        private List<ParameterNode> ParseCallParametersFromTokens()
        {
            var parameters = new List<ParameterNode>();
            SkipOptionalComma();
            if (_scanner!.Current.IsEndOfInput)
                return parameters;

            Token functionNameToken = default;
            var parsedFunctionName = Or(
                () =>
                {
                    if (_scanner.Current.Kind != TokenKind.StringLiteral) return false;
                    functionNameToken = _scanner.Scan();
                    return true;
                },
                () =>
                {
                    if (_scanner.Current.Kind != TokenKind.Identifier) return false;
                    functionNameToken = _scanner.Scan();
                    return true;
                });

            if (!parsedFunctionName)
                throw new CompilationException($"Expected function name (string or identifier) at position {_scanner.Current.Start}.", _scanner.Current.Start);
            parameters.Add(new FunctionNameParameterNode { Name = "function", FunctionName = functionNameToken.Value });

            SkipOptionalComma();
            while (!_scanner.Current.IsEndOfInput)
            {
                if (_scanner.Current.Kind == TokenKind.LBracket)
                {
                    Expect(TokenKind.LBracket);
                    var numTok = _scanner.Scan();
                    if (numTok.Kind != TokenKind.Number && numTok.Kind != TokenKind.Float)
                        throw new CompilationException($"Expected number in memory slot at position {numTok.Start}.", numTok.Start);
                    if (long.TryParse(numTok.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var addr))
                    {
                        Expect(TokenKind.RBracket);
                        parameters.Add(new FunctionParameterNode { Name = "memory", ParameterName = "memory", EntityType = "memory", Index = addr });
                    }
                    SkipOptionalComma();
                    continue;
                }
                if (_scanner.Current.Kind != TokenKind.Identifier)
                    break;
                var paramName = _scanner.Scan().Value;
                Expect(TokenKind.Colon);
                var paramNode = ParseCallParameterValueFromTokens(paramName);
                if (paramNode != null)
                    parameters.Add(paramNode);
                SkipOptionalComma();
            }
            return parameters;
        }

        private ParameterNode? ParseCallParameterValueFromTokens(string paramName)
        {
            if (_scanner!.Current.Kind == TokenKind.StringLiteral)
            {
                var v = _scanner.Scan().Value;
                return new StringParameterNode { Name = paramName, Value = v };
            }
            if (_scanner.Current.Kind == TokenKind.LBrace)
            {
                var complex = ParseComplexValueFromTokens();
                return new ComplexValueParameterNode { Name = paramName, ParameterName = paramName, Value = complex };
            }
            if (_scanner.Current.Kind == TokenKind.Identifier)
            {
                var firstIdent = _scanner.Scan().Value;
                if (_scanner.Current.Kind == TokenKind.Colon)
                    _scanner.Scan();
                SkipOptionalComma();
                if (_scanner.Current.Kind == TokenKind.LBrace)
                {
                    var complex = ParseComplexValueFromTokens();
                    var dict = new Dictionary<string, object> { { firstIdent, complex } };
                    return new ComplexValueParameterNode { Name = paramName, ParameterName = paramName, Value = dict };
                }
                var entityType = firstIdent.Equals("index", StringComparison.OrdinalIgnoreCase) ? paramName : firstIdent;
                if (_scanner.Current.Kind == TokenKind.Identifier && _scanner.Current.Value.Equals("index", StringComparison.OrdinalIgnoreCase))
                {
                    _scanner.Scan();
                    if (_scanner.Current.Kind == TokenKind.Colon)
                        _scanner.Scan();
                }
                if (_scanner.Current.Kind == TokenKind.Number || _scanner.Current.Kind == TokenKind.Float)
                {
                    var numTok = _scanner.Scan();
                    if (long.TryParse(numTok.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                        return new FunctionParameterNode { Name = paramName, ParameterName = paramName, EntityType = entityType, Index = idx };
                }
                throw new CompilationException($"Expected index or {{ after entity type at position {_scanner.Current.Start}.", _scanner.Current.Start);
            }
            return null;
        }

        private Dictionary<string, object> ParseComplexValueFromTokens()
        {
            Expect(TokenKind.LBrace);
            var result = new Dictionary<string, object>();
            SkipOptionalComma();
            while (!_scanner!.Current.IsEndOfInput && _scanner.Current.Kind != TokenKind.RBrace)
            {
                var keyTok = Expect(TokenKind.Identifier);
                var key = keyTok.Value;
                Expect(TokenKind.Colon);
                object? value = ParseValueFromTokens();
                if (value != null)
                    result[key] = value;
                SkipOptionalComma();
            }
            Expect(TokenKind.RBrace);
            return result;
        }

        private object? ParseValueFromTokens()
        {
            if (_scanner!.Current.IsEndOfInput) return null;
            if (_scanner.Current.Kind == TokenKind.LBracket)
            {
                _scanner.Scan();
                var list = ParseArrayItemsFromTokens();
                Expect(TokenKind.RBracket);
                return list;
            }
            if (_scanner.Current.Kind == TokenKind.LBrace)
                return ParseComplexValueFromTokens();
            if (_scanner.Current.Kind == TokenKind.StringLiteral)
                return _scanner.Scan().Value;
            if (_scanner.Current.Kind == TokenKind.Number)
            {
                var t = _scanner.Scan();
                if (long.TryParse(t.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                    return l;
                return t.Value;
            }
            if (_scanner.Current.Kind == TokenKind.Float)
            {
                var t = _scanner.Scan();
                if (double.TryParse(t.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return d;
                return t.Value;
            }
            if (_scanner.Current.Kind == TokenKind.Identifier)
                return _scanner.Scan().Value;
            return null;
        }

        private List<object> ParseArrayItemsFromTokens()
        {
            var items = new List<object>();
            SkipOptionalComma();
            while (!_scanner!.Current.IsEndOfInput && _scanner.Current.Kind != TokenKind.RBracket)
            {
                var item = ParseValueFromTokens();
                if (item != null)
                    items.Add(item);
                SkipOptionalComma();
            }
            return items;
        }

        private List<ParameterNode> ParseParametersFromTokens()
        {
            var parameters = new List<ParameterNode>();
            SkipOptionalComma();
            while (!_scanner!.Current.IsEndOfInput && _scanner.Current.Kind == TokenKind.Identifier)
            {
                var nameTok = _scanner.Scan();
                var name = nameTok.Value.ToLowerInvariant();
                if (_scanner.Current.Kind == TokenKind.Semicolon || _scanner.Current.IsEndOfInput)
                    break;
                if (_scanner.Current.Kind == TokenKind.Colon)
                    _scanner.Scan();
                else if (_scanner.Current.Kind != TokenKind.Number && _scanner.Current.Kind != TokenKind.Float)
                    Expect(TokenKind.Colon);
                var param = ParseParameterValueByNameFromTokens(name);
                if (param != null)
                    parameters.Add(param);
                SkipOptionalComma();
            }
            return parameters;
        }

        private ParameterNode? ParseParameterValueByNameFromTokens(string name)
        {
            if (name == "index")
            {
                var t = _scanner!.Scan();
                if (t.Kind != TokenKind.Number && t.Kind != TokenKind.Float)
                    throw new CompilationException($"Expected number for index at position {t.Start}.", t.Start);
                if (long.TryParse(t.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                    return new IndexParameterNode { Name = name, Value = v };
            }
            if (name == "dimensions")
            {
                Expect(TokenKind.LBracket);
                var values = new List<float>();
                SkipOptionalComma();
                while (!_scanner!.Current.IsEndOfInput && _scanner.Current.Kind != TokenKind.RBracket)
                {
                    var t = _scanner.Scan();
                    if (t.Kind == TokenKind.Number || t.Kind == TokenKind.Float)
                    {
                        if (float.TryParse(t.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                            values.Add(f);
                    }
                    SkipOptionalComma();
                }
                Expect(TokenKind.RBracket);
                return new DimensionsParameterNode { Name = name, Values = values };
            }
            if (name == "weight")
            {
                var t = _scanner!.Scan();
                if (t.Kind != TokenKind.Number && t.Kind != TokenKind.Float)
                    throw new CompilationException($"Expected number for weight at position {t.Start}.", t.Start);
                if (float.TryParse(t.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                    return new WeightParameterNode { Name = name, Value = f };
            }
            if (name == "data")
            {
                var (typeStr, valueStr, hasColon) = ParseDataFromTokens();
                var dataParam = new DataParameterNode { Name = name, Type = typeStr, Value = valueStr, HasColon = hasColon };
                if (!string.IsNullOrEmpty(typeStr))
                {
                    foreach (var part in typeStr.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (!string.IsNullOrEmpty(part))
                            dataParam.Types.Add(part.ToLowerInvariant());
                }
                dataParam.OriginalString = !string.IsNullOrEmpty(typeStr) && !string.IsNullOrEmpty(valueStr) ? (hasColon ? $"{typeStr} \"{valueStr}\"" : $"{typeStr} {valueStr}") : typeStr ?? "";
                return dataParam;
            }
            if (name == "from" || name == "to")
            {
                var (entityType, index) = ParseEntityReferenceFromTokens();
                return name == "from"
                    ? (ParameterNode)new FromParameterNode { Name = name, EntityType = entityType, Index = index }
                    : new ToParameterNode { Name = name, EntityType = entityType, Index = index };
            }
            if (name == "vertices")
            {
                var indices = ParseVerticesFromTokens();
                return new VerticesParameterNode { Name = name, Indices = indices };
            }
            return null;
        }

        private (string type, string value, bool hasColon) ParseDataFromTokens()
        {
            var types = new List<string>();
            var sawColon = false;
            while (_scanner!.Current.Kind == TokenKind.Identifier)
            {
                types.Add(_scanner.Scan().Value.ToLowerInvariant());
                if (_scanner.Current.Kind != TokenKind.Colon)
                    break;
                sawColon = true;
                _scanner.Scan();
            }
            if (types.Count == 0)
                return ("", "", false);
            var mainType = types[types.Count - 1];
            types.RemoveAt(types.Count - 1);
            var typeString = types.Count > 0 ? string.Join(":", types) + ":" + mainType : mainType;
            if (!sawColon && _scanner.Current.Kind == TokenKind.Identifier)
                throw new CompilationException($"Expected Colon or string literal after type '{typeString}', got Identifier at position {_scanner.Current.Start}.", _scanner.Current.Start);
            var valueStr = _scanner.Current.Kind == TokenKind.StringLiteral ? _scanner.Scan().Value : "";
            return (typeString, valueStr, sawColon);
        }

        private (string entityType, long index) ParseEntityReferenceFromTokens()
        {
            if (_scanner!.Current.Kind != TokenKind.Identifier)
                return ("", 0);
            var entityType = _scanner.Scan().Value.ToLowerInvariant();
            if (_scanner.Current.Kind == TokenKind.Colon)
                _scanner.Scan();
            if (_scanner.Current.Kind == TokenKind.Identifier && _scanner.Current.Value.Equals("index", StringComparison.OrdinalIgnoreCase))
            {
                _scanner.Scan();
                if (_scanner.Current.Kind == TokenKind.Colon)
                    _scanner.Scan();
            }
            if (_scanner.Current.Kind != TokenKind.Number && _scanner.Current.Kind != TokenKind.Float)
                return ("", 0);
            var numTok = _scanner.Scan();
            if (numTok.Kind != TokenKind.Number && numTok.Kind != TokenKind.Float)
                return ("", 0);
            long.TryParse(numTok.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx);
            return (entityType, idx);
        }

        private List<long> ParseVerticesFromTokens()
        {
            if (_scanner!.Current.Kind == TokenKind.Identifier && _scanner.Current.Value.Equals("indices", StringComparison.OrdinalIgnoreCase))
            {
                ExpectOneOf("indices");
                Expect(TokenKind.Colon);
            }
            Expect(TokenKind.LBracket);
            var indices = new List<long>();
            SkipOptionalComma();
            while (!_scanner.Current.IsEndOfInput && _scanner.Current.Kind != TokenKind.RBracket)
            {
                var t = _scanner.Scan();
                if (t.Kind == TokenKind.Number || t.Kind == TokenKind.Float)
                {
                    if (long.TryParse(t.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
                        indices.Add(l);
                }
                SkipOptionalComma();
            }
            Expect(TokenKind.RBracket);
            return indices;
        }
    }
}
