namespace Magic.Kernel.Compilation.Ast
{
    /// <summary>Raw high-level statement text from non-asm block.</summary>
    public class StatementLineNode : StatementNode
    {
        /// <summary>1-based строка AGI для statement-блока.</summary>
        public int SourceLine { get; set; }

        public string Text { get; set; } = string.Empty;
    }

    /// <summary>Отдельный узел для деклараций типов в прологе программы.</summary>
    public sealed class TypeNode : StatementLineNode
    {
    }
}
