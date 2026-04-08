using System;
using System.Collections.Generic;
using System.Linq;

namespace Magic.Kernel
{
    /// <summary>
    /// Minimal runtime object representation for AGI types (e.g. Point).
    /// Backed by a case-insensitive field dictionary.
    /// </summary>
    public sealed class ObjectInstance
    {
        public string TypeName { get; }

        public Dictionary<string, object?> Fields { get; } =
            new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        public ObjectInstance(string typeName)
        {
            TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));
        }

        public override string ToString()
        {
            var fields = string.Join(", ", Fields.Select(kv => kv.Key + "=" + kv.Value));
            return $"{TypeName}{{{fields}}}";
        }
    }
}

