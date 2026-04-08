using System;
using System.Collections.Generic;
using System.Linq;

namespace Magic.Kernel.Core
{
    public sealed class DefClass : DefType
    {
        /// <summary>OOP base type names from <c>Child: Base1, Base2 { }</c> / bytecode <c>def</c>. Not the same as <see cref="DefType.Generalizations"/> (runtime “meanings”, devices, drivers).</summary>
        public List<string> Inheritances { get; set; } = new();

        public DefClass()
        {
        }

        public DefClass(string name, IEnumerable<string>? baseTypeNames = null)
        {
            Name = name ?? string.Empty;
            if (baseTypeNames == null)
                return;

            foreach (var baseName in baseTypeNames.Where(n => !string.IsNullOrWhiteSpace(n)))
            {
                var trimmed = baseName.Trim();
                if (Inheritances.Any(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase)))
                    continue;
                Inheritances.Add(trimmed);
            }
        }

        public override string ToString()
        {
            var baseStr = base.ToString();
            if (Inheritances.Count == 0)
                return baseStr;
            var idx = baseStr.LastIndexOf(')');
            if (idx < 0)
                return $"{baseStr} : {string.Join(", ", Inheritances)}";
            var head = baseStr[..idx].TrimEnd();
            var inherits = string.Join(", ", Inheritances);
            return $"{head} : {inherits})";
        }
    }
}
