using System.Collections.Generic;

namespace Magic.Kernel.Compilation.Ast
{
    /// <summary>Общий узел для тела процедуры/функции/entrypoint.</summary>
    public sealed class BodyNode : AstNode
    {
        public List<StatementNode> Statements { get; set; } = new List<StatementNode>();
    }
}

