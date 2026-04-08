using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel
{
    public class MemoryAddress
    {
        public long? Index { get; set; }
        public bool IsGlobal { get; set; }

        /// <summary>
        /// Optional logical (per-function) index used for AGIASM/debug formatting.
        /// Physical storage index remains in <see cref="Index"/>.
        /// </summary>
        public long? LogicalIndex { get; set; }

        public override string ToString()
        {
            if (IsGlobal)
                return "Mem(Global)";

            if (LogicalIndex.HasValue)
                return $"Mem({LogicalIndex.Value})";

            return Index.HasValue ? $"Mem({Index.Value})" : "Mem(null)";
        }
    }
}
