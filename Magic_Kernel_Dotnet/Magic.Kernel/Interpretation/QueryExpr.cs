using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Magic.Kernel.Core;
using Magic.Kernel.Data;

namespace Magic.Kernel.Interpretation
{
    public abstract class QueryExprNode
    {
    }

    public sealed class QuerySourceExpr : QueryExprNode
    {
        public object Source { get; }

        public QuerySourceExpr(object source)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
        }
    }

    public sealed class QueryCallExpr : QueryExprNode
    {
        public QueryExprNode Target { get; }
        public string MethodName { get; }
        public IReadOnlyList<object?> Args { get; }
        public bool ReturnsQueryExpr { get; }

        public QueryCallExpr(QueryExprNode target, string methodName, IReadOnlyList<object?> args, bool returnsQueryExpr)
        {
            Target = target ?? throw new ArgumentNullException(nameof(target));
            MethodName = methodName ?? throw new ArgumentNullException(nameof(methodName));
            Args = args ?? throw new ArgumentNullException(nameof(args));
            ReturnsQueryExpr = returnsQueryExpr;
        }
    }

    public sealed class QueryExpr : DefType
    {
        public Table SourceTable { get; }
        public QueryExprNode Root { get; }

        public QueryExpr(Table sourceTable, QueryExprNode root)
        {
            SourceTable = sourceTable ?? throw new ArgumentNullException(nameof(sourceTable));
            Root = root ?? throw new ArgumentNullException(nameof(root));
            Name = "query";
        }

        public override Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var (queryArgs, returnsQueryExpr) = SplitQueryControl(args);
            return Task.FromResult<object?>(new QueryExpr(SourceTable, new QueryCallExpr(Root, methodName ?? string.Empty, queryArgs, returnsQueryExpr)));
        }

        public override Task<object?> Await()
            => AwaitObjAsync();

        public override Task<object?> AwaitObjAsync()
            => SourceTable.ExecuteQueryAsync(this);

        public static IReadOnlyList<QueryCallExpr> Decompose(QueryExprNode root)
        {
            if (root == null)
                throw new ArgumentNullException(nameof(root));

            var calls = new List<QueryCallExpr>();
            Unwrap(root, calls);
            return calls;
        }

        private static void Unwrap(QueryExprNode node, List<QueryCallExpr> calls)
        {
            switch (node)
            {
                case QuerySourceExpr:
                    return;

                case QueryCallExpr callExpr:
                    Unwrap(callExpr.Target, calls);
                    calls.Add(callExpr);
                    return;

                default:
                    throw new InvalidOperationException($"Unsupported query node: {node.GetType().Name}.");
            }
        }

        public static (object?[] Args, bool ReturnsQueryExpr) SplitQueryControl(object?[]? args)
        {
            if (args == null || args.Length == 0)
                return (Array.Empty<object?>(), false);

            var last = args[args.Length - 1];
            var hasFlag = last is int or long;
            if (!hasFlag)
                return (args, false);

            var flag = last is int i ? i : (int)(last as long? ?? 0L);
            var effectiveArgs = args.Take(args.Length - 1).ToArray();
            return (effectiveArgs, flag != 0);
        }

        public static bool HasQueryControl(object?[]? args)
        {
            if (args == null || args.Length == 0)
                return false;
            return args[args.Length - 1] is int or long;
        }
    }
}
