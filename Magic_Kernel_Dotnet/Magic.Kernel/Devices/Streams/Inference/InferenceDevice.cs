using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Magic.Kernel.Core;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.Streams;
using Magic.Kernel.Types;

namespace Magic.Kernel.Devices.Streams.Inference
{
    /// <summary>Base class for inference stream devices (LLM / AI completion streams).
    /// Subclasses implement provider-specific open/write/read-delta logic.</summary>
    public abstract class InferenceDevice : DefStream
    {
        /// <summary>API token / bearer credential for the inference backend.</summary>
        public string Token { get; set; } = "";

        /// <summary>Conversation history maintained across turns.</summary>
        public List<object?> History { get; set; } = new List<object?>();

        /// <summary>System prompt / role description sent to the model.</summary>
        public string? SystemPrompt { get; set; }

        protected IStreamDevice? Driver { get; set; }

        private IStreamDevice RequireDriver()
            => Driver ?? throw new InvalidOperationException("InferenceDevice not opened. Call Open() first.");

        /// <summary>Open the inference connection. Accepts a configuration dictionary with <c>token</c> and optional <c>history</c>.</summary>
        public override async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";

            if (string.Equals(name, "open", StringComparison.OrdinalIgnoreCase))
            {
                if (args?.Length > 0 && args[0] is IDictionary<string, object?> config)
                {
                    if (config.TryGetValue("token", out var t) && t is string tokenStr)
                        Token = tokenStr;
                    if (config.TryGetValue("history", out var h))
                    {
                        if (h is Types.DefList defList)
                            History = defList.Items;
                        else if (h is List<object?> list)
                            History = list;
                    }
                    if (config.TryGetValue("system", out var sys) && sys is string sysStr)
                        SystemPrompt = sysStr;
                }
                else if (args?.Length > 0 && args[0] is System.Collections.Generic.IDictionary<string, object> rawConfig)
                {
                    if (rawConfig.TryGetValue("token", out var t) && t is string tokenStr)
                        Token = tokenStr;
                    if (rawConfig.TryGetValue("history", out var h))
                    {
                        if (h is Types.DefList defList)
                            History = defList.Items;
                        else if (h is List<object?> list)
                            History = list;
                    }
                    if (rawConfig.TryGetValue("system", out var sys) && sys is string sysStr)
                        SystemPrompt = sysStr;
                }
                return await OpenAsync().ConfigureAwait(false);
            }

            if (string.Equals(name, "write", StringComparison.OrdinalIgnoreCase))
            {
                // write({data:[...], system:"...", instruction:"..."}) — sends a message and returns a streaming response object
                object? payload = args?.Length > 0 ? args[0] : null;
                return await WriteRequestAsync(payload).ConfigureAwait(false);
            }

            return await base.CallObjAsync(methodName, args).ConfigureAwait(false);
        }

        /// <summary>Sends an inference request and returns a streaming response device.</summary>
        protected abstract Task<object?> WriteRequestAsync(object? payload);

        public override Task<DeviceOperationResult> OpenAsync()
        {
            var prefix = ExecutionCallContext?.GetPrefix() ?? string.Empty;
            Driver = CreateDriver(Token, prefix);
            return Driver.OpenAsync();
        }

        public override Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync()
            => RequireDriver().ReadAsync();

        public override Task<DeviceOperationResult> WriteAsync(byte[] bytes)
            => RequireDriver().WriteAsync(bytes);

        public override Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl)
            => RequireDriver().ControlAsync(deviceControl);

        public override Task<DeviceOperationResult> CloseAsync()
            => RequireDriver().CloseAsync();

        public override Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync()
            => RequireDriver().ReadChunkAsync();

        public override Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk)
            => RequireDriver().WriteChunkAsync(chunk);

        public override Task<DeviceOperationResult> MoveAsync(StructurePosition? position)
            => RequireDriver().MoveAsync(position);

        public override Task<(DeviceOperationResult Result, long Length)> LengthAsync()
            => RequireDriver().LengthAsync();

        /// <summary>Factory method: creates the underlying provider driver using the given API token.</summary>
        protected abstract IStreamDevice CreateDriver(string apiToken, string consolePrefix);
    }
}
