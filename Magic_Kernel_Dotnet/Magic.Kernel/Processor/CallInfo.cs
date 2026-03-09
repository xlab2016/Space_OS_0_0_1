using System.Collections.Generic;
using System.Linq;

namespace Magic.Kernel.Processor
{
    public class CallInfo
    {
        public string FunctionName { get; set; } = string.Empty;
        public Dictionary<string, object> Parameters { get; set; } = new Dictionary<string, object>();

        public override string ToString()
        {
            if (Parameters.Count == 0)
                return $"CallInfo({FunctionName})";
            var args = string.Join(", ", Parameters.Select(p => $"{p.Key}={FormatParam(p.Value)}"));
            return $"CallInfo({FunctionName}, {args})";
        }

        static string FormatParam(object? value)
        {
            if (value == null) return "null";
            var s = value.ToString();
            if (s != null && s.Length > 40) return s.Substring(0, 37) + "...";
            return s ?? "null";
        }
    }
}
