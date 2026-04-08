namespace Magic.Kernel.Compilation.Ast
{
    public class InstructionNode : StatementNode
    {
        /// <summary>1-based строка исходника AGI; 0 — не привязано.</summary>
        public int SourceLine { get; set; }

        public string Opcode { get; set; } = string.Empty;
        public List<ParameterNode> Parameters { get; set; } = new List<ParameterNode>();
    }

}
