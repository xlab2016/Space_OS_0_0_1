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
            var text = NormalizeInstructionText(CurrentScanner.Slice(start, end).Trim());
            if (!string.IsNullOrWhiteSpace(text))
                output.Add(text);
        }

        private void AddRangeAsStatementWithSourceLine(int start, int end, List<(string Text, int Line)> output)
            => AddRangeAsStatementWithSourceLine(CurrentScanner, start, end, output);

        private static void AddRangeAsStatementWithSourceLine(Scanner sc, int start, int end, List<(string Text, int Line)> output)
        {
            if (start < 0 || end <= start) return;
            var raw = sc.Slice(start, end).Trim();
            if (string.IsNullOrWhiteSpace(raw)) return;
            var text = ShouldPreserveNewlinesInStatement(raw)
                ? NormalizeInstructionTextPreservingNewlines(raw)
                : NormalizeInstructionText(raw);
            if (!string.IsNullOrWhiteSpace(text))
                output.Add((text, sc.GetLineNumber(start)));
        }

        private static bool LooksLikeSwitchStatement(string rawTrimmed)
        {
            var t = rawTrimmed.TrimStart();
            if (t.Length < 7 || !t.StartsWith("switch", StringComparison.OrdinalIgnoreCase))
                return false;
            if (t.Length > 6 && char.IsLetterOrDigit(t[6]))
                return false;
            return t.IndexOf('{') >= 0;
        }

        /// <summary>«//» вне строк — иначе <see cref="NormalizeInstructionText"/> слепляет следующую строку с комментарием.</summary>
        private static bool RawContainsLineCommentOutsideStrings(string raw)
        {
            var inString = false;
            var escaped = false;
            for (var i = 0; i + 1 < raw.Length; i++)
            {
                var ch = raw[i];
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

                    if (ch == '"')
                        inString = false;
                    continue;
                }

                if (ch == '"')
                {
                    inString = true;
                    continue;
                }

                if (ch == '/' && raw[i + 1] == '/')
                    return true;
            }

            return false;
        }

        private static bool ShouldPreserveNewlinesInStatement(string raw) =>
            LooksLikeSwitchStatement(raw) || RawContainsLineCommentOutsideStrings(raw);

        /// <summary>Как <see cref="NormalizeInstructionText(string)"/>, но переводы строк сохраняются (одна \n на разрыв).</summary>
        private static string NormalizeInstructionTextPreservingNewlines(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var sb = new System.Text.StringBuilder(text.Length);
            var inString = false;
            var escaped = false;
            var pendingSpace = false;

            foreach (var ch in text)
            {
                if (ch == '\r')
                    continue;
                if (ch == '\n')
                {
                    sb.Append('\n');
                    pendingSpace = false;
                    continue;
                }

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
                    if (pendingSpace && sb.Length > 0 && sb[sb.Length - 1] != ' ' && sb[sb.Length - 1] != '\n')
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

                if (pendingSpace && sb.Length > 0 && sb[sb.Length - 1] != ' ' && sb[sb.Length - 1] != '\n' &&
                    sb[sb.Length - 1] != '.')
                    sb.Append(' ');
                pendingSpace = false;
                sb.Append(ch);
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// Нормализует текст инструкции:
        /// - схлопывает пробелы вне строк;
        /// - сохраняет пробел перед строковыми литералами, если был;
        /// - не трогает содержимое строк.
        /// </summary>
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

                if (pendingSpace && sb.Length > 0 && sb[sb.Length - 1] != ' ' && sb[sb.Length - 1] != '.')
                    sb.Append(' ');
                pendingSpace = false;
                sb.Append(ch);
            }

            return sb.ToString().Trim();
        }

        private void AddRangeAsAsmInstruction(int start, int end, List<InstructionNode> output)
        {
            if (start < 0 || end <= start) return;
            var text = NormalizeInstructionText(CurrentScanner.Slice(start, end).Trim());
            if (string.IsNullOrWhiteSpace(text))
                return;
            var instruction = ParseInstruction(text);
            output.Add(instruction);
        }

        /// <summary>Потребляет обычный блок (после "{") и сразу парсит его в <see cref="StatementNode"/> до парной "}".</summary>
        private List<StatementNode> ParseStatementBlock()
            => ParseStatementBlock(CurrentScanner);

        /// <summary>Вариант <see cref="ParseStatementBlock()"/> для произвольного <see cref="Scanner"/> (вложенные function из фрагмента текста).</summary>
        private List<StatementNode> ParseStatementBlock(Scanner sc)
        {
            var result = new List<StatementNode>();
            var depth = 1;
            var parenDepth = 0;
            var bracketDepth = 0;
            var currentStart = -1;
            var currentEnd = -1;

            while (true)
            {
                var tok = sc.Scan();
                if (tok.Kind == TokenKind.EndOfInput)
                {
                    TryParseStatement(sc, currentStart, currentEnd, result);
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
                        TryParseStatement(sc, currentStart, currentEnd, result);
                        break;
                    }
                    if (currentStart < 0) currentStart = tok.Start;
                    currentEnd = tok.End;
                    continue;
                }

                if (tok.Kind == TokenKind.LParen)
                {
                    parenDepth++;
                    if (currentStart < 0) currentStart = tok.Start;
                    currentEnd = tok.End;
                    continue;
                }

                if (tok.Kind == TokenKind.RParen)
                {
                    parenDepth = Math.Max(0, parenDepth - 1);
                    if (currentStart < 0) currentStart = tok.Start;
                    currentEnd = tok.End;
                    continue;
                }

                if (tok.Kind == TokenKind.LBracket)
                {
                    bracketDepth++;
                    if (currentStart < 0) currentStart = tok.Start;
                    currentEnd = tok.End;
                    continue;
                }

                if (tok.Kind == TokenKind.RBracket)
                {
                    bracketDepth = Math.Max(0, bracketDepth - 1);
                    if (currentStart < 0) currentStart = tok.Start;
                    currentEnd = tok.End;
                    continue;
                }

                if (depth == 1 && parenDepth == 0 && bracketDepth == 0 &&
                    (tok.Kind == TokenKind.Semicolon || tok.Kind == TokenKind.Newline))
                {
                    // Разрешаем переносы строк в цепочках вызовов вида:
                    // db.Table<>.
                    //   where(...)
                    //   .max(...);
                    // Если текущий statement оканчивается на '.', не разрываем его по Newline.
                    if (tok.Kind == TokenKind.Newline && currentStart >= 0 && currentEnd > currentStart)
                    {
                        var slice = sc.Slice(currentStart, currentEnd).TrimEnd();
                        if (slice.EndsWith(".", StringComparison.Ordinal))
                        {
                            continue;
                        }
                    }
                    TryParseStatement(sc, currentStart, currentEnd, result);
                    currentStart = -1;
                    currentEnd = -1;
                    continue;
                }

                if (currentStart < 0) currentStart = tok.Start;
                currentEnd = tok.End;
            }

            return result;
        }

        private void TryParseStatement(Scanner sc, int start, int end, List<StatementNode> output)
        {
            if (start < 0 || end <= start) return;
            var raw = sc.Slice(start, end).Trim();
            if (string.IsNullOrWhiteSpace(raw)) return;

            var text = ShouldPreserveNewlinesInStatement(raw)
                ? NormalizeInstructionTextPreservingNewlines(raw)
                : NormalizeInstructionText(raw);

            if (string.IsNullOrWhiteSpace(text)) return;

            var line = sc.GetLineNumber(start);

            if (TryParseNestedFunctionStatementText(text, line, out var nested) && nested != null)
            {
                output.Add(nested);
                return;
            }

            output.Add(new StatementLineNode { Text = text, SourceLine = line });
        }

        private bool TryParseNestedFunctionStatementText(string text, int sourceLine, out NestedFunctionNode? nested)
        {
            nested = null;
            var trimmed = text.Trim();
            if (trimmed.Length == 0) return false;
            var sc = new Scanner(trimmed);
            if (sc.Current.Kind != TokenKind.Identifier)
                return false;
            var keyword = sc.Current.Value;
            if (!string.Equals(keyword, "function", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(keyword, "procedure", StringComparison.OrdinalIgnoreCase))
                return false;

            var startPos = sc.Save();
            sc.Scan(); // function | procedure
            if (sc.Current.Kind != TokenKind.Identifier)
            {
                sc.Restore(startPos);
                return false;
            }

            var name = sc.Scan().Value ?? "";
            var parameters = ParseOptionalSignatureParametersFromScanner(sc);
            if (sc.Current.Kind != TokenKind.LBrace)
            {
                sc.Restore(startPos);
                return false;
            }

            sc.Scan(); // '{'
            var innerStatements = ParseStatementBlock(sc);
            var body = new BodyNode { Statements = innerStatements };
            nested = new NestedFunctionNode
            {
                Name = name,
                Parameters = parameters,
                Body = body,
                IsProcedure = string.Equals(keyword, "procedure", StringComparison.OrdinalIgnoreCase)
            };
            return true;
        }

        /// <summary>Вариант сигнатуры <c>(a, b, …)</c> для произвольного <see cref="Scanner"/> (вложенные function из фрагмента текста).</summary>
        private static List<string> ParseOptionalSignatureParametersFromScanner(Scanner sc)
        {
            var parameters = new List<string>();
            while (sc.Current.Kind == TokenKind.Newline)
                sc.Scan();
            if (sc.Current.Kind != TokenKind.LParen)
                return parameters;

            sc.Scan(); // '('
            while (sc.Current.Kind != TokenKind.RParen && !sc.Current.IsEndOfInput)
            {
                while (sc.Current.Kind == TokenKind.Newline) sc.Scan();
                if (sc.Current.Kind == TokenKind.Identifier)
                {
                    parameters.Add(sc.Scan().Value ?? "");
                    while (sc.Current.Kind == TokenKind.Newline) sc.Scan();
                    continue;
                }

                if (sc.Current.Kind == TokenKind.Comma)
                {
                    sc.Scan();
                    while (sc.Current.Kind == TokenKind.Newline) sc.Scan();
                    continue;
                }

                break;
            }

            if (sc.Current.Kind == TokenKind.RParen)
                sc.Scan();
            while (sc.Current.Kind == TokenKind.Newline)
                sc.Scan();
            return parameters;
        }

        /// <summary>Потребляет asm-блок (после "asm {") и сразу парсит его в инструкции до парной "}".</summary>
        private List<InstructionNode> ParseAsmBlockInstructions()
        {
            var result = new List<InstructionNode>();
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
                        AddRangeAsAsmInstruction(currentStart, currentEnd, result);
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
                            AddRangeAsAsmInstruction(currentStart, currentEnd, result);
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
                            AddRangeAsAsmInstruction(currentStart, currentEnd, result);
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
                            AddRangeAsAsmInstruction(currentStart, currentEnd, result);
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
        private bool TryParseBodyBlock(out BodyNode body)
        {
            body = new BodyNode();
            if (CurrentScanner.Current.Kind != TokenKind.LBrace) return false;
            CurrentScanner.Scan(); // consume outer "{"
            SkipNewlines();

            // asm-block = "asm" "{" asm-instructions "}"
            if (TryParseAsmBlock(out var asmInstructions))
            {
                body.Statements.AddRange(asmInstructions);
                SkipNewlines();
                if (CurrentScanner.Current.Kind != TokenKind.RBrace)
                    throw new CompilationException($"Expected '}}' to close block at position {CurrentScanner.Current.Start}.", CurrentScanner.Current.Start);
                CurrentScanner.Scan(); // consume outer "}"
                return true;
            }

            // statement-block = { statements }
            var nodes = ParseStatementBlock(); // consumes outer "}"
            body.Statements.AddRange(nodes);
            return true;
        }

        /// <summary>Пытается разобрать asm-блок формата <c>asm { ... }</c> и вернуть список инструкций.</summary>
        private bool TryParseAsmBlock(out List<InstructionNode> instructions)
        {
            instructions = new List<InstructionNode>();

            if (CurrentScanner.Current.Kind != TokenKind.Identifier ||
                !string.Equals(CurrentScanner.Current.Value, "asm", StringComparison.OrdinalIgnoreCase))
                return false;

            CurrentScanner.Scan(); // consume 'asm'
            SkipNewlines();

            if (CurrentScanner.Current.Kind != TokenKind.LBrace)
                throw new CompilationException($"Expected '{{' after asm at position {CurrentScanner.Current.Start}.", CurrentScanner.Current.Start);

            CurrentScanner.Scan(); // consume asm "{"
            instructions = ParseAsmBlockInstructions(); // consumes asm "}"
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

        /// <summary>Общий разбор блока выполнения (procedure/function) с единообразным сообщением об ошибке.</summary>
        private BodyNode TryParseExecutionUnitBlock(string unitKind, string name)
        {
            SkipNewlines();
            if (!TryParseBodyBlock(out var body))
                throw new CompilationException(
                    $"Expected body block for {unitKind} '{name}' at position {CurrentScanner.Current.Start}.",
                    CurrentScanner.Current.Start);
            return body;
        }

        private bool TryParseProcedure(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier || !string.Equals(CurrentScanner.Current.Value, "procedure", StringComparison.OrdinalIgnoreCase)) return false;
            ExpectToken(TokenKind.Identifier, "procedure");
            var name = ExpectToken(TokenKind.Identifier).Value ?? "";
            var parameters = TryParseArgs();

            var body = TryParseExecutionUnitBlock("procedure", name);
            var node = new Ast.ProcedureNode
            {
                Name = name,
                Parameters = parameters,
                Body = body
            };
            structure.Procedures[name] = node;
            structure.IsProgramStructure = true;
            return true;
        }

        /// <summary>
        /// Парсит список параметров сигнатуры вида <c>(a, b, ...)</c> для текущего <see cref="CurrentScanner"/>.
        /// Если скобок нет, возвращает пустой список и не потребляет токены.
        /// </summary>
        private List<string> TryParseArgs()
        {
            var parameters = new List<string>();
            if (CurrentScanner.Current.Kind != TokenKind.LParen)
                return parameters;

            CurrentScanner.Scan(); // consume '('
            while (CurrentScanner.Current.Kind != TokenKind.RParen && !CurrentScanner.Current.IsEndOfInput)
            {
                if (CurrentScanner.Current.Kind == TokenKind.Identifier)
                {
                    parameters.Add(CurrentScanner.Scan().Value ?? "");
                    continue;
                }

                if (CurrentScanner.Current.Kind == TokenKind.Comma)
                {
                    CurrentScanner.Scan();
                    continue;
                }

                break;
            }

            if (CurrentScanner.Current.Kind == TokenKind.RParen)
                CurrentScanner.Scan(); // consume ')'

            return parameters;
        }

        private bool TryParseFunction(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier || !string.Equals(CurrentScanner.Current.Value, "function", StringComparison.OrdinalIgnoreCase)) return false;
            ExpectToken(TokenKind.Identifier, "function");
            var name = ExpectToken(TokenKind.Identifier).Value ?? "";

            SkipNewlines();
            var parameters = TryParseArgs();

            var body = TryParseExecutionUnitBlock("function", name);
            var node = new Ast.FunctionNode
            {
                Name = name,
                Parameters = parameters,
                Body = body
            };
            structure.Functions[name] = node;
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
            structure.EntryPoint = body.Statements;
            structure.IsProgramStructure = true;
            return true;
        }

        private bool TryParseUse(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier ||
                !string.Equals(CurrentScanner.Current.Value, "use", StringComparison.OrdinalIgnoreCase))
                return false;

            ExpectToken(TokenKind.Identifier, "use");
            SkipNewlines();

            // Module path: id ( ( ':' | '\' | '/' ) id )*  e.g. modularity:module1 or modularity\module1
            var segments = new List<string>();
            if (CurrentScanner.Current.Kind != TokenKind.Identifier ||
                string.Equals(CurrentScanner.Current.Value, "as", StringComparison.OrdinalIgnoreCase))
                return false;

            segments.Add(CurrentScanner.Scan().Value ?? "");
            SkipNewlines();

            while (true)
            {
                var tok = CurrentScanner.Current;
                if (tok.Kind == TokenKind.Identifier &&
                    string.Equals(tok.Value, "as", StringComparison.OrdinalIgnoreCase))
                    break;
                if (tok.Kind == TokenKind.Semicolon || tok.Kind == TokenKind.Newline || tok.Kind == TokenKind.LBrace)
                    break;
                if (tok.IsEndOfInput)
                    break;

                var isSep = tok.Kind == TokenKind.Colon
                    || (tok.Kind == TokenKind.Identifier &&
                        (string.Equals(tok.Value, "\\", StringComparison.Ordinal)
                         || string.Equals(tok.Value, "/", StringComparison.Ordinal)));
                if (!isSep)
                    return false;

                CurrentScanner.Scan();
                SkipNewlines();
                if (CurrentScanner.Current.Kind != TokenKind.Identifier ||
                    string.Equals(CurrentScanner.Current.Value, "as", StringComparison.OrdinalIgnoreCase))
                    throw new CompilationException("Expected module name after path separator in 'use'.", CurrentScanner.Current.Start);

                segments.Add(CurrentScanner.Scan().Value ?? "");
                SkipNewlines();
            }

            var modulePath = string.Join("\\", segments);
            if (string.IsNullOrWhiteSpace(modulePath))
                throw new CompilationException("Expected module path after 'use'.", CurrentScanner.Current.Start);

            SkipNewlines();

            var use = new UseNode { ModulePath = modulePath.Trim() };

            if (CurrentScanner.Current.Kind == TokenKind.Identifier &&
                string.Equals(CurrentScanner.Current.Value, "as", StringComparison.OrdinalIgnoreCase))
            {
                CurrentScanner.Scan(); // consume 'as'
                SkipNewlines();

                if (CurrentScanner.Current.Kind == TokenKind.Identifier)
                {
                    // use <module> as <alias>;
                    use.Alias = ExpectToken(TokenKind.Identifier).Value ?? "";
                    SkipNewlines();
                    while (CurrentScanner.Current.Kind == TokenKind.Semicolon)
                        CurrentScanner.Scan();
                    structure.UseDirectives.Add(use);
                    structure.IsProgramStructure = true;
                    return true;
                }

                if (CurrentScanner.Current.Kind != TokenKind.LBrace)
                    throw new CompilationException("Expected alias identifier or '{' after 'use ... as'.", CurrentScanner.Current.Start);

                // use <module> as { function f(...); procedure p(...); };
                CurrentScanner.Scan(); // consume '{'
                SkipNewlines();

                while (!CurrentScanner.Current.IsEndOfInput)
                {
                    SkipNewlines();
                    while (CurrentScanner.Current.Kind == TokenKind.Semicolon)
                        CurrentScanner.Scan();

                    // Closing brace can be preceded by newlines (e.g. after signatures).
                    if (CurrentScanner.Current.Kind == TokenKind.RBrace)
                        break;

                    if (CurrentScanner.Current.Kind == TokenKind.Identifier &&
                        string.Equals(CurrentScanner.Current.Value, "function", StringComparison.OrdinalIgnoreCase))
                    {
                        CurrentScanner.Scan(); // consume 'function'
                        var name = ExpectToken(TokenKind.Identifier).Value ?? "";
                        var parameters = ParseOptionalSignatureParameters();

                        while (CurrentScanner.Current.Kind == TokenKind.Semicolon)
                            CurrentScanner.Scan();

                        use.Signatures ??= new List<UseImportSignature>();
                        use.Signatures.Add(new UseImportSignature
                        {
                            Kind = "function",
                            Name = name,
                            ParameterNames = parameters
                        });
                        continue;
                    }

                    if (CurrentScanner.Current.Kind == TokenKind.Identifier &&
                        string.Equals(CurrentScanner.Current.Value, "procedure", StringComparison.OrdinalIgnoreCase))
                    {
                        CurrentScanner.Scan(); // consume 'procedure'
                        var name = ExpectToken(TokenKind.Identifier).Value ?? "";
                        var parameters = ParseOptionalSignatureParameters();

                        while (CurrentScanner.Current.Kind == TokenKind.Semicolon)
                            CurrentScanner.Scan();

                        use.Signatures ??= new List<UseImportSignature>();
                        use.Signatures.Add(new UseImportSignature
                        {
                            Kind = "procedure",
                            Name = name,
                            ParameterNames = parameters
                        });
                        continue;
                    }

                    throw new CompilationException(
                        "Expected 'function <name>(...)' or 'procedure <name>(...)' inside use-as '{ ... }'.",
                        CurrentScanner.Current.Start);
                }

                if (CurrentScanner.Current.Kind != TokenKind.RBrace)
                    throw new CompilationException("Expected '}' to close use-as block.", CurrentScanner.Current.Start);

                CurrentScanner.Scan(); // consume '}'
                SkipNewlines();
                while (CurrentScanner.Current.Kind == TokenKind.Semicolon)
                    CurrentScanner.Scan();

                structure.UseDirectives.Add(use);
                structure.IsProgramStructure = true;
                return true;
            }

            // use <modulePath>;
            while (CurrentScanner.Current.Kind == TokenKind.Semicolon)
                CurrentScanner.Scan();

            structure.UseDirectives.Add(use);
            structure.IsProgramStructure = true;
            return true;
        }

        private List<string> ParseOptionalSignatureParameters()
        {
            var parameters = new List<string>();
            SkipNewlines();
            if (CurrentScanner.Current.Kind != TokenKind.LParen)
                return parameters;

            CurrentScanner.Scan(); // consume '('
            SkipNewlines();
            while (CurrentScanner.Current.Kind != TokenKind.RParen && !CurrentScanner.Current.IsEndOfInput)
            {
                SkipNewlines();
                if (CurrentScanner.Current.Kind == TokenKind.Identifier)
                {
                    parameters.Add(CurrentScanner.Scan().Value ?? "");
                    SkipNewlines();
                    continue;
                }

                if (CurrentScanner.Current.Kind == TokenKind.Comma)
                {
                    CurrentScanner.Scan();
                    SkipNewlines();
                    continue;
                }

                break;
            }
            if (CurrentScanner.Current.Kind == TokenKind.RParen)
                CurrentScanner.Scan(); // consume ')'
            SkipNewlines();
            return parameters;
        }

        private bool TryParseMainBlock(ProgramStructure structure)
        {
            if (CurrentScanner.Current.Kind != TokenKind.Identifier || IsProgramKeyword(CurrentScanner.Current.Value ?? "")) return false;
            var name = CurrentScanner.Current.Value ?? "";
            if (CurrentScanner.Watch(1)?.Kind != TokenKind.Semicolon) return false;
            CurrentScanner.Scan();
            CurrentScanner.Scan();
            structure.EntryPoint ??= new List<StatementNode>();
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

        private bool TryParseType(ProgramStructure structure)
        {
            // Обобщённый разбор деклараций вида:
            //   Name: table  { ... }
            //   Name: database { ... }
            //   Point: type { ... }
            //   Shape: class { ... }
            //   Circle: Shape { ... }
            // и т.п.
            //
            // На уровне Parser мы не навязываем семантику kind, а просто
            // вырезаем цельный фрагмент "Name: <kind/...> { ... }" в Prelude,
            // чтобы дальнейший lowering (StatementLoweringCompiler) решил,
            // что с ним делать.

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
            CurrentScanner.Scan(); // ':'

            // Ожидаем хотя бы один идентификатор (kind или базовый тип),
            // затем обязательно '{' с телом.
            if (CurrentScanner.Current.Kind != TokenKind.Identifier)
            {
                CurrentScanner.Restore(pos);
                return false;
            }

            // Пропускаем последовательность токенов до первой '{'
            var seenIdentifier = false;
            while (!CurrentScanner.Current.IsEndOfInput &&
                   CurrentScanner.Current.Kind != TokenKind.LBrace &&
                   CurrentScanner.Current.Kind != TokenKind.Newline &&
                   CurrentScanner.Current.Kind != TokenKind.Semicolon)
            {
                if (CurrentScanner.Current.Kind == TokenKind.Identifier)
                    seenIdentifier = true;
                CurrentScanner.Scan();
            }

            if (!seenIdentifier || CurrentScanner.Current.Kind != TokenKind.LBrace)
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

            structure.Types.Add(new TypeNode
            {
                Text = text,
                SourceLine = CurrentScanner.GetLineNumber(start)
            });
            structure.IsProgramStructure = true;
            return true;
        }

        private (string Text, int Line) ConsumeLineToUnprocessedWithLine()
        {
            if (CurrentScanner.Current.IsEndOfInput) return ("", 0);
            var start = CurrentScanner.Current.Start;
            var lineNo = CurrentScanner.GetLineNumber(start);
            while (!CurrentScanner.Current.IsEndOfInput && CurrentScanner.Current.Kind != TokenKind.Newline)
                CurrentScanner.Scan();
            var end = CurrentScanner.Current.IsEndOfInput ? CurrentScanner.SourceLength : CurrentScanner.Current.Start;
            if (CurrentScanner.Current.Kind == TokenKind.Newline) CurrentScanner.Scan();
            var text = CurrentScanner.Slice(start, end).Trim();
            return (text, lineNo);
        }

        private InstructionNode ParseInstruction(string instructionText)
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
            var unprocessedLines = new List<(string Text, int Line)>();

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
                if (TryParseUse(structure)) continue;
                if (TryParseType(structure)) continue;
                if (TryParseMainBlock(structure)) continue;

                var (line, lineNo) = ConsumeLineToUnprocessedWithLine();
                if (!string.IsNullOrWhiteSpace(line) && !structure.IsProgramStructure)
                    unprocessedLines.Add((line, lineNo));
            }

            if (!structure.IsProgramStructure)
                structure.EntryPoint = ParseAsm(source, unprocessedLines);

            return structure;
        }

        /// <summary>
        /// Парсит исходный код как ASM-программу в терминах <see cref="StatementNode"/> для ProgramStructure.EntryPoint.
        /// Если нет специальных строк (<paramref name="unprocessedLines"/> пуст), используется полный текст.
        /// Иначе парсятся только переданные строки с сохранением номеров.
        /// </summary>
        private List<StatementNode> ParseAsm(string sourceCode, List<(string Text, int Line)> unprocessedLines)
        {
            if (unprocessedLines == null || unprocessedLines.Count == 0)
                return ParseAsmStructure(sourceCode).Cast<StatementNode>().ToList();

            var nodes = new List<StatementNode>();
            foreach (var (lt, ln) in unprocessedLines)
            {
                var node = ParseInstruction(lt);
                node.SourceLine = ln;
                nodes.Add(node);
            }

            return nodes;
        }

        /// <summary>Парсит исходный код как плоский набор инструкций (по строкам). Для обратной совместимости, когда нет program/entrypoint.</summary>
        public List<InstructionNode> ParseAsmStructure(string sourceCode)
        {
            var nodes = new List<InstructionNode>();
            var reader = new LineReader(sourceCode ?? "");
            for (var i = 0; i < reader.Count; i++)
            {
                var (_, trimmedLine) = reader.GetLine(i);
                if (string.IsNullOrWhiteSpace(trimmedLine))
                    continue;
                var node = _instructionParser.Parse(trimmedLine);
                node.SourceLine = i + 1;
                nodes.Add(node);
            }
            return nodes;
        }

        private static readonly HashSet<string> AsmOpcodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "addvertex",
            "addrelation",
            "addshape",
            "call",
            "acall",
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
            "streamwait",
            "not"
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
