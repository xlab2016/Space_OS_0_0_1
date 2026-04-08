using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.Streams.Drivers;

namespace Magic.Kernel.Devices.Streams.Inference
{
    /// <summary>OpenAI-backed inference stream device. Integrates with the OpenAI Chat Completions API (streaming).
    /// Registered in <see cref="Magic.Kernel.Core.OS.Hal.DefGen"/> via <c>stream&lt;inference,openai&gt;</c>.</summary>
    public class OpenAIInference : InferenceDevice
    {
        /// <summary>OpenAI API base URL. Defaults to the standard OpenAI endpoint.</summary>
        public string ApiBase { get; set; } = "https://api.openai.com";

        /// <summary>Model name to use for completions.</summary>
        public string Model { get; set; } = "gpt-4o-mini";

        protected override IStreamDevice CreateDriver(string apiToken, string consolePrefix)
        {
            return new OpenAIDriver(apiToken, ApiBase, Model, consolePrefix);
        }

        protected override async Task<object?> WriteRequestAsync(object? payload)
        {
            var request = BuildRequest(payload);
            // Start streaming in the background via OpenAIDriver.WriteAsync (it owns the queue).
            var driver = new OpenAIDriver(Token, ApiBase, Model, ExecutionCallContext?.GetPrefix() ?? string.Empty);
            await driver.OpenAsync().ConfigureAwait(false);

            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request);
            await driver.WriteAsync(requestBytes).ConfigureAwait(false);

            return new OpenAIStreamingResponse(driver);
        }

        /// <summary>Converts the incoming <paramref name="payload"/> into a typed <see cref="Magic.Drivers.Inference.OpenAI.OpenAIInferenceRequest"/>.
        /// Accepts either an already-typed instance or an untyped dictionary for backwards compatibility.</summary>
        private Magic.Drivers.Inference.OpenAI.OpenAIInferenceRequest BuildRequest(object? payload)
        {
            if (payload is Magic.Drivers.Inference.OpenAI.OpenAIInferenceRequest typed)
                return typed;

            var req = new Magic.Drivers.Inference.OpenAI.OpenAIInferenceRequest
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
