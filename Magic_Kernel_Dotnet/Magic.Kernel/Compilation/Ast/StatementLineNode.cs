namespace Magic.Kernel.Compilation.Ast
{
    /// <summary>Raw high-level statement text from non-asm block.</summary>
    public class StatementLineNode : AstNode
    {
        public string Text { get; set; } = string.Empty;
    }
}
