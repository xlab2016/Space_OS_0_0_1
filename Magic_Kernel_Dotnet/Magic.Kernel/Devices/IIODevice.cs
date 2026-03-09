using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Devices
{
    public interface IIODevice
    {
        Task<DeviceOperationResult> OpenAsync();
        Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync();
        Task<DeviceOperationResult> WriteAsync(byte[] bytes);
        Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl);
        Task<DeviceOperationResult> CloseAsync();
    }
}
