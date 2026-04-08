using System.Collections.Generic;

namespace Magic.Kernel.Compilation.Ast
{
    public class LambdaParametersParameterNode : ParameterNode
    {
        public List<string> Parameters { get; set; } = new List<string>();
    }
}
