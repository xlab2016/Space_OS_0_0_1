using Magic.Drivers.WTelegram;
using Magic.Kernel.Core;
using Magic.Kernel.Devices.Streams.Drivers;
using Magic.Kernel.Interpretation;
using TL;
using Magic.Kernel;

namespace Magic.Kernel.Devices.Streams
{
    /// <summary>streamwait-compatible history iterator over WTelegram channel messages.</summary>
    public sealed class WTelegramHistoryStreamDevice : DefType
    {
        private readonly WTelegramConnection _connection;
        private readonly TelegramHistoryRequest _request;
        private readonly TelegramHistoryOpenOptions _options;
        private WTelegramHistoryStream? _history;
        private Queue<TelegramHistoryMessage> _buffer = new();
        private Dictionary<string, object?>? _lastAggregate;

        public WTelegramHistoryStreamDevice(
            WTelegramConnection connection,
            TelegramHistoryRequest request,
            TelegramHistoryOpenOptions options)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _request = request ?? throw new ArgumentNullException(nameof(request));
            _options = options ?? new TelegramHistoryOpenOptions();
        }

        public override Task<object?> Await()
            => Task.FromResult<object?>(this);

        public override Task<object?> AwaitObjAsync()
            => Task.FromResult<object?>(this);

        public override async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";
            if (string.Equals(name, "close", StringComparison.OrdinalIgnoreCase))
            {
                if (_history is not null)
                    await _history.CloseAsync().ConfigureAwait(false);
                _history = null;
                _buffer.Clear();
                _lastAggregate = null;
                return this;
            }

            throw new CallUnknownMethodException(name, this);
        }

        public override async Task<(bool IsEnd, object? Delta, object? Aggregate)> StreamWaitAsync(string streamWaitType)
        {
            if (!string.Equals(streamWaitType, "delta", StringComparison.OrdinalIgnoreCase))
                return (true, null, null);

            await EnsureHistoryAsync().ConfigureAwait(false);

            while (_buffer.Count == 0)
            {
                var page = await _history!.ReadPageAsync().ConfigureAwait(false);
                _lastAggregate = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["nextOffsetId"] = page.NextOffsetId,
                    ["hasMore"] = page.HasMore,
                    ["rawMessagesFetched"] = page.RawMessagesFetched
                };

                foreach (var item in page.Items)
                    _buffer.Enqueue(item);

                if (_buffer.Count == 0)
                {
                    if (!page.HasMore)
                        return (true, null, _lastAggregate);
                }
            }

            var message = _buffer.Dequeue();
            Console.WriteLine(message.Text);
            var delta = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["data"] = CreatePayload(message)
            };

            return (false, new DeltaWeakDisposable(delta), _lastAggregate);
        }

        private async Task EnsureHistoryAsync()
        {
            if (_history is not null)
                return;

            _history = _connection.History(_request);
            await _history.OpenAsync(_options).ConfigureAwait(false);
        }

        private Dictionary<string, object?> CreatePayload(TelegramHistoryMessage message)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = message.Id,
                ["time"] = message.DateUtc,
                ["tokenHash"] = null,
                ["chatId"] = message.PeerId?.ToString() ?? message.PeerUsername ?? message.PeerTitle,
                ["chatName"] = message.PeerTitle,
                ["username"] = message.Username ?? message.FromTitle,
                ["text"] = message.Text,
                ["messageType"] = message.MessageType,
                ["mediaType"] = message.MediaType,
                ["photo"] = CreateMediaHandle(message.Media, isPhoto: true),
                ["document"] = CreateMediaHandle(message.Media, isPhoto: false)
            };
        }

        private object? CreateMediaHandle(MessageMedia? media, bool isPhoto)
        {
            if (isPhoto && media is MessageMediaPhoto mediaPhoto)
            {
                return new WTelegramMediaHandle
                {
                    Connection = _connection,
                    Media = mediaPhoto,
                    Kind = "photo"
                };
            }

            if (!isPhoto && media is MessageMediaDocument mediaDocument)
            {
                return new WTelegramMediaHandle
                {
                    Connection = _connection,
                    Media = mediaDocument,
                    Kind = "document"
                };
            }

            return null;
        }
    }
}
