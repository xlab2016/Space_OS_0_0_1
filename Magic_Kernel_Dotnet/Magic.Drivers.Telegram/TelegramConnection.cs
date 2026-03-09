using System.IO;
using System.Security.Cryptography;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Magic.Drivers.Telegram
{
    /// <summary>Single Telegram bot connection: send and long-polling receive in one instance.</summary>
    public sealed class TelegramConnection
    {
        private readonly TelegramBotClient _client;
        private readonly string _botToken;
        private readonly string _tokenHash;

        public TelegramConnection(string botToken)
        {
            _botToken = botToken ?? throw new ArgumentNullException(nameof(botToken));
            _client = new TelegramBotClient(_botToken);
            _tokenHash = ComputeTokenHash(_botToken);
        }

        private static string ComputeTokenHash(string token)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        /// <summary>Send text to chat. Uses the same client instance as receive.</summary>
        public async Task<int> SendMessageAsync(long chatId, string text, CancellationToken cancellationToken = default)
        {
            var message = await _client.SendMessage(chatId, text, cancellationToken: cancellationToken);
            return message.MessageId;
        }

        /// <summary>
        /// Runs long-polling loop and invokes onMessage for each received message.
        /// Exits when cancellation is requested.
        /// </summary>
        /// <param name="onMessage">
        /// (chatId, textOrCaption, username, tokenHash, photo, document)
        /// </param>
        public async Task RunReceiveLoopAsync(
            Action<long, string, string?, string, PhotoSize[]?, Document?> onMessage,
            CancellationToken cancellationToken = default)
        {
            int? offset = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var updates = await _client.GetUpdates(offset, timeout: 60, cancellationToken: cancellationToken);
                    foreach (var update in updates)
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        offset = update.Id + 1;
                        var msg = update.Message;
                        if (msg == null) continue;

                        var text = msg.Text ?? msg.Caption ?? string.Empty;
                        var hasPhoto = msg.Photo != null && msg.Photo.Length > 0;
                        var hasDocument = msg.Document != null;

                        if (string.IsNullOrEmpty(text) && !hasPhoto && !hasDocument)
                            continue;

                        var chatId = msg.Chat.Id;
                        var username = msg.Chat.Username;
                        var photo = msg.Photo;
                        var document = msg.Document;
                        try
                        {
                            onMessage(chatId, text, username, _tokenHash, photo, document);
                        }
                        catch
                        {
                            // consumer should not break the loop
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    if (cancellationToken.IsCancellationRequested) break;
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
            }
        }

        /// <summary>Get Telegram FilePath for file_id without downloading file bytes.</summary>
        public static async Task<string?> GetFilePathAsync(string botToken, string fileId, CancellationToken cancellationToken = default)
        {
            var client = new TelegramBotClient(botToken);
            var tgFile = await client.GetFile(fileId, cancellationToken).ConfigureAwait(false);
            return tgFile.FilePath;
        }

        /// <summary>Download file by file_id and return bytes. Used by TelegramNetworkFileDriver.</summary>
        public static async Task<byte[]> GetFileBytesAsync(string botToken, string fileId, CancellationToken cancellationToken = default)
        {
            var client = new TelegramBotClient(botToken);
            var tgFile = await client.GetFile(fileId, cancellationToken).ConfigureAwait(false);
            await using var ms = new MemoryStream();
            await client.DownloadFile(tgFile, ms, cancellationToken).ConfigureAwait(false);
            return ms.ToArray();
        }
    }
}
