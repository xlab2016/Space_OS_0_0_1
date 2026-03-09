using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.Streams.Drivers;
using Magic.Kernel.Interpretation;
using Telegram.Bot.Types;

namespace Magic.Kernel.Devices.Streams
{
    /// <summary>Stream device for Telegram file over network: open({ token, file }) with file = PhotoSize[] or Document; uses TelegramNetworkFileDriver.</summary>
    public class TelegramNetworkFileStreamDevice : DefStream
    {
        private IStreamDevice? _driver;

        public override async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";
            if (string.Equals(name, "open", StringComparison.OrdinalIgnoreCase))
            {
                var options = args?.Length > 0 ? args[0] : null;
                if (!TryGetTokenAndFileId(options, out var token, out var fileId))
                    return DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "open requires { token, file } with file as PhotoSize[] or Document");
                _driver = new TelegramNetworkFileDriver(token, fileId);
                return await _driver.OpenAsync().ConfigureAwait(false);
            }
            throw new CallUnknownMethodException(name, this);
        }

        private static bool TryGetTokenAndFileId(object? options, out string token, out string fileId)
        {
            token = "";
            fileId = "";
            if (options == null) return false;

            if (options is not IDictionary<string, object?> dict)
                return false;
            token = (dict.TryGetValue("token", out var t) && t is string ts) ? ts : "";
            var fileObj = dict.TryGetValue("file", out var f) ? f : null;
            if (fileObj is PhotoSize[] arr && arr.Length > 0)
                fileId = arr[^1].FileId ?? "";
            else if (fileObj is Document d)
                fileId = d.FileId ?? "";
            else if (fileObj is IDictionary<string, object?> fileDict && fileDict.TryGetValue("file_id", out var fid))
                fileId = fid?.ToString() ?? "";
            else if (fileObj is System.Collections.IList list && list.Count > 0)
            {
                var last = list[list.Count - 1];
                if (last is IDictionary<string, object?> lastDict && lastDict.TryGetValue("file_id", out var lid))
                    fileId = lid?.ToString() ?? "";
                else if (last is PhotoSize ps)
                    fileId = ps.FileId ?? "";
            }
            return !string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(fileId);
        }

        public override Task<DeviceOperationResult> OpenAsync() =>
            Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Use open({ token, file })"));

        private IStreamDevice Driver => _driver ?? throw new InvalidOperationException("Device not opened. Call open({ token, file }) first.");

        public override Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync() => Driver.ReadAsync();
        public override Task<DeviceOperationResult> WriteAsync(byte[] bytes) => Driver.WriteAsync(bytes);
        public override Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl) => Driver.ControlAsync(deviceControl);
        public override Task<DeviceOperationResult> CloseAsync() => Driver.CloseAsync();
        public override Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync() => Driver.ReadChunkAsync();
        public override Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk) => Driver.WriteChunkAsync(chunk);
        public override Task<DeviceOperationResult> MoveAsync(StructurePosition? position) => Driver.MoveAsync(position);
        public override Task<(DeviceOperationResult Result, long Length)> LengthAsync() => Driver.LengthAsync();
    }
}
