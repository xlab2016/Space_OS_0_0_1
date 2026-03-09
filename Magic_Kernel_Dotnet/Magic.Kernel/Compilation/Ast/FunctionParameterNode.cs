namespace Magic.Kernel.Compilation.Ast
{
    public class FunctionParameterNode : ParameterNode
    {
        public string ParameterName { get; set; } = string.Empty;
        public string EntityType { get; set; } = string.Empty;
        public long Index { get; set; }
    }
}
