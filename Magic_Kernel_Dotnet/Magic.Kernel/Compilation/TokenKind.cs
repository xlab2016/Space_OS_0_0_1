namespace Magic.Kernel.Compilation
{
    /// <summary>Типы токенов, выдаваемых сканером.</summary>
    public enum TokenKind
    {
        EndOfInput,
        Identifier,
        Number,
        Float,
        StringLiteral,
        /// <summary>Строка от // до конца строки (только при токенизации для подсветки).</summary>
        LineComment,
        Colon,
        Comma,
        LBracket,
        RBracket,
        LParen,
        RParen,
        LBrace,
        RBrace,
        Dot,
        Assign,
        LessThan,
        GreaterThan,
        Semicolon,
        Newline,
    }
}
