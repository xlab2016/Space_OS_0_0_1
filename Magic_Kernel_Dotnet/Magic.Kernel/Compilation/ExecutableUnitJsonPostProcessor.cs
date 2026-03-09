using Magic.Kernel.Processor;
using System.Collections.Generic;
using System.Text.Json;

namespace Magic.Kernel.Compilation
{
    internal static class ExecutableUnitJsonPostProcessor
    {
        public static void NormalizeObjectGraphs(ExecutableUnit unit)
        {
            // Normalize CallInfo.Parameters in EntryPoint
            NormalizeExecutionBlock(unit.EntryPoint);

            foreach (var p in unit.Procedures.Values)
            {
                NormalizeExecutionBlock(p.Body);
            }

            foreach (var f in unit.Functions.Values)
            {
                NormalizeExecutionBlock(f.Body);
            }

            foreach (var e in unit.Exports)
            {
                NormalizeExecutionBlock(e.Source);
            }
        }

        private static void NormalizeExecutionBlock(ExecutionBlock block)
        {
            foreach (var cmd in block)
            {
                if (cmd.Operand1 is CallInfo ci)
                {
                    ci.Parameters = NormalizeDictionary(ci.Parameters);
                }

                if (cmd.Operand2 is CallInfo ci2)
                {
                    ci2.Parameters = NormalizeDictionary(ci2.Parameters);
                }
            }
        }

        private static Dictionary<string, object> NormalizeDictionary(Dictionary<string, object> dict)
        {
            var result = new Dictionary<string, object>(dict.Count);
            foreach (var kv in dict)
            {
                result[kv.Key] = NormalizeValue(kv.Value)!;
            }
            return result;
        }

        private static object? NormalizeValue(object? value)
        {
            if (value is JsonElement je)
            {
                return ConvertJsonElement(je);
            }

            if (value is Dictionary<string, object> d)
            {
                return NormalizeDictionary(d);
            }

            if (value is List<object> list)
            {
                var outList = new List<object>(list.Count);
                foreach (var item in list)
                {
                    outList.Add(NormalizeValue(item)!);
                }
                return outList;
            }

            return value;
        }

        private static object? ConvertJsonElement(JsonElement je)
        {
            switch (je.ValueKind)
            {
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.String:
                    return je.GetString() ?? string.Empty;
                case JsonValueKind.Number:
                    if (je.TryGetInt64(out var l)) return l;
                    if (je.TryGetDouble(out var d)) return d;
                    return je.GetRawText();
                case JsonValueKind.Object:
                {
                    var dict = new Dictionary<string, object>();
                    foreach (var prop in je.EnumerateObject())
                    {
                        dict[prop.Name] = ConvertJsonElement(prop.Value)!;
                    }
                    return dict;
                }
                case JsonValueKind.Array:
                {
                    var list = new List<object>();
                    foreach (var item in je.EnumerateArray())
                    {
                        list.Add(ConvertJsonElement(item)!);
                    }
                    return list;
                }
                default:
                    return je.GetRawText();
            }
        }
    }
}

