using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Space
{
    public class EntityData
    {
        public HierarchicalDataType Type { get; set; }
        // Base64 encoded data
        public string? Data { get; set; }
    }
}
