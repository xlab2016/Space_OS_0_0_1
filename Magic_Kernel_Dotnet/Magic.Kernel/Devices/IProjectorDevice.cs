using Magic.Kernel.Space;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Devices
{
    public interface IProjectorDevice : IDevice
    {
        public Task<DeviceOperationResult> ProjectAsync(Vertex vertex, out Position result);
        public Task<DeviceOperationResult> ProjectAsync(Shape shape, out List<Position> result);
    }
}
