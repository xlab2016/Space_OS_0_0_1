using Magic.Drivers.WTelegram;
using Magic.Kernel.Devices;

namespace Magic.Kernel.Devices.Streams.Drivers
{
    /// <summary>Stateful WTelegram client session used by kernel stream devices.</summary>
    public sealed class WTelegramDriver : IAsyncDisposable
    {
        private WTelegramConnection? _connection;

        public bool IsOpened => _connection?.IsOpen == true;

        public WTelegramConnection Connection
            => _connection ?? throw new InvalidOperationException("WTelegram connection is not open.");

        public async Task<DeviceOperationResult> OpenAsync(WTelegramOpenOptions options, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(options);

            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }

            try
            {
                var connection = new WTelegramConnection();
                await connection.OpenAsync(options, cancellationToken).ConfigureAwait(false);
                _connection = connection;
                return DeviceOperationResult.Success;
            }
            catch (Exception ex)
            {
                return DeviceOperationResult.IOError(ex.Message, ex.HResult);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
                _connection = null;
            }
        }
    }
}
