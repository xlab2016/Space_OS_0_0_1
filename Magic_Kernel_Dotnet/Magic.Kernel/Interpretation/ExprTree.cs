using System;
using System.Collections.Generic;
using Magic.Kernel.Processor;

namespace Magic.Kernel.Interpretation
{
    /// <summary>Provider-agnostic expression tree for lambda predicates. Built by Interpreter from body (with memory resolution). Translation to SQL/other is done by the driver (e.g. visitor).</summary>
    public abstract class ExprTree
    {
    }

    public sealed class ExprParameter : ExprTree
    {
        public int Index { get; }
        public ExprParameter(int index) => Index = index;
    }

    public sealed class ExprConstant : ExprTree
    {
        public object? Value { get; }
        public ExprConstant(object? value) => Value = value;
    }

    public sealed class ExprMemberAccess : ExprTree
    {
        public ExprTree Target { get; }
        public string MemberName { get; }
        public ExprMemberAccess(ExprTree target, string memberName)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            MemberName = memberName ?? throw new ArgumentNullException(nameof(memberName));
        }
    }

    public sealed class ExprEqual : ExprTree
    {
        public ExprTree Left { get; }
        public ExprTree Right { get; }
        public ExprEqual(ExprTree left, ExprTree right)
        {
            Left = left ?? throw new ArgumentNullException(nameof(left));
            Right = right ?? throw new ArgumentNullException(nameof(right));
        }
    }

    public sealed class ExprAnd : ExprTree
    {
        public ExprTree Left { get; }
        public ExprTree Right { get; }

        public ExprAnd(ExprTree left, ExprTree right)
        {
            Left = left ?? throw new ArgumentNullException(nameof(left));
            Right = right ?? throw new ArgumentNullException(nameof(right));
        }
    }

    /// <summary>Lambda expression: argument expressions (typically ExprParameter) and body expression.</summary>
    public sealed class ExprLambda : ExprTree
    {
        public IReadOnlyList<ExprTree> Args { get; }
        public ExprTree Body { get; }

        public ExprLambda(IReadOnlyList<ExprTree> args, ExprTree body)
        {
            Args = args ?? throw new ArgumentNullException(nameof(args));
            Body = body ?? throw new ArgumentNullException(nameof(body));
        }
    }
}
