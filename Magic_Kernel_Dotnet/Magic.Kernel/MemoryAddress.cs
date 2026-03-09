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

        public override string ToString() =>
            IsGlobal ? $"Mem(Global)" : (Index.HasValue ? $"Mem({Index.Value})" : "Mem(null)");
    }
}
