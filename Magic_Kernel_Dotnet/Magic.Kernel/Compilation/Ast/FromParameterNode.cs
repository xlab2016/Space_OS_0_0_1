namespace Magic.Kernel.Compilation.Ast
{
    public class FromParameterNode : ParameterNode
    {
        public string EntityType { get; set; } = string.Empty;
        public long Index { get; set; }
    }
}
