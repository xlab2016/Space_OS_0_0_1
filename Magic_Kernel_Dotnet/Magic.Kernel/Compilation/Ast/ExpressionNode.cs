namespace Magic.Kernel.Compilation.Ast
{
    public class ExpressionNode : AstNode
    {
        public ExpressionType Type { get; set; }
        public string? Operator { get; set; } // =>, ], |
        public AstNode? Left { get; set; }
        public AstNode? Right { get; set; }
        public string? VariableName { get; set; }
        public VariableInitializerNode? Literal { get; set; }
    }

    public enum ExpressionType
    {
        Variable,
        Literal,
        BinaryOperator,
        UnaryOperator
    }
}
