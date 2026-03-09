using System;
using System.Threading.Tasks;
using Magic.Kernel.Devices;
using Magic.Drivers.Telegram;

namespace Magic.Kernel.Devices.Streams.Drivers
{
    /// <summary>Stream driver for Telegram file-by-file_id: downloads file via Bot API and exposes as read-only bytes.</summary>
    public class TelegramNetworkFileDriver : IStreamDevice
    {
        private readonly string _botToken;
        private readonly string _fileId;
        private string? _filePath;
        private byte[] _bytes = Array.Empty<byte>();
        private bool _opened;
        private int _readPosition;

        public TelegramNetworkFileDriver(string botToken, string fileId)
        {
            _botToken = botToken ?? throw new ArgumentNullException(nameof(botToken));
            _fileId = fileId ?? throw new ArgumentNullException(nameof(fileId));
        }

        public async Task<DeviceOperationResult> OpenAsync()
        {
            if (string.IsNullOrEmpty(_botToken) || string.IsNullOrEmpty(_fileId))
                return DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Bot token and file_id are required");
            try
            {
                // Only resolve Telegram FilePath on open; actual bytes are downloaded lazily on first Read/ReadChunk.
                _filePath = await TelegramConnection.GetFilePathAsync(_botToken, _fileId).ConfigureAwait(false);
                _opened = true;
                _readPosition = 0;
                _bytes = Array.Empty<byte>();
                return DeviceOperationResult.Success;
            }
            catch (Exception ex)
            {
                return DeviceOperationResult.IOError(ex.Message, ex.HResult);
            }
        }

        public async Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync()
        {
            if (!_opened)
                return (DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Not open"), Array.Empty<byte>());

            if (_bytes.Length == 0)
            {
                try
                {
                    // Lazy download on first read.
                    _bytes = await TelegramConnection.GetFileBytesAsync(_botToken, _fileId).ConfigureAwait(false);
                    _readPosition = 0;
                }
                catch (Exception ex)
                {
                    return (DeviceOperationResult.IOError(ex.Message, ex.HResult), Array.Empty<byte>());
                }
            }

            var remaining = _bytes.Length - _readPosition;
            if (remaining <= 0)
                return (DeviceOperationResult.Success, Array.Empty<byte>());
            var chunk = new byte[remaining];
            Array.Copy(_bytes, _readPosition, chunk, 0, remaining);
            _readPosition = _bytes.Length;
            return (DeviceOperationResult.Success, chunk);
        }

        public Task<DeviceOperationResult> WriteAsync(byte[] bytes) =>
            Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Read-only stream"));

        public Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl) =>
            Task.FromResult(DeviceOperationResult.Success);

        public Task<DeviceOperationResult> CloseAsync()
        {
            _opened = false;
            return Task.FromResult(DeviceOperationResult.Success);
        }

        public async Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync()
        {
            var (result, bytes) = await ReadAsync().ConfigureAwait(false);
            if (!result.IsSuccess || bytes == null || bytes.Length == 0)
                return (result, null);
            var chunk = new StreamChunk
            {
                ChunkSize = bytes.Length,
                Data = bytes,
                DataFormat = DataFormat.Text,
                ApplicationFormat = ApplicationFormat.Unknown,
                Position = new StructurePosition { RelativeIndex = _readPosition, RelativeIndexName = "" }
            };
            return (result, chunk);
        }

        public Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk) =>
            Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Read-only stream"));

        public Task<DeviceOperationResult> MoveAsync(StructurePosition? position) =>
            Task.FromResult(DeviceOperationResult.Success);

        public Task<(DeviceOperationResult Result, long Length)> LengthAsync() =>
            Task.FromResult((DeviceOperationResult.Success, (long)_bytes.Length));
    }
}
