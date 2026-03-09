namespace Magic.Kernel.Compilation.Ast
{
    public class VariableDeclarationNode : AstNode
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty; // vertex, relation, shape
        public VariableInitializerNode? Initializer { get; set; }
    }
}
