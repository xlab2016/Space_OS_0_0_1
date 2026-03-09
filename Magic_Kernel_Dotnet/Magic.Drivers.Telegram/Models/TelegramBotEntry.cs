using AI;
using Telegram.Bot;

namespace Magic.Drivers.Telegram.Models
{
    public class TelegramBotEntry
    {
        public string Name { get; set; } = string.Empty;
        public ITelegramBotClient Client { get; set; } = null!;
        public bool Processing { get; set; }
        public bool Stopped { get; set; }
        public string? SecretToken { get; set; }

        public Func<MessageInfo, CancellationToken, Task>? OnMessageReceived { get; set; }
        public Func<Exception, CancellationToken, Task>? OnError { get; set; }
    }
}
