using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Magic.Kernel.Devices;
using Magic.Kernel.Core.OS;
using Magic.Drivers.Telegram;
using Magic.Kernel.Interpretation;
using Telegram.Bot.Types;

namespace Magic.Kernel.Devices.Streams.Drivers
{
    /// <summary>Stream driver for Telegram: one connection per instance, Write = send to chat, ReadChunk = dequeue received (long-polling in same instance).</summary>
    public class TelegramDriver : IStreamDevice
    {
        private readonly string _botToken;
        private long _defaultChatId;
        private bool _opened;
        private int _chunkIndex;
        private readonly ConcurrentQueue<byte[]> _incomingQueue = new();
        private TelegramConnection? _connection;
        private CancellationTokenSource? _receiveCts;
        private int _streamWaitPollIntervalMs = 50;

        /// <param name="streamWaitPollIntervalMs">Delay between queue checks when waiting for a message in ReadChunkAsync (default 50).</param>
        public TelegramDriver(string botToken, long defaultChatId = 0, int streamWaitPollIntervalMs = 50)
        {
            _botToken = botToken ?? throw new ArgumentNullException(nameof(botToken));
            _defaultChatId = defaultChatId;
            _streamWaitPollIntervalMs = Math.Clamp(streamWaitPollIntervalMs, 10, 10_000);
        }

        public Task<DeviceOperationResult> OpenAsync()
        {
            if (string.IsNullOrEmpty(_botToken))
                return Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Bot token is required"));
            _connection = new TelegramConnection(_botToken);
            _receiveCts = new CancellationTokenSource();
            _ = _connection.RunReceiveLoopAsync(
                (chatId, text, username, tokenHash, photo, document) =>
                    EnqueueIncomingMessage(chatId, text, username, tokenHash, photo, document),
                _receiveCts.Token);
            _opened = true;
            return Task.FromResult(DeviceOperationResult.Success);
        }

        public async Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync()
        {
            if (!_opened)
                return (DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Not open"), Array.Empty<byte>());
            if (!_incomingQueue.TryDequeue(out var bytes))
                return (DeviceOperationResult.Success, Array.Empty<byte>());
            return (DeviceOperationResult.Success, bytes ?? Array.Empty<byte>());
        }

        public async Task<DeviceOperationResult> WriteAsync(byte[] bytes)
        {
            if (!_opened)
                return DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Not open");
            if (_defaultChatId == 0)
                return DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Default chat ID not set (use Control to set)");
            if (bytes == null || bytes.Length == 0)
                return DeviceOperationResult.Success;
            if (_connection == null)
                return DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Not open");
            try
            {
                var text = Encoding.UTF8.GetString(bytes);
                await _connection.SendMessageAsync(_defaultChatId, text).ConfigureAwait(false);
                return DeviceOperationResult.Success;
            }
            catch (Exception ex)
            {
                return DeviceOperationResult.IOError(ex.Message, ex.HResult);
            }
        }

        public async Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync()
        {
            if (!_opened)
                return (DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Not open"), null);

            var prefix = Magic.Kernel.Interpretation.ExecutionContext.GetPrefix();

            Console.WriteLine(prefix + "TelegramDriver: Waiting for incoming stream...");

            while (true)
            {
                if (!_opened)
                    return (DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Not open"), null);

                if (_incomingQueue.TryDequeue(out var data))
                {
                    var chunk = new StreamChunk
                    {
                        ChunkSize = data?.Length ?? 0,
                        Data = data ?? Array.Empty<byte>(),
                        DataFormat = DataFormat.Json,
                        ApplicationFormat = ApplicationFormat.Unknown,
                        Position = new StructurePosition { RelativeIndex = _chunkIndex++, RelativeIndexName = "" }
                    };
                    return (DeviceOperationResult.Success, chunk);
                }

                await Task.Delay(_streamWaitPollIntervalMs).ConfigureAwait(false);
            }
        }

        public Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk)
        {
            if (chunk?.Data == null || chunk.Data.Length == 0)
                return Task.FromResult(DeviceOperationResult.Success);
            return WriteAsync(chunk.Data);
        }

        public Task<DeviceOperationResult> MoveAsync(StructurePosition? position) => Task.FromResult(DeviceOperationResult.Success);

        public Task<(DeviceOperationResult Result, long Length)> LengthAsync()
            => Task.FromResult((DeviceOperationResult.Success, 0L));

        public Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl)
        {
            if (deviceControl is TelegramDriverControl ctrl)
            {
                if (ctrl.DefaultChatId.HasValue)
                    _defaultChatId = ctrl.DefaultChatId.Value;
                if (ctrl.StreamWaitPollIntervalMs.HasValue)
                    _streamWaitPollIntervalMs = Math.Clamp(ctrl.StreamWaitPollIntervalMs.Value, 10, 10_000);
            }
            return Task.FromResult(DeviceOperationResult.Success);
        }

        public Task<DeviceOperationResult> CloseAsync()
        {
            _receiveCts?.Cancel();
            _receiveCts?.Dispose();
            _receiveCts = null;
            _connection = null;
            _opened = false;
            return Task.FromResult(DeviceOperationResult.Success);
        }

        /// <summary>Push incoming message into the stream (used by receive loop; also callable from outside if needed).</summary>
        public void EnqueueIncoming(byte[] messageBytes)
        {
            if (messageBytes != null && messageBytes.Length > 0)
                _incomingQueue.Enqueue(messageBytes);
        }

        /// <summary>
        /// Enqueue incoming message as JSON:
        /// {
        ///   "chatId",
        ///   "text",
        ///   "username",
        ///   "tokenHash",
        ///   "photo",
        ///   "document"
        /// }
        /// </summary>
        public void EnqueueIncomingMessage(
            long chatId,
            string text,
            string? username = null,
            string? tokenHash = null,
            PhotoSize[]? photo = null,
            Document? document = null)
        {
            var obj = new
            {
                chatId,
                text,
                username = username ?? string.Empty,
                tokenHash = tokenHash ?? string.Empty,
                photo,
                document
            };
            var json = JsonSerializer.Serialize(obj);
            EnqueueIncoming(Encoding.UTF8.GetBytes(json));
        }
    }

    /// <summary>Control for TelegramDriver: set DefaultChatId, StreamWaitPollIntervalMs.</summary>
    public class TelegramDriverControl : DeviceControlBase
    {
        public long? DefaultChatId { get; set; }
        /// <summary>Delay in ms between queue checks in ReadChunkAsync (10–10000, applied on next wait).</summary>
        public int? StreamWaitPollIntervalMs { get; set; }
    }
}
