namespace Magic.Kernel.Compilation.Ast
{
    public class StatementNode : AstNode
    {
        public StatementType Type { get; set; }
        public string? VariableName { get; set; }
        public AstNode? Expression { get; set; }
        public string? FunctionName { get; set; }
        public List<AstNode>? Arguments { get; set; }
    }

    public enum StatementType
    {
        VariableDeclaration,
        Assignment,
        FunctionCall,
        ProcedureCall
    }
}
