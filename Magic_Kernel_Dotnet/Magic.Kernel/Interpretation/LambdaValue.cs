using System.Collections.Generic;
using Magic.Kernel.Processor;

namespace Magic.Kernel.Interpretation
{
    /// <summary>Runtime value for a lambda: body (for in-memory invoke) and optional ExprTree (for Db/SQL).</summary>
    public sealed class LambdaValue
    {
        /// <summary>Instructions between Expr and DefExpr (DefExpr excluded). Used by InvokeLambdaAsync.</summary>
        public IReadOnlyList<Command> Body { get; }

        /// <summary>Tree built at DefExpr from Body; passed to driver for translation (e.g. Postgres visitor → SQL WHERE).</summary>
        public ExprTree? ExprTree { get; }

        public LambdaValue(IReadOnlyList<Command> body, ExprTree? exprTree = null)
        {
            Body = body ?? throw new ArgumentNullException(nameof(body));
            ExprTree = exprTree;
        }
    }
}
