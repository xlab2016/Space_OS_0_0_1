using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Devices
{
    public interface IDevice
    {
        public long? Index { get; set; }
        public string Name { get; set; }
    }
}
