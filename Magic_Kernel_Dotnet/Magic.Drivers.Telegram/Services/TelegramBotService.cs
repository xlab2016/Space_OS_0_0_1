using AI;
using Magic.Drivers.Telegram.Models;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Magic.Drivers.Telegram.Services
{
    public class TelegramBotService
    {
        private readonly ILogger<TelegramBotService> _logger;
        private readonly List<TelegramBotEntry> _entries = new();

        public TelegramBotEntry? this[string name] => _entries.FirstOrDefault(e => e.Name == name);

        public TelegramBotService(ILogger<TelegramBotService> logger)
        {
            _logger = logger;
        }

        public TelegramBotEntry AddClient(string name, ITelegramBotClient client, string? secretToken = null)
        {
            var result = new TelegramBotEntry
            {
                Name = name,
                Client = client,
                SecretToken = secretToken,
            };
            _entries.Add(result);
            return result;
        }

        public void RemoveClient(string name)
        {
            var entry = _entries.FirstOrDefault(e => e.Name == name);
            if (entry != null)
                _entries.Remove(entry);
        }

        public async Task HandleUpdate(Update update, TelegramBotEntry entry, CancellationToken cancellationToken = default)
        {
            var message = update.Message;

            if (entry.Stopped) return;

            var botClient = entry.Client;

            if (entry.Processing)
            {
                await botClient.SendMessage(message!.Chat.Id, "Запрос обрабатывается, пожалуйста подождите...", cancellationToken: cancellationToken);
                return;
            }

            var fileId = message?.Document?.FileId;
            var callbackQuery = update.CallbackQuery;
            var callbackData = callbackQuery?.Data;

            if (callbackQuery != null && message == null)
                message = callbackQuery.Message;

            if (message == null)
                return;

            if (message.Text is null && message.Contact is null && string.IsNullOrEmpty(fileId) && callbackData is null)
                return;

            if (callbackQuery != null)
                await botClient.AnswerCallbackQuery(callbackQuery.Id);

            var displayName = TelegramMessageSender.GetDisplayName(message);
            var messageInfo = new MessageInfo
            {
                Text = callbackData ?? message.Text,
                ChatId = message.Chat.Id,
                Username = !string.IsNullOrEmpty(displayName)
                    ? displayName
                    : (message.Chat?.Username ?? $"user_chatid_{message.Chat.Id}"),
                Contact = message.Contact != null
                    ? new ContactInfo
                    {
                        FirstName = message.Contact.FirstName,
                        LastName = message.Contact.LastName,
                        PhoneNumber = message.Contact.PhoneNumber,
                    }
                    : null,
                IsCallback = callbackData is not null,
            };

            if (!string.IsNullOrEmpty(fileId))
            {
                var file = await botClient.GetFile(fileId, cancellationToken);
                messageInfo.File = new AI.FileInfo
                {
                    FilePath = file.FilePath,
                    Size = file.FileSize ?? 0,
                    MimeType = message.Document?.MimeType
                };
            }

            if (string.IsNullOrEmpty(messageInfo.Text))
            {
                if (message.Contact != null)
                    messageInfo.Text = "Contact";
                if (!string.IsNullOrEmpty(fileId))
                    messageInfo.Text = "File";
            }

            entry.Processing = true;
            try
            {
                if (entry.OnMessageReceived != null)
                    await entry.OnMessageReceived(messageInfo, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling Telegram update");
            }
            finally
            {
                entry.Processing = false;
            }
        }

        public async Task<int> SendMessage(string botName, long chatId, string text, string? parseMode = null, CancellationToken cancellationToken = default)
        {
            var entry = this[botName];
            if (entry == null)
                throw new ArgumentException($"Bot not found: {botName}");

            var parseModeEnum = parseMode switch
            {
                "MarkdownV2" => global::Telegram.Bot.Types.Enums.ParseMode.MarkdownV2,
                "HTML" => global::Telegram.Bot.Types.Enums.ParseMode.Html,
                _ => (global::Telegram.Bot.Types.Enums.ParseMode?)null
            };

            global::Telegram.Bot.Types.Message message;
            if (parseModeEnum.HasValue)
            {
                message = await entry.Client.SendMessage(chatId, text, parseMode: parseModeEnum.Value, cancellationToken: cancellationToken);
            }
            else
            {
                message = await entry.Client.SendMessage(chatId, text, cancellationToken: cancellationToken);
            }
            return message.MessageId;
        }
    }
}
