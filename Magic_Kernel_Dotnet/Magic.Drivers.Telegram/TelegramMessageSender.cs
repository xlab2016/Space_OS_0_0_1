using System.Linq;
using Telegram.Bot.Types;

namespace Magic.Drivers.Telegram
{
    /// <summary>Maps Bot API fields: visible name vs optional @username.</summary>
    public static class TelegramMessageSender
    {
        /// <summary>Public @handle when set (no leading @).</summary>
        public static string? GetPublicUsername(Message msg) =>
            string.IsNullOrEmpty(msg.From?.Username) ? msg.Chat?.Username : msg.From?.Username;

        /// <summary>
        /// Name as shown in Telegram UI: sender first+last, else group/channel title,
        /// else private chat first+last from <see cref="Chat"/>.
        /// </summary>
        public static string GetDisplayName(Message msg)
        {
            if (msg.From != null)
            {
                var fromName = JoinNames(msg.From.FirstName, msg.From.LastName);
                if (fromName.Length > 0)
                    return fromName;
            }

            var chat = msg.Chat;
            if (chat == null)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(chat.Title))
                return chat.Title.Trim();

            return JoinNames(chat.FirstName, chat.LastName);
        }

        private static string JoinNames(string? first, string? last)
        {
            var parts = new[] { first, last }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim());
            return string.Join(" ", parts);
        }
    }
}
