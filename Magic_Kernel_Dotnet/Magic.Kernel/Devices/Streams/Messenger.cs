using Magic.Kernel.Devices.Streams.Drivers;
using Magic.Kernel.Interpretation;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Magic.Kernel.Devices.Streams
{
    public class Messenger : DefStream
    {
        protected IStreamDevice? _driver;

        public string BotToken { get; set; } = "";
        public long DefaultChatId { get; set; }

        public override async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";
            if (string.Equals(name, "open", StringComparison.OrdinalIgnoreCase))
            {
                if (args?.Length > 0 && args[0] is Dictionary<string, object> arg0)
                    BotToken = arg0.TryGetValue("token", out var tok) ? (tok?.ToString() ?? "") : "";
                else
                    BotToken = (Position?.IndexNames?.Count > 0 ? Position.IndexNames[0] : null) ?? (args?.Length > 0 ? (args[0]?.ToString() ?? "") : "");
                DefaultChatId = Position?.AbsolutePosition ?? 0;
                if (DefaultChatId == 0 && args != null && args.Length > 1)
                {
                    if (args[1] is long l) DefaultChatId = l;
                    else if (args[1] is int i) DefaultChatId = i;
                    else if (args[1] != null && long.TryParse(args[1].ToString(), out var parsed)) DefaultChatId = parsed;
                }
                return await OpenAsync().ConfigureAwait(false);
            }
            throw new CallUnknownMethodException(name, this);
        }

        public override async Task<DeviceOperationResult> OpenAsync()
        {
            //_driver = new TelegramDriver(BotToken, DefaultChatId);
            return await _driver.OpenAsync().ConfigureAwait(false);
        }

        private IStreamDevice Driver => _driver ?? throw new InvalidOperationException("Device not opened. Call OpenAsync first.");

        public override Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync() => Driver.ReadAsync();
        public override Task<DeviceOperationResult> WriteAsync(byte[] bytes) => Driver.WriteAsync(bytes);
        public override Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl) => Driver.ControlAsync(deviceControl);
        public override Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync() => Driver.ReadChunkAsync();
        public override Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk) => Driver.WriteChunkAsync(chunk);
        public override Task<DeviceOperationResult> MoveAsync(StructurePosition? position) => Driver.MoveAsync(position);
        public override Task<(DeviceOperationResult Result, long Length)> LengthAsync() => Driver.LengthAsync();
        public override Task<DeviceOperationResult> CloseAsync() => Driver.CloseAsync();
    }
}
