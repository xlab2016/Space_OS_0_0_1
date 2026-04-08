using Magic.Drivers.WTelegram;
using Magic.Kernel.Devices;
using TL;

namespace Magic.Kernel.Devices.Streams.Drivers
{
    /// <summary>Read-only stream over a Telegram media object downloaded through an opened WTelegram session.</summary>
    public sealed class WTelegramNetworkFileDriver : IStreamDevice
    {
        private readonly WTelegramMediaHandle _mediaHandle;
        private byte[] _bytes = Array.Empty<byte>();
        private bool _opened;
        private bool _consumed;

        public WTelegramNetworkFileDriver(WTelegramMediaHandle mediaHandle)
        {
            _mediaHandle = mediaHandle ?? throw new ArgumentNullException(nameof(mediaHandle));
        }

        public Task<DeviceOperationResult> OpenAsync()
        {
            _opened = true;
            _consumed = false;
            _bytes = Array.Empty<byte>();
            return Task.FromResult(DeviceOperationResult.Success);
        }

        public async Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync()
        {
            if (!_opened)
                return (DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Not open"), Array.Empty<byte>());

            if (_bytes.Length == 0)
            {
                try
                {
                    _bytes = await DownloadAsync().ConfigureAwait(false);
                    _consumed = false;
                }
                catch (Exception ex)
                {
                    return (DeviceOperationResult.IOError(ex.Message, ex.HResult), Array.Empty<byte>());
                }
            }

            if (_consumed)
                return (DeviceOperationResult.Success, Array.Empty<byte>());

            _consumed = true;
            return (DeviceOperationResult.Success, _bytes);
        }

        public Task<DeviceOperationResult> WriteAsync(byte[] bytes)
            => Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Read-only stream"));

        public Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl)
            => Task.FromResult(DeviceOperationResult.Success);

        public Task<DeviceOperationResult> CloseAsync()
        {
            _opened = false;
            return Task.FromResult(DeviceOperationResult.Success);
        }

        public async Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync()
        {
            var (result, bytes) = await ReadAsync().ConfigureAwait(false);
            if (!result.IsSuccess || bytes.Length == 0)
                return (result, null);

            return (result, new StreamChunk
            {
                ChunkSize = bytes.Length,
                Data = bytes,
                DataFormat = DataFormat.Text,
                ApplicationFormat = ApplicationFormat.Unknown,
                Position = new StructurePosition { RelativeIndex = bytes.Length, RelativeIndexName = "" }
            });
        }

        public Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk)
            => Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Read-only stream"));

        public Task<DeviceOperationResult> MoveAsync(StructurePosition? position)
            => Task.FromResult(DeviceOperationResult.Success);

        public Task<(DeviceOperationResult Result, long Length)> LengthAsync()
            => Task.FromResult((DeviceOperationResult.Success, (long)_bytes.Length));

        private Task<byte[]> DownloadAsync()
        {
            return _mediaHandle.Media switch
            {
                MessageMediaPhoto mediaPhoto => _mediaHandle.Connection.DownloadPhotoBytesAsync(mediaPhoto),
                MessageMediaDocument mediaDocument => _mediaHandle.Connection.DownloadDocumentBytesAsync(mediaDocument),
                Message message when message.media is MessageMediaPhoto => _mediaHandle.Connection.DownloadPhotoBytesAsync(message),
                Message message when message.media is MessageMediaDocument => _mediaHandle.Connection.DownloadDocumentBytesAsync(message),
                _ => throw new InvalidOperationException($"Unsupported WTelegram media type '{_mediaHandle.Media.GetType().Name}'.")
            };
        }
    }
}
