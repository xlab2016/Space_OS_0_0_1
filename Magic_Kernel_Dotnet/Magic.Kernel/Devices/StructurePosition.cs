using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Devices
{
    public class StructurePosition
    {

        public long AbsolutePosition { get; set; }
        public int RelativeIndex { get; set; }
        public string RelativeIndexName { get; set; }

        public List<int> Indices { get; set; }
        public List<string> IndexNames { get; set; }
    }
}
