using System;
using System.Threading.Tasks;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.Streams.Drivers;

namespace Magic.Kernel.Devices.Streams.Inference
{
    /// <summary>
    /// Streaming proxy returned by <see cref="DeepSeekInference"/>.
    /// </summary>
    public class DeepSeekStreamingResponse : DefStream
    {
        private readonly DeepSeekDriver _driver;

        public DeepSeekStreamingResponse(DeepSeekDriver driver)
        {
            _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        }

        public override Task<DeviceOperationResult> OpenAsync() => Task.FromResult(DeviceOperationResult.Success);
        public override Task<DeviceOperationResult> CloseAsync() => _driver.CloseAsync();

        public override Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync() => _driver.ReadAsync();
        public override Task<DeviceOperationResult> WriteAsync(byte[] bytes) => _driver.WriteAsync(bytes);
        public override Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl) => _driver.ControlAsync(deviceControl);

        public override Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync() => _driver.ReadChunkAsync();
        public override Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk) => _driver.WriteChunkAsync(chunk);

        public override Task<DeviceOperationResult> MoveAsync(StructurePosition? position) => _driver.MoveAsync(position);
        public override Task<(DeviceOperationResult Result, long Length)> LengthAsync() => _driver.LengthAsync();
    }
}

