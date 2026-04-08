using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Magic.Drivers.Inference.OpenAI
{
    /// <summary>Extension helpers for creating and using the standalone <see cref="OpenAIHttpClient"/>.</summary>
    public static class OpenAIInferenceExtensions
    {
        /// <summary>Creates a new <see cref="OpenAIHttpClient"/> configured with the given credentials.</summary>
        public static OpenAIHttpClient CreateClient(string apiToken, string apiBase = "https://api.openai.com", string model = "gpt-4o")
        {
            return new OpenAIHttpClient(apiToken, apiBase, model);
        }

        /// <summary>Sends a streaming request and collects all deltas into a single response string.</summary>
        public static async Task<string> SendAsync(
            string apiToken,
            string apiBase,
            string model,
            object? payload,
            IReadOnlyList<object?> history,
            string? systemPrompt,
            CancellationToken cancellationToken = default)
        {
            var client = new OpenAIHttpClient(apiToken, apiBase, model);
            var result = new System.Text.StringBuilder();

            var request = new OpenAIInferenceRequest
            {
                System = systemPrompt,
                Instruction = payload is string s ? s : null,
                Data = payload is string ? null : payload,
                History = new List<object?>(history)
            };

            var finishedTcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var _ = cancellationToken.Register(() => finishedTcs.TrySetCanceled(cancellationToken));

            await client.SendStreamingAsync(
                    request,
                    delta => result.Append(delta),
                    () => finishedTcs.TrySetResult(null),
                    cancellationToken)
                .ConfigureAwait(false);

            await finishedTcs.Task.ConfigureAwait(false);

            return result.ToString();
        }
    }
}
