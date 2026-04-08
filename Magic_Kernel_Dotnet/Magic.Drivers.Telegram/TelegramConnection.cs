using System.IO;
using System.Security.Cryptography;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Magic.Drivers.Telegram
{
    public sealed class TelegramIncomingMessage
    {
        public int Id { get; init; }
        public DateTime Time { get; init; }
        public long ChatId { get; init; }
        public string Text { get; init; } = string.Empty;
        /// <summary>Optional public @username (no @).</summary>
        public string? Username { get; init; }
        /// <summary>Visible name in Telegram (first+last or chat title).</summary>
        public string DisplayName { get; init; } = string.Empty;
        public string TokenHash { get; init; } = string.Empty;
        public PhotoSize[]? Photo { get; init; }
        public Document? Document { get; init; }
        public TelegramIncomingMessage? Reply { get; init; }
    }

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
        /// TelegramIncomingMessage with id/time/reply support
        /// </param>
        public async Task RunReceiveLoopAsync(
            Action<TelegramIncomingMessage> onMessage,
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

                        var incomingMessage = ToIncomingMessage(msg);
                        try
                        {
                            onMessage(incomingMessage);
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

        private TelegramIncomingMessage ToIncomingMessage(Message msg)
        {
            var text = msg.Text ?? msg.Caption ?? string.Empty;
            return new TelegramIncomingMessage
            {
                Id = msg.Id,
                Time = msg.Date,
                ChatId = msg.Chat?.Id ?? 0,
                Text = text,
                Username = TelegramMessageSender.GetPublicUsername(msg),
                DisplayName = TelegramMessageSender.GetDisplayName(msg),
                TokenHash = _tokenHash,
                Photo = msg.Photo,
                Document = msg.Document,
                Reply = msg.ReplyToMessage == null ? null : ToIncomingMessage(msg.ReplyToMessage)
            };
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
