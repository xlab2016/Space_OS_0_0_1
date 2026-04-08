using Magic.Kernel.Core;
using Magic.Kernel.Devices.Store;
using Magic.Kernel.Interpretation;
using System;
using System.Collections.Generic;
using System.Collections;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Data
{
    public class Table : DefType
    {
        public const string PendingWriteModeKey = "__magic_write_mode";
        public const string PendingWriteModeUpsert = "upsert";

        public Database? Database { get; set; }
        public List<Column> Columns { get; set; } = new List<Column>();
        public List<TableRelation> Relations { get; set; } = new List<TableRelation>();
        public List<Dictionary<string, object?>> PendingRows { get; set; } = new List<Dictionary<string, object?>>();
        public ExprTree? FilterExpr { get; set; }

        public override async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";
            var (queryArgs, returnsQueryExpr) = QueryExpr.SplitQueryControl(args);
            if (!(ExecutionCallContext?.IsExecutingQueryExpr ?? false) &&
                (string.Equals(name, "where", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "max", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "find", StringComparison.OrdinalIgnoreCase))
               )
            {
                return new QueryExpr(this, new QueryCallExpr(new QuerySourceExpr(this), name, queryArgs, returnsQueryExpr));
            }

            if (string.Equals(name, "add", StringComparison.OrdinalIgnoreCase))
            {
                if (queryArgs == null || queryArgs.Length == 0)
                    throw new ArgumentException("Table.add requires one argument (row object).");
                PendingRows.Add(ToDictionary(queryArgs[0]));
                return this;
            }

            if (string.Equals(name, "mul", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "upsert", StringComparison.OrdinalIgnoreCase))
            {
                if (queryArgs == null || queryArgs.Length == 0)
                    throw new ArgumentException("Table.mul requires one argument (row object).");
                var row = ToDictionary(queryArgs[0]);
                row[PendingWriteModeKey] = PendingWriteModeUpsert;
                PendingRows.Add(row);
                return this;
            }

            if (string.Equals(name, "where", StringComparison.OrdinalIgnoreCase))
            {
                if (queryArgs == null || queryArgs.Length == 0 || queryArgs[0] is not LambdaValue lambda)
                    throw new ArgumentException("Table.where requires one lambda argument.");

                var runtimeDb = (Database?.Device as Devices.Store.DatabaseDevice) ?? ExecutionCallContext?.CurrentDatabase;
                var postgres = runtimeDb?.Generalizations?.OfType<Postgres>().FirstOrDefault();
                if (postgres != null && lambda.ExprTree != null)
                {
                    return CloneForQuery(filteredRows: null, filterExpr: CombineFilterExpr(FilterExpr, lambda.ExprTree));
                }

                var interpreter = ExecutionCallContext?.Interpreter;
                if (interpreter == null)
                    throw new InvalidOperationException("Table.where(predicate) requires interpreter context when not using Db.");

                var filteredRows = new List<Dictionary<string, object?>>();
                foreach (var row in PendingRows)
                {
                    var passes = await interpreter.InvokeLambdaAsync(lambda, new object?[] { row }).ConfigureAwait(false);
                    if (passes is true or 1 or 1L)
                        filteredRows.Add(new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase));
                }

                return CloneForQuery(filteredRows, null);
            }

            if (string.Equals(name, "any", StringComparison.OrdinalIgnoreCase))
            {
                if (queryArgs == null || queryArgs.Length == 0 || queryArgs[0] is not LambdaValue lambda)
                    return false;
                // If this table is attached to a schema Database with runtime Device and lambda has ExprTree: run predicate as SQL (any = at least one row)
                var runtimeDb = (Database?.Device as Devices.Store.DatabaseDevice) ?? ExecutionCallContext?.CurrentDatabase;
                if (runtimeDb != null && lambda.ExprTree != null)
                {
                    var postgres = runtimeDb.Generalizations?.OfType<Postgres>().FirstOrDefault();
                    if (postgres != null)
                    {
                        var count = await postgres.AnyAsync(this, CombineFilterExpr(FilterExpr, lambda.ExprTree)!).ConfigureAwait(false);
                        return count != 0;
                    }
                }
                // In-memory: iterate PendingRows and invoke lambda per row
                var interpreter = ExecutionCallContext?.Interpreter;
                if (interpreter == null)
                    throw new InvalidOperationException("Table.any(predicate) requires interpreter context when not using Db.");
                var rows = PendingRows;
                foreach (var row in rows)
                {
                    var result = await interpreter.InvokeLambdaAsync(lambda, new object?[] { row }).ConfigureAwait(false);
                    if (result is true or 1 or 1L)
                        return true;
                }
                return false;
            }

            if (string.Equals(name, "find", StringComparison.OrdinalIgnoreCase))
            {
                if (queryArgs == null || queryArgs.Length == 0 || queryArgs[0] is not LambdaValue lambda)
                    return null;

                var interpreter = ExecutionCallContext?.Interpreter;
                if (interpreter == null)
                    throw new InvalidOperationException("Table.find(predicate) requires interpreter context when not using Db.");

                var rows = PendingRows;
                foreach (var row in rows)
                {
                    var result = await interpreter.InvokeLambdaAsync(lambda, new object?[] { row }).ConfigureAwait(false);
                    if (result is true or 1 or 1L)
                        return CloneRow(row);
                }

                return null;
            }

            if (string.Equals(name, "max", StringComparison.OrdinalIgnoreCase))
            {
                if (queryArgs == null || queryArgs.Length == 0 || queryArgs[0] is not LambdaValue selectorLambda)
                    throw new ArgumentException("Table.max requires one lambda argument.");

                var runtimeDb = (Database?.Device as Devices.Store.DatabaseDevice) ?? ExecutionCallContext?.CurrentDatabase;
                if (runtimeDb != null && selectorLambda.ExprTree != null)
                {
                    var postgres = runtimeDb.Generalizations?.OfType<Postgres>().FirstOrDefault();
                    if (postgres != null && TryGetSelectorMemberName(selectorLambda.ExprTree, out var columnName))
                    {
                        return await postgres.MaxAsync(this, FilterExpr, columnName).ConfigureAwait(false);
                    }
                }

                var interpreter = ExecutionCallContext?.Interpreter;
                if (interpreter == null)
                    throw new InvalidOperationException("Table.max(selector) requires interpreter context when not using Db.");

                long max = 0;
                var hasValue = false;
                foreach (var row in PendingRows)
                {
                    var value = await interpreter.InvokeLambdaAsync(selectorLambda, new object?[] { row }).ConfigureAwait(false);
                    if (!TryConvertToLong(value, out var longValue))
                        continue;

                    if (!hasValue || longValue > max)
                    {
                        max = longValue;
                        hasValue = true;
                    }
                }

                return hasValue ? max : 0L;
            }

            if (string.Equals(name, "maxWhere", StringComparison.OrdinalIgnoreCase))
            {
                if (queryArgs == null || queryArgs.Length < 2 || queryArgs[0] is not LambdaValue whereLambda || queryArgs[1] is not string columnName)
                    throw new ArgumentException("Table.maxWhere requires (whereLambda, columnName:string) arguments.");

                var runtimeDb = (Database?.Device as Devices.Store.DatabaseDevice) ?? ExecutionCallContext?.CurrentDatabase;
                if (runtimeDb != null && whereLambda.ExprTree != null)
                {
                    var postgres = runtimeDb.Generalizations?.OfType<Postgres>().FirstOrDefault();
                    if (postgres != null)
                    {
                        var maxValue = await postgres.MaxAsync(this, whereLambda.ExprTree, columnName).ConfigureAwait(false);
                        return maxValue;
                    }
                }

                // In-memory fallback over PendingRows using interpreter.
                var interpreter = ExecutionCallContext?.Interpreter;
                if (interpreter == null)
                    throw new InvalidOperationException("Table.maxWhere(predicate, selector) requires interpreter context when not using Db.");

                long max = 0;
                var hasValue = false;
                foreach (var row in PendingRows)
                {
                    var passes = await interpreter.InvokeLambdaAsync(whereLambda, new object?[] { row }).ConfigureAwait(false);
                    if (passes is not (true or 1 or 1L))
                        continue;

                    // Selector: same column name on the row.
                    if (!row.TryGetValue(columnName, out var value) || value == null)
                        continue;

                    if (value is long lv)
                    {
                        if (!hasValue || lv > max)
                        {
                            max = lv;
                            hasValue = true;
                        }
                    }
                    else if (value is int i)
                    {
                        var lv2 = (long)i;
                        if (!hasValue || lv2 > max)
                        {
                            max = lv2;
                            hasValue = true;
                        }
                    }
                }

                return hasValue ? max : 0L;
            }

            throw new CallUnknownMethodException(name, this);
        }

        public List<Dictionary<string, object?>> ConsumePendingRows()
        {
            if (PendingRows.Count == 0)
                return new List<Dictionary<string, object?>>();
            var snapshot = PendingRows.ToList();
            PendingRows.Clear();
            return snapshot;
        }

        internal async Task<object?> ExecuteQueryAsync(QueryExpr query)
        {
            if (query == null)
                throw new ArgumentNullException(nameof(query));

            var sourceTable = query.SourceTable;
            var calls = QueryExpr.Decompose(query.Root);

            var runtimeDb = (sourceTable.Database?.Device as Devices.Store.DatabaseDevice) ?? sourceTable.ExecutionCallContext?.CurrentDatabase;
            var postgres = runtimeDb?.Generalizations?.OfType<Postgres>().FirstOrDefault();
            if (postgres != null && CanExecuteWithDriver(calls))
                return await postgres.ExecuteQueryAsync(sourceTable, query).ConfigureAwait(false);

            var previous = sourceTable.ExecutionCallContext;
            if (previous != null)
            {
                sourceTable.ExecutionCallContext = new Core.ExecutionCallContext
                {
                    Unit = previous.Unit,
                    Interpreter = previous.Interpreter,
                    CurrentDatabase = previous.CurrentDatabase,
                    CurrentSocket = previous.CurrentSocket,
                    IsExecutingQueryExpr = true
                };
            }
            try
            {
                object? current = sourceTable;
                foreach (var call in calls)
                {
                    if (current is not IDefType defType)
                        throw new InvalidOperationException($"Query target for '{call.MethodName}' is not awaitable/callable.");
                    current = await defType.CallObjAsync(call.MethodName, call.Args.ToArray()).ConfigureAwait(false);
                }
                return current;
            }
            finally { sourceTable.ExecutionCallContext = previous; }
        }

        private static Dictionary<string, object?> ToDictionary(object? row)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            if (row == null)
                return result;

            if (row is IDictionary<string, object?> typed)
            {
                foreach (var kv in typed)
                    result[kv.Key] = kv.Value;
                return result;
            }

            if (row is IDictionary raw)
            {
                foreach (DictionaryEntry kv in raw)
                {
                    var key = kv.Key?.ToString();
                    if (!string.IsNullOrWhiteSpace(key))
                        result[key] = kv.Value;
                }
                return result;
            }

            var type = row.GetType();
            foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            {
                if (!prop.CanRead)
                    continue;
                result[prop.Name] = prop.GetValue(row);
            }
            return result;
        }

        internal Table CloneForQuery(List<Dictionary<string, object?>>? filteredRows, ExprTree? filterExpr)
        {
            return new Table
            {
                Index = Index,
                Name = Name,
                Position = Position,
                Generalizations = new List<IDefType>(Generalizations),
                Database = Database,
                Columns = Columns.ToList(),
                Relations = Relations.ToList(),
                PendingRows = filteredRows ?? PendingRows,
                FilterExpr = filterExpr
            };
        }

        private static bool CanExecuteWithDriver(IReadOnlyList<QueryCallExpr> calls)
        {
            if (calls == null || calls.Count == 0)
                return false;

            for (var i = 0; i < calls.Count; i++)
            {
                var call = calls[i];
                var name = call.MethodName?.Trim() ?? string.Empty;
                var isLast = i == calls.Count - 1;

                if (string.Equals(name, "where", StringComparison.OrdinalIgnoreCase))
                {
                    if (call.Args.Count == 0 || call.Args[0] is not LambdaValue whereLambda || whereLambda.ExprTree == null)
                        return false;
                    continue;
                }

                if (string.Equals(name, "max", StringComparison.OrdinalIgnoreCase))
                {
                    if (!isLast || call.Args.Count == 0 || call.Args[0] is not LambdaValue maxLambda || !TryGetSelectorMemberName(maxLambda.ExprTree!, out _))
                        return false;
                    continue;
                }

                if (string.Equals(name, "any", StringComparison.OrdinalIgnoreCase))
                {
                    if (!isLast || call.Args.Count == 0 || call.Args[0] is not LambdaValue anyLambda || anyLambda.ExprTree == null)
                        return false;
                    continue;
                }

                if (string.Equals(name, "find", StringComparison.OrdinalIgnoreCase))
                {
                    if (!isLast || call.Args.Count == 0 || call.Args[0] is not LambdaValue findLambda || findLambda.ExprTree == null)
                        return false;
                    continue;
                }

                return false;
            }

            return true;
        }

        private static ExprTree? CombineFilterExpr(ExprTree? existing, ExprTree? next)
        {
            if (existing == null)
                return next;
            if (next == null)
                return existing;
            return new ExprAnd(UnwrapLambda(existing), UnwrapLambda(next));
        }

        private static ExprTree UnwrapLambda(ExprTree expr)
            => expr is ExprLambda lambda ? lambda.Body : expr;

        private static bool TryGetSelectorMemberName(ExprTree exprTree, out string memberName)
        {
            memberName = string.Empty;
            var node = UnwrapLambda(exprTree);
            if (node is not ExprMemberAccess access)
                return false;
            if (access.Target is not ExprParameter)
                return false;
            memberName = access.MemberName ?? string.Empty;
            return !string.IsNullOrWhiteSpace(memberName);
        }

        private static bool TryConvertToLong(object? value, out long result)
        {
            result = 0L;
            switch (value)
            {
                case long l:
                    result = l;
                    return true;
                case int i:
                    result = i;
                    return true;
                case short s:
                    result = s;
                    return true;
                case byte b:
                    result = b;
                    return true;
                case string text when long.TryParse(text, out var parsed):
                    result = parsed;
                    return true;
                default:
                    return false;
            }
        }

        private static Dictionary<string, object?> CloneRow(IDictionary<string, object?> row)
        {
            var clone = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in row)
            {
                if (string.Equals(kv.Key, PendingWriteModeKey, StringComparison.OrdinalIgnoreCase))
                    continue;
                clone[kv.Key] = kv.Value;
            }
            return clone;
        }
    }
}
