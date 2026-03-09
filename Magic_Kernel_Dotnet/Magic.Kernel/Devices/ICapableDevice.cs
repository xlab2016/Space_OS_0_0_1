using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Devices
{
    public interface ICapableDevice
    {
        public Task<bool> IsCapableAsync(IDevice other);
    }
}
