using Magic.Kernel.Space;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Devices
{
    public interface IInferenceDevice : IIODevice
    {
        public Task<DeviceOperationResult> Print(EntityData input, EntityData output);
    }
}
