namespace Magic.Kernel.Compilation.Ast
{
    public abstract class ParameterNode : AstNode
    {
        public string Name { get; set; } = string.Empty;
    }
}
