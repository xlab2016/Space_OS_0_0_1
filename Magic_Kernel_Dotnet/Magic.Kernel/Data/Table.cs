using Magic.Kernel.Core;
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
        public Database? Database { get; set; }
        public List<Column> Columns { get; set; } = new List<Column>();
        public List<Dictionary<string, object?>> PendingRows { get; set; } = new List<Dictionary<string, object?>>();

        public override Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";
            if (string.Equals(name, "add", StringComparison.OrdinalIgnoreCase))
            {
                if (args == null || args.Length == 0)
                    throw new ArgumentException("Table.add requires one argument (row object).");
                PendingRows.Add(ToDictionary(args[0]));
                return Task.FromResult<object?>(this);
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
    }
}
