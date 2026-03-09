using System;

namespace Magic.Kernel.Compilation
{
    /// <summary>Один токен: вид, значение, позиция в исходном тексте.</summary>
    public readonly struct Token
    {
        public TokenKind Kind { get; }
        public string Value { get; }
        public int Start { get; }
        public int End { get; }

        public Token(TokenKind kind, string value, int start, int end)
        {
            Kind = kind;
            Value = value ?? string.Empty;
            Start = start;
            End = end;
        }

        public static Token Eof(int position) => new Token(TokenKind.EndOfInput, "", position, position);

        public bool IsEndOfInput => Kind == TokenKind.EndOfInput;
        public bool IsIdentifier => Kind == TokenKind.Identifier;
        public bool IsNumber => Kind == TokenKind.Number || Kind == TokenKind.Float;
        public bool IsString => Kind == TokenKind.StringLiteral;

        public bool IsPunctuation(char c)
        {
            return Kind == TokenKind.Colon && c == ':' ||
                   Kind == TokenKind.Comma && c == ',' ||
                   Kind == TokenKind.LBracket && c == '[' ||
                   Kind == TokenKind.RBracket && c == ']' ||
                   Kind == TokenKind.LParen && c == '(' ||
                   Kind == TokenKind.RParen && c == ')' ||
                   Kind == TokenKind.LBrace && c == '{' ||
                   Kind == TokenKind.RBrace && c == '}' ||
                   Kind == TokenKind.Dot && c == '.' ||
                   Kind == TokenKind.Assign && c == '=' ||
                   Kind == TokenKind.LessThan && c == '<' ||
                   Kind == TokenKind.GreaterThan && c == '>';
        }

        public override string ToString() => Kind == TokenKind.EndOfInput ? "<eof>" : $"{Kind}:{Value}";
    }
}
