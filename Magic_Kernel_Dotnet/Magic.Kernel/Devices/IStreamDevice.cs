using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Devices
{
    public interface IStreamDevice : IIODevice
    {
        Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync();
        Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk);
        Task<DeviceOperationResult> MoveAsync(StructurePosition? position);
        Task<(DeviceOperationResult Result, long Length)> LengthAsync();
    }
}
