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
