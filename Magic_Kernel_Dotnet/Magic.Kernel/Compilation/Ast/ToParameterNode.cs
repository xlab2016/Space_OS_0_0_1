namespace Magic.Kernel.Compilation.Ast
{
    public class ToParameterNode : ParameterNode
    {
        public string EntityType { get; set; } = string.Empty;
        public long Index { get; set; }
    }
}
