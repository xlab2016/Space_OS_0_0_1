namespace Magic.Kernel.Compilation.Ast
{
    public class InstructionNode : AstNode
    {
        public string Opcode { get; set; } = string.Empty;
        public List<ParameterNode> Parameters { get; set; } = new List<ParameterNode>();
    }
}
