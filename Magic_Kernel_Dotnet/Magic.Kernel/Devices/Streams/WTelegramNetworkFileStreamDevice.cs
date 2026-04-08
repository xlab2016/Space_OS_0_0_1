using Magic.Kernel.Devices.Streams.Drivers;
using Magic.Kernel.Interpretation;
using Magic.Kernel.Devices;

namespace Magic.Kernel.Devices.Streams
{
    /// <summary>Network file stream for media returned by WTelegram history messages.</summary>
    public sealed class WTelegramNetworkFileStreamDevice : DefStream
    {
        private IStreamDevice? _driver;

        public override async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";
            if (string.Equals(name, "open", StringComparison.OrdinalIgnoreCase))
            {
                var options = args?.Length > 0 ? args[0] : null;
                if (!TryGetMediaHandle(options, out var mediaHandle))
                    return DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "open requires { file } where file comes from WTelegram history.");

                _driver = new WTelegramNetworkFileDriver(mediaHandle);
                return await _driver.OpenAsync().ConfigureAwait(false);
            }

            throw new CallUnknownMethodException(name, this);
        }

        public override Task<DeviceOperationResult> OpenAsync()
            => Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Use open({ file })"));

        private IStreamDevice Driver
            => _driver ?? throw new InvalidOperationException("Device not opened. Call open({ file }) first.");

        public override Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync() => Driver.ReadAsync();
        public override Task<DeviceOperationResult> WriteAsync(byte[] bytes) => Driver.WriteAsync(bytes);
        public override Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl) => Driver.ControlAsync(deviceControl);
        public override Task<DeviceOperationResult> CloseAsync() => Driver.CloseAsync();
        public override Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync() => Driver.ReadChunkAsync();
        public override Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk) => Driver.WriteChunkAsync(chunk);
        public override Task<DeviceOperationResult> MoveAsync(StructurePosition? position) => Driver.MoveAsync(position);
        public override Task<(DeviceOperationResult Result, long Length)> LengthAsync() => Driver.LengthAsync();

        private static bool TryGetMediaHandle(object? options, out WTelegramMediaHandle mediaHandle)
        {
            mediaHandle = null!;
            if (options is not IDictionary<string, object?> dict)
                return false;

            if (dict.TryGetValue("file", out var file) && file is WTelegramMediaHandle handle)
            {
                mediaHandle = handle;
                return true;
            }

            foreach (var pair in dict)
            {
                if (string.Equals(pair.Key, "file", StringComparison.OrdinalIgnoreCase) && pair.Value is WTelegramMediaHandle ciHandle)
                {
                    mediaHandle = ciHandle;
                    return true;
                }
            }

            return false;
        }
    }
}
