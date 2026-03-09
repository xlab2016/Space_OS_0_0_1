namespace Magic.Kernel.Compilation.Ast
{
    public class VerticesParameterNode : ParameterNode
    {
        public List<long> Indices { get; set; } = new List<long>();
    }
}
