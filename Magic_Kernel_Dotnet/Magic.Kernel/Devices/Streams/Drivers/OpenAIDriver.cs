using System;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.Streams.Inference;

namespace Magic.Kernel.Devices.Streams.Drivers
{
    /// <summary>
    /// OpenAI inference stream driver with internal delta queue.
    /// Queueing + HTTP streaming is triggered on <see cref="WriteAsync(byte[])"/>.
    /// </summary>
    public class OpenAIDriver : IStreamDevice
    {
        private readonly string _apiToken;
        private readonly string _apiBase;
        private readonly string _model;

        private bool _opened;
        private readonly ConcurrentQueue<string> _deltaQueue = new();
        private volatile bool _streamFinished;
        private long _chunkIndex;
        private int _streamWaitPollIntervalMs = 10;
        private CancellationTokenSource? _cts;
        private readonly string _consolePrefix;

        public OpenAIDriver(string apiToken, string apiBase = "https://api.openai.com", string model = "gpt-4o-mini", string consolePrefix = "")
        {
            _apiToken = apiToken ?? throw new ArgumentNullException(nameof(apiToken));
            _apiBase = apiBase.TrimEnd('/');
            _model = model;
            _consolePrefix = consolePrefix ?? string.Empty;
        }

        public Task<DeviceOperationResult> OpenAsync()
        {
            _opened = true;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            return Task.FromResult(DeviceOperationResult.Success);
        }

        public Task<DeviceOperationResult> CloseAsync()
        {
            _opened = false;
            try { _cts?.Cancel(); } catch { }
            _cts?.Dispose();
            _cts = null;

            // best-effort clear
            while (_deltaQueue.TryDequeue(out _)) { }
            _streamFinished = false;
            _chunkIndex = 0;

            return Task.FromResult(DeviceOperationResult.Success);
        }

        /// <summary>
        /// Accepts JSON-serialized <see cref="Magic.Drivers.Inference.OpenAI.OpenAIInferenceRequest"/> bytes.
        /// Starts HTTP streaming and enqueues text deltas into an internal queue.
        /// </summary>
        public async Task<DeviceOperationResult> WriteAsync(byte[] bytes)
        {
            if (!_opened)
                return DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Driver not opened");

            if (bytes == null || bytes.Length == 0)
                return DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Empty write payload");

            // Reset queue state for the next request.
            while (_deltaQueue.TryDequeue(out _)) { }
            _streamFinished = false;
            _chunkIndex = 0;

            Magic.Drivers.Inference.OpenAI.OpenAIInferenceRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<Magic.Drivers.Inference.OpenAI.OpenAIInferenceRequest>(bytes);
            }
            catch (Exception ex)
            {
                return DeviceOperationResult.Fail(DeviceOperationState.Failed, $"Failed to parse OpenAIInferenceRequest: {ex.Message}");
            }

            if (request == null)
                return DeviceOperationResult.Fail(DeviceOperationState.Failed, "OpenAIInferenceRequest is null");

            var client = new Magic.Drivers.Inference.OpenAI.OpenAIHttpClient(
                _apiToken,
                _apiBase,
                _model,
                consolePrefix: _consolePrefix,
                logAction: Console.WriteLine);

            await client.SendStreamingAsync(
                    request,
                    delta => _deltaQueue.Enqueue(delta),
                    () => _streamFinished = true,
                    _cts?.Token ?? CancellationToken.None)
                .ConfigureAwait(false);

            return DeviceOperationResult.Success;
        }

        /// <summary>
        /// Waits for the next queued delta and returns it as a JSON StreamChunk: {"text":"..."}.
        /// When the stream is finished and queue is empty, returns a success with null chunk (stream end).
        /// </summary>
        public async Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync()
        {
            if (!_opened)
                return (DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Driver not opened"), null);

            while (true)
            {
                if (_deltaQueue.TryDequeue(out var delta))
                {
                    var json = JsonSerializer.Serialize(new { text = delta });
                    var dataBytes = Encoding.UTF8.GetBytes(json);

                    var chunk = new StreamChunk
                    {
                        ChunkSize = dataBytes.Length,
                        Data = dataBytes,
                        DataFormat = DataFormat.Json,
                        ApplicationFormat = ApplicationFormat.Unknown,
                        Position = new StructurePosition { RelativeIndex = (int)_chunkIndex++, RelativeIndexName = "" }
                    };
                    return (DeviceOperationResult.Success, chunk);
                }

                if (_streamFinished)
                    return (DeviceOperationResult.Success, null);

                await Task.Delay(_streamWaitPollIntervalMs).ConfigureAwait(false);
            }
        }

        public Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync()
            => Task.FromResult((DeviceOperationResult.Success, Array.Empty<byte>()));

        public Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl)
            => Task.FromResult(DeviceOperationResult.Success);

        public Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk)
            => Task.FromResult(DeviceOperationResult.Success);

        public Task<DeviceOperationResult> MoveAsync(StructurePosition? position)
            => Task.FromResult(DeviceOperationResult.Success);

        public Task<(DeviceOperationResult Result, long Length)> LengthAsync()
            => Task.FromResult((DeviceOperationResult.Success, 0L));
    }
}
