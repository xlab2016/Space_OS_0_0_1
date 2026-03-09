using Telegram.Bot;

namespace Magic.Drivers.Telegram
{
    /// <summary>Minimal send-only helper for stream driver (no service/DI).</summary>
    public static class TelegramSendHelper
    {
        public static async Task<int> SendMessageAsync(string botToken, long chatId, string text, CancellationToken cancellationToken = default)
        {
            var client = new TelegramBotClient(botToken);
            var message = await client.SendMessage(chatId, text, cancellationToken: cancellationToken);
            return message.MessageId;
        }
    }
}
