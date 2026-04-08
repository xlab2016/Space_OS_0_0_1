using System.Collections.Generic;

namespace Magic.Kernel.Compilation.Ast
{
    public sealed class ProcedureNode : AstNode
    {
        public string Name { get; init; } = "";
        public List<string> Parameters { get; init; } = new List<string>();
        public BodyNode Body { get; init; } = new BodyNode();
    }

    public sealed class FunctionNode : AstNode
    {
        public string Name { get; init; } = "";
        public List<string> Parameters { get; init; } = new List<string>();
        public BodyNode Body { get; init; } = new BodyNode();
    }
}

