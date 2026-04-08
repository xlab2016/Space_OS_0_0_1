using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.Streams.Drivers;

namespace Magic.Kernel.Devices.Streams.Inference
{
    /// <summary>DeepSeek-backed inference stream device (OpenAI-compatible streaming).</summary>
    public class DeepSeekInference : InferenceDevice
    {
        public string ApiBase { get; set; } = "https://api.deepseek.com";
        public string Model { get; set; } = "deepseek-chat";

        protected override IStreamDevice CreateDriver(string apiToken, string consolePrefix)
            => new DeepSeekDriver(apiToken, ApiBase, Model, consolePrefix);

        protected override async Task<object?> WriteRequestAsync(object? payload)
        {
            var request = BuildRequest(payload);

            // Start streaming in the background via DeepSeekDriver.WriteAsync (driver owns the queue).
            var driver = new DeepSeekDriver(Token, ApiBase, Model, ExecutionCallContext?.GetPrefix() ?? string.Empty);
            await driver.OpenAsync().ConfigureAwait(false);

            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request);
            await driver.WriteAsync(requestBytes).ConfigureAwait(false);

            return new DeepSeekStreamingResponse(driver);
        }

        private Magic.Drivers.Inference.DeepSeek.DeepSeekInferenceRequest BuildRequest(object? payload)
        {
            if (payload is Magic.Drivers.Inference.DeepSeek.DeepSeekInferenceRequest typed)
                return typed;

            var req = new Magic.Drivers.Inference.DeepSeek.DeepSeekInferenceRequest
            {
                History = History,
                System = SystemPrompt,
            };

            if (payload is IDictionary<string, object?> dict)
            {
                dict.TryGetValue("data", out var data);
                dict.TryGetValue("system", out var sys);
                dict.TryGetValue("instruction", out var instr);
                dict.TryGetValue("tools", out var tools);
                dict.TryGetValue("skills", out var skills);

                req.Data = data;
                if (sys is string sysStr && !string.IsNullOrEmpty(sysStr))
                    req.System = sysStr;

                req.Instruction = instr?.ToString();
                req.Tools = tools;
                req.Skills = skills;
            }
            else if (payload is string payloadStr)
            {
                req.Instruction = payloadStr;
            }

            return req;
        }
    }
}

