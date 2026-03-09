using System.Collections.Generic;

namespace Magic.Kernel.Compilation.Ast
{
    public class ComplexValueParameterNode : ParameterNode
    {
        public string ParameterName { get; set; } = string.Empty;
        public Dictionary<string, object> Value { get; set; } = new Dictionary<string, object>();
    }
}
