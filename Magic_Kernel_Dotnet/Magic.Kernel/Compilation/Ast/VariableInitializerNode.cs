namespace Magic.Kernel.Compilation.Ast
{
    public class VariableInitializerNode : AstNode
    {
        public Dictionary<string, object> Properties { get; set; } = new Dictionary<string, object>();
    }
}
