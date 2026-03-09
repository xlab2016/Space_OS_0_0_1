using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Magic.Kernel.Core;
using Magic.Kernel.Devices;

namespace Magic.Kernel.Devices.SSC
{
    public interface ISSCompiler
    {
        public Task<SSCResult> CompileAsync(IStreamDevice device, ISpaceDisk disk);
    }
}
