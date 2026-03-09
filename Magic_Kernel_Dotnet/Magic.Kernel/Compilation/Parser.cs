using Magic.Kernel.Compilation.Ast;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Magic.Kernel.Compilation
{
    public class Parser
    {
        private readonly InstructionParser _instructionParser = new InstructionParser();
        private Scanner? _scanner;

        private Scanner CurrentScanner =>
            _scanner ?? throw new InvalidOperationException("Scanner is not initialized.");

        private void SkipNewlines()
        {
            while (CurrentScanner.Current.Kind == TokenKind.Newline)
                CurrentScanner.Scan();
        }

        private void AddRangeAsStatement(int start, int end, List<string> output)
        {
            if (start < 0 || end <= start) return;
            var text = CurrentScanner.Slice(start, end).Trim();
            if (!string.IsNullOrWhiteSpace(text))
                output.Add(text);
        }

        private static string NormalizeInstructionText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var sb = new System.Text.StringBuilder(text.Length);
            var inString = false;
            var escaped = false;
            var pendingSpace = false;

            foreach (var ch in text)
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
                    if (ch == '"')
                        inString = false;
                    continue;
                }

                if (ch == '"')
                {
                    if (pendingSpace && sb.Length > 0 && sb[sb.Length - 1] != ' ')
                        sb.Append(' ');
                    pendingSpace = false;
                    inString = true;
                    sb.Append(ch);
                    continue;
                }

                if (char.IsWhiteSpace(ch))
                {
                    pendingSpace = true;
                    continue;
                }

                if (pendingSpace && sb.Length > 0 && sb[sb.Length - 1] != ' ')
                    sb.Append(' ');
                pendingSpace = false;
                sb.Append(ch);
            }

            return sb.ToString().Trim();
        }

        /// <summary>Потребляет обычный блок (после "{") и возвращает список statement-строк до парной "}".</summary>
        private List<string> ConsumeStatementBlockLines()
        {
            var result = new List<string>();
            var depth = 1;
            var currentStart = -1;
            var currentEnd = -1;

            while (true)
            {
                var tok = CurrentScanner.Scan();
                if (tok.Kind == TokenKind.EndOfInput)
                {
                    AddRangeAsStatement(currentStart, currentEnd, result);
                    break;
                }

                if (tok.Kind == TokenKind.LBrace)
                {
                    depth++;
                    if (currentStart < 0) currentStart = tok.Start;
                    currentEnd = tok.End;
                    continue;
                }

                if (tok.Kind == TokenKind.RBrace)
                {
                    depth--;
                    if (depth == 0)
                    {
                        AddRangeAsStatement(currentStart, currentEnd, result);
                        break;
                    }
                    if (currentStart < 0) currentStart = tok.Start;
                    currentEnd = tok.End;
                    continue;
                }

                if (depth == 1 && (tok.Kind == TokenKind.Semicolon || tok.Kind == TokenKind.Newline))
                {
                    AddRangeAsStatement(currentStart, currentEnd, result);
                    currentStart = -1;
                    currentEnd = -1;
                    continue;
                }

                if (currentStart < 0) currentStart = tok.Start;
                currentEnd = tok.End;
            }

            return result;
        }

        /// <summary>Потребляет asm-блок (после "asm {") и возвращает инструкции до парной "}".</summary>
        private List<string> ConsumeAsmBlockInstructions()
        {
            var result = new List<string>();
            var braceDepth = 1;
            var bracketDepth = 0;
            var parenDepth = 0;
            var currentStart = -1;
            var currentEnd = -1;

            while (true)
            {
                var tok = CurrentScanner.Scan();
                switch (tok.Kind)
                {
                    case TokenKind.EndOfInput:
                        AddRangeAsStatement(currentStart, currentEnd, result);
                        return result;
                    case TokenKind.LBrace:
                        braceDepth++;
                        if (currentStart < 0) currentStart = tok.Start;
                        currentEnd = tok.End;
                        break;
                    case TokenKind.RBrace:
                        braceDepth--;
                        if (braceDepth == 0)
                        {
                            AddRangeAsStatement(currentStart, currentEnd, result);
                            return result;
                        }
                        if (currentStart < 0) currentStart = tok.Start;
                        currentEnd = tok.End;
                        break;
                    case TokenKind.LBracket:
                        bracketDepth++;
                        if (currentStart < 0) currentStart = tok.Start;
                        currentEnd = tok.End;
                        break;
                    case TokenKind.RBracket:
                        if (bracketDepth > 0) bracketDepth--;
                        if (currentStart < 0) currentStart = tok.Start;
                        currentEnd = tok.End;
                        break;
                    case TokenKind.LParen:
                        parenDepth++;
                        if (currentStart < 0) currentStart = tok.Start;
                        currentEnd = tok.End;
                        break;
                    case TokenKind.RParen:
                        if (parenDepth > 0) parenDepth--;
                        if (currentStart < 0) currentStart = tok.Start;
                        currentEnd = tok.End;
                        break;
                    case TokenKind.Semicolon:
                        if (braceDepth == 1 && bracketDepth == 0 && parenDepth == 0)
                        {
                            AddRangeAsStatement(currentStart, currentEnd, result);
                            currentStart = -1;
                            currentEnd = -1;
                        }
                        else
                        {
                            if (currentStart < 0) currentStart = tok.Start;
                            currentEnd = tok.End;
                        }
                        break;
                    case TokenKind.Newline:
                        if (braceDepth == 1 && bracketDepth == 0 && parenDepth == 0 && NextAsmTokenStartsOpcode())
                        {
                            AddRangeAsStatement(currentStart, currentEnd, result);
                            currentStart = -1;
                            currentEnd = -1;
                        }
                        break;
                    default:
                        if (currentStart < 0) currentStart = tok.Start;
                        currentEnd = tok.End;
                        break;
                }
            }
        }

        /// <summary>BNF: block = "{" (asm-block | statement-block) "}"</summary>
        private bool TryParseBodyBlock(out List<AstNode> instructions)
        {
            instructions = new List<AstNode>();
            if (CurrentScanner.Current.Kind != TokenKind.LBrace) return false;
            CurrentScanner.Scan(); // consume outer "{"
            SkipNewlines();

            // asm-block = "asm" "{" asm-instructions "}"
            if (CurrentScanner.Current.Kind == TokenKind.Identifier && string.Equals(CurrentScanner.Current.Value, "asm", StringComparison.OrdinalIgnoreCase))
            {
                CurrentScanner.Scan();
                SkipNewlines();
                if (CurrentScanner.Current.Kind != TokenKind.LBrace)
                    throw new CompilationException($"Expected '{{' after asm at position {CurrentScanner.Current.Start}.", CurrentScanner.Current.Start);
                CurrentScanner.Scan(); // consume asm "{"
                var asmList = ConsumeAsmBlockInstructions(); // consumes asm "}"
                instructions = asmList
                    .Select(NormalizeInstructionText)
                    .Select(ParseWithContext)
                    .Cast<AstNode>()
                    .ToList();
                SkipNewlines();
                if (CurrentScanner.Current.Kind != TokenKind.RBrace)
                    throw new CompilationException($"Expected '}}' to close block at position {CurrentScanner.Current.Start}.", CurrentScanner.Current.Start);
                CurrentScanner.Scan(); // consume outer "}"
                return true;
            }

            // statement-block = { statements }
            var statementLines = ConsumeStatementBlockLines(); // consumes outer "}"
            instructions = statementLines
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => (AstNode)new StatementLineNode { Text = line })
                .ToList();
            return true;
        }

        private static bool IsProgramKeyword(string id) =>
            id == "@" || string.Equals(id, "AGI", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "program", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "module", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "system", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "procedure", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "function", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "entrypoint", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "asm", StringComparison.OrdinalIgnoreCase);

        private Token ExpectToken(TokenKind kind, string? value = null)
        {
            var tok = CurrentScanner.Scan();
            if (tok.Kind != kind)
                throw new CompilationException($"Expected {kind}, got {tok.Kind} at position {tok.Start}.", tok.Start);
            if (value != null && !string.Equals(tok.Value, value, StringComparison.OrdinalIgnoreCase))
                throw new CompilationException($"Expected '{value}', got '{tok.Value}' at position {tok.Start}.", tok.Start);
            return tok;
        }

        private string ExpectNonEmptyHeaderValue(string headerName)
        {
            var start = CurrentScanner.Current.Start;
            var end = start;
            var hasTokens = false;
            while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.Semicolon && CurrentScanner.Current.Kind != TokenKind.Newline)
            {
                hasTokens = true;
                end = CurrentScanner.Current.End;
                CurrentScanner.Scan();
            }

            if (!hasTokens)
                throw new CompilationException($"Expected {headerName} at position {CurrentScanner.Current.Start}.", CurrentScanner.Current.Start);

            var value = CurrentScanner.Slice(start, end).Trim();
            if (string.IsNullOrWhiteSpace(value))
                throw new CompilationException($"Expected {headerName} at position {start}.", start);

            return value;
        }

        private bool TryParseAgi(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier || CurrentScanner.Current.Value != "@") return false;
            ExpectToken(TokenKind.Identifier, "@");
            ExpectToken(TokenKind.Identifier, "AGI");
            var versionStart = CurrentScanner.Current.Start;
            var versionEnd = versionStart;
            while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.Newline)
            {
                versionEnd = CurrentScanner.Current.End;
                CurrentScanner.Scan();
            }
            structure.Version = versionEnd > versionStart
                ? CurrentScanner.Slice(versionStart, versionEnd).Trim()
                : "";
            if (CurrentScanner.Current.Kind == TokenKind.Newline) CurrentScanner.Scan();
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseProgram(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier || !string.Equals(CurrentScanner.Current.Value, "program", StringComparison.OrdinalIgnoreCase)) return false;
            ExpectToken(TokenKind.Identifier, "program");
            var name = ExpectNonEmptyHeaderValue("program name");
            if (CurrentScanner.Current.Kind == TokenKind.Semicolon) CurrentScanner.Scan();
            SkipNewlines();
            structure.ProgramName = name;
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseModule(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier || !string.Equals(CurrentScanner.Current.Value, "module", StringComparison.OrdinalIgnoreCase)) return false;
            ExpectToken(TokenKind.Identifier, "module");
            var name = ExpectNonEmptyHeaderValue("module name");
            if (CurrentScanner.Current.Kind == TokenKind.Semicolon) CurrentScanner.Scan();
            SkipNewlines();
            structure.Module = name;
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseSystem(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier || !string.Equals(CurrentScanner.Current.Value, "system", StringComparison.OrdinalIgnoreCase)) return false;
            ExpectToken(TokenKind.Identifier, "system");
            var name = ExpectNonEmptyHeaderValue("system name");
            if (CurrentScanner.Current.Kind == TokenKind.Semicolon) CurrentScanner.Scan();
            SkipNewlines();
            structure.System = name;
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseProcedure(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier || !string.Equals(CurrentScanner.Current.Value, "procedure", StringComparison.OrdinalIgnoreCase)) return false;
            ExpectToken(TokenKind.Identifier, "procedure");
            var name = ExpectToken(TokenKind.Identifier).Value ?? "";
            SkipNewlines();
            if (!TryParseBodyBlock(out var body))
                throw new CompilationException($"Expected body block for procedure '{name}' at position {CurrentScanner.Current.Start}.", CurrentScanner.Current.Start);
            structure.Procedures[name] = body;
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseFunction(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier || !string.Equals(CurrentScanner.Current.Value, "function", StringComparison.OrdinalIgnoreCase)) return false;
            ExpectToken(TokenKind.Identifier, "function");
            var name = ExpectToken(TokenKind.Identifier).Value ?? "";
            SkipNewlines();
            if (!TryParseBodyBlock(out var body))
                throw new CompilationException($"Expected body block for function '{name}' at position {CurrentScanner.Current.Start}.", CurrentScanner.Current.Start);
            structure.Functions[name] = body;
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseEntrypoint(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier || !string.Equals(CurrentScanner.Current.Value, "entrypoint", StringComparison.OrdinalIgnoreCase)) return false;
            ExpectToken(TokenKind.Identifier, "entrypoint");
            SkipNewlines();
            if (!TryParseBodyBlock(out var body))
                throw new CompilationException($"Expected body block for entrypoint at position {CurrentScanner.Current.Start}.", CurrentScanner.Current.Start);
            structure.EntryPoint = body;
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseTopLevelCall(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier || IsProgramKeyword(CurrentScanner.Current.Value ?? "")) return false;
            var name = CurrentScanner.Current.Value ?? "";
            if (CurrentScanner.Watch(1)?.Kind != TokenKind.Semicolon) return false;
            CurrentScanner.Scan();
            CurrentScanner.Scan();
            structure.EntryPoint ??= new List<AstNode>();
            structure.EntryPoint.Add(new InstructionNode
            {
                Opcode = "call",
                Parameters = new List<ParameterNode>
                {
                    new FunctionNameParameterNode
                    {
                        Name = "function",
                        FunctionName = name
                    }
                }
            });
            structure.IsProgramStructure = true;
            SkipNewlines();
            return true;
        }

        private bool TryParseTopLevelSchemaDeclaration(ProgramStructure structure)
        {
            var pos = CurrentScanner.Save();
            if (CurrentScanner.Current.Kind != TokenKind.Identifier)
                return false;

            var start = CurrentScanner.Current.Start;
            CurrentScanner.Scan(); // name
            if (CurrentScanner.Current.Kind != TokenKind.Colon)
            {
                CurrentScanner.Restore(pos);
                return false;
            }
            CurrentScanner.Scan();
            if (CurrentScanner.Current.Kind != TokenKind.Identifier)
            {
                CurrentScanner.Restore(pos);
                return false;
            }

            var kind = CurrentScanner.Scan().Value;
            if (!string.Equals(kind, "table", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(kind, "database", StringComparison.OrdinalIgnoreCase))
            {
                CurrentScanner.Restore(pos);
                return false;
            }

            if (CurrentScanner.Current.Kind != TokenKind.LBrace)
            {
                CurrentScanner.Restore(pos);
                return false;
            }

            var depth = 0;
            var end = CurrentScanner.Current.End;
            while (!CurrentScanner.Current.IsEndOfInput)
            {
                var tok = CurrentScanner.Scan();
                end = tok.End;
                if (tok.Kind == TokenKind.LBrace)
                    depth++;
                else if (tok.Kind == TokenKind.RBrace)
                {
                    depth--;
                    if (depth == 0)
                        break;
                }
            }

            while (CurrentScanner.Current.Kind == TokenKind.Semicolon || CurrentScanner.Current.Kind == TokenKind.Newline)
            {
                end = CurrentScanner.Current.End;
                CurrentScanner.Scan();
            }

            var text = CurrentScanner.Slice(start, end).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                CurrentScanner.Restore(pos);
                return false;
            }

            structure.Prelude.Add(new StatementLineNode { Text = text });
            structure.IsProgramStructure = true;
            return true;
        }

        private string ConsumeLineToUnprocessed()
        {
            if (CurrentScanner.Current.IsEndOfInput) return "";
            var start = CurrentScanner.Current.Start;
            while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.Newline)
                CurrentScanner.Scan();
            var end = CurrentScanner.Current.IsEndOfInput ? CurrentScanner.SourceLength : CurrentScanner.Current.Start;
            if (CurrentScanner.Current.Kind == TokenKind.Newline) CurrentScanner.Scan();
            return CurrentScanner.Slice(start, end).Trim();
        }

        private InstructionNode ParseWithContext(string instructionText)
        {
            try
            {
                return _instructionParser.Parse(instructionText);
            }
            catch (CompilationException ex)
            {
                throw new CompilationException($"{ex.Message} Instruction: '{instructionText}'.", ex.Position);
            }
        }

        public ProgramStructure ParseProgram(string sourceCode)
        {
            var structure = new ProgramStructure();
            var source = sourceCode ?? "";
            if (string.IsNullOrEmpty(source))
            {
                return structure;
            }
            _scanner = new Scanner(source);
            var unprocessedLines = new List<string>();

            while (!CurrentScanner.Current.IsEndOfInput)
            {
                SkipNewlines();
                if (CurrentScanner.Current.IsEndOfInput) break;

                if (TryParseAgi(structure)) continue;
                if (TryParseProgram(structure)) continue;
                if (TryParseModule(structure)) continue;
                if (TryParseSystem(structure)) continue;
                if (TryParseProcedure(structure)) continue;
                if (TryParseFunction(structure)) continue;
                if (TryParseEntrypoint(structure)) continue;
                if (TryParseTopLevelSchemaDeclaration(structure)) continue;
                if (TryParseTopLevelCall(structure)) continue;

                var line = ConsumeLineToUnprocessed();
                if (!string.IsNullOrWhiteSpace(line) && !structure.IsProgramStructure)
                    unprocessedLines.Add(line);
            }

            if (!structure.IsProgramStructure)
            {
                if (unprocessedLines.Count == 0)
                    structure.EntryPoint = ParseAsInstructionSet(source).Cast<AstNode>().ToList();
                else
                    structure.EntryPoint = unprocessedLines.Select(ParseWithContext).Cast<AstNode>().ToList();
            }

            return structure;
        }

        /// <summary>Парсит исходный код как плоский набор инструкций (по строкам). Для обратной совместимости, когда нет program/entrypoint.</summary>
        public List<InstructionNode> ParseAsInstructionSet(string sourceCode)
        {
            var nodes = new List<InstructionNode>();
            var reader = new LineReader(sourceCode ?? "");
            for (var i = 0; i < reader.Count; i++)
            {
                var (_, trimmedLine) = reader.GetLine(i);
                if (string.IsNullOrWhiteSpace(trimmedLine))
                    continue;
                nodes.Add(_instructionParser.Parse(trimmedLine));
            }
            return nodes;
        }

        private static readonly HashSet<string> AsmOpcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "addvertex",
            "addrelation",
            "addshape",
            "call",
            "pop",
            "push",
            "nop",
            "def",
            "defgen",
            "callobj",
            "awaitobj",
            "streamwaitobj",
            "await",
            "label",
            "cmp",
            "je",
            "jmp",
            "getobj",
            "setobj",
            "streamwait"
        };

        private bool NextAsmTokenStartsOpcode()
        {
            var offset = 0;
            while (true)
            {
                var tok = CurrentScanner.Watch(offset);
                if (tok == null || tok.Value.Kind == TokenKind.EndOfInput)
                    return false;
                if (tok.Value.Kind == TokenKind.Newline)
                {
                    offset++;
                    continue;
                }
                return tok.Value.Kind == TokenKind.Identifier && AsmOpcodes.Contains(tok.Value.Value ?? "");
            }
        }

    }
}
