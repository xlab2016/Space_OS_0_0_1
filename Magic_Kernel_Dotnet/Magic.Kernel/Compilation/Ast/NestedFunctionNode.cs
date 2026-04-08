using System.Collections.Generic;

namespace Magic.Kernel.Compilation.Ast
{
    /// <summary>Локальная подпрограмма (<c>function</c> или <c>procedure</c>) внутри тела процедуры или функции (произвольная глубина вложенности).</summary>
    public sealed class NestedFunctionNode : StatementNode
    {
        public string Name { get; set; } = "";
        public List<string> Parameters { get; set; } = new List<string>();
        public BodyNode Body { get; set; } = new BodyNode();

        /// <summary><c>true</c> если объявлено как <c>procedure</c> (юнит в словаре процедур), иначе как <c>function</c>.</summary>
        public bool IsProcedure { get; set; }
    }
}
