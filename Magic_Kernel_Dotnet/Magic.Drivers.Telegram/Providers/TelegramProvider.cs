using AI;
using Magic.Drivers.Telegram.Mappings;
using Microsoft.Extensions.Logging;
using Magic.Drivers.Telegram.Models;
using Magic.Drivers.Telegram.Services;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Magic.Drivers.Telegram.Providers
{
    public class TelegramProvider : IChannelClientProvider
    {
        private TelegramBotClient _botClient = null!;
        private readonly ILogger<TelegramProvider> _logger;
        private readonly TelegramBotService _botService;
        private readonly AgentConnection _connection;
        private bool _stopped;
        private readonly AIMarkupToReplyMarkupMap _replyMarkupMap = new();
        private readonly AIMarkupToInlineMarkupMap _inlineMarkupMap = new();
        private Func<MessageInfo, CancellationToken, Task>? _onMessageReceived;
        private CancellationToken _cancellationToken;
        private TelegramBotEntry _botEntry = null!;

        public string? ChannelName { get; set; }
        public string? SecretToken { get; set; }
        public bool Webhook { get; set; }

        public TelegramProvider(ILogger<TelegramProvider> logger, TelegramBotService botService, AgentConnection connection)
        {
            _logger = logger;
            _botService = botService;
            _connection = connection;
        }

        public void Start(string telegramToken)
        {
            _botClient = new TelegramBotClient(telegramToken);
            _botEntry = _botService.AddClient(ChannelName ?? "", _botClient, SecretToken);
        }

        public async Task Stop()
        {
            _stopped = true;
            if (!Webhook)
                _botClient.OnUpdate -= BotClient_OnUpdate;
            _botService.RemoveClient(ChannelName ?? "");
            await Task.CompletedTask;
        }

        public async Task StartReceivingAsync(CancellationToken cancellationToken, Func<MessageInfo, CancellationToken, Task> onMessageReceived, Func<Exception, CancellationToken, Task> onError)
        {
            _cancellationToken = cancellationToken;
            _onMessageReceived = onMessageReceived;
            _botEntry.OnMessageReceived = onMessageReceived;
            _botEntry.OnError = onError;

            var me = await _botClient.GetMe(cancellationToken);
            _logger.LogInformation("@{Username} is running...", me.Username);

            if (Webhook)
            {
                await _botClient.DeleteWebhook(false, cancellationToken);
                await _botClient.SetWebhook($"{_connection.Host}/api/telegram/webhook?name={ChannelName}", null, null, null, null, false, SecretToken, cancellationToken);
            }
            else
            {
                _botClient.OnUpdate += BotClient_OnUpdate;
            }

            _botClient.OnError += async (Exception exception, global::Telegram.Bot.Polling.HandleErrorSource _) =>
            {
                await (_botEntry.OnError?.Invoke(exception, _cancellationToken) ?? Task.CompletedTask);
                // Fatal 409 handling can be done in onError if needed
            };
        }

        private async Task BotClient_OnUpdate(Update update)
        {
            await _botService.HandleUpdate(update, _botEntry, _cancellationToken);
        }

        public async Task SendTypingAsync(long chatId, CancellationToken cancellationToken)
        {
            await _botClient.SendChatAction(chatId, ChatAction.Typing);
        }

        public async Task<int> SendTextMessageAsync(long chatId, string message, AIMarkup? markup, int? parseMode, bool escape, CancellationToken cancellationToken)
        {
            var replyMarkup = markup != null
                ? (!markup.Keyboard.Inline ? _replyMarkupMap.Map(markup) : _inlineMarkupMap.Map(markup))
                : null;

            if (escape)
                message = parseMode == (int)ParseModeIds.MarkdownV2 ? message.EscapeMarkdownV2() : message;

            var response = await _botClient.SendMessage(chatId, message, replyMarkup: replyMarkup,
                parseMode: parseMode == (int)ParseModeIds.MarkdownV2 ? ParseMode.MarkdownV2 : ParseMode.Html,
                cancellationToken: cancellationToken);
            return response.MessageId;
        }

        public async Task EditTextMessageAsync(long chatId, int messageId, string text, AIMarkup? markup, int? parseMode, bool escape, CancellationToken cancellationToken)
        {
            if (escape)
                text = parseMode == (int)ParseModeIds.MarkdownV2 ? text.EscapeMarkdownV2() : text;
            await _botClient.EditMessageText(chatId, messageId, text,
                parseMode: parseMode == (int)ParseModeIds.MarkdownV2 ? ParseMode.MarkdownV2 : ParseMode.Html,
                null, null, null, null, cancellationToken);
        }

        public async Task<int> SendVideoAsync(long chatId, string base64Content, CancellationToken cancellationToken)
        {
            var bytes = Convert.FromBase64String(base64Content);
            using var stream = new MemoryStream(bytes);
            var file = InputFile.FromStream(stream);
            var response = await _botClient.SendVideo(chatId, file, cancellationToken: cancellationToken);
            return response.MessageId;
        }

        public async Task<int> SendVideoNoteAsync(long chatId, string base64Content, int? length = null, CancellationToken cancellationToken = default)
        {
            var bytes = Convert.FromBase64String(base64Content);
            using var stream = new MemoryStream(bytes);
            var file = InputFile.FromStream(stream);
            var response = await _botClient.SendVideoNote(chatId, file, length: length, cancellationToken: cancellationToken);
            return response.MessageId;
        }

        public async Task DeleteTextMessageAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            await _botClient.DeleteMessage(chatId, messageId, cancellationToken);
        }

        public async Task MonitorConnectionAsync(CancellationToken cancellationToken, Func<Task>? ping = null)
        {
            while (!cancellationToken.IsCancellationRequested && !_stopped)
            {
                try
                {
                    if (ping != null) await ping();
                    await _botClient.GetMe(cancellationToken);
                    if (ping != null) await ping();
                }
                catch (Exception ex)
                {
                    _logger.LogError("[HEALTH CHECK:{ChannelName}] Ошибка подключения к Telegram API: {Message}", ChannelName, ex.Message);
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(40), cancellationToken);
                        await _botClient.GetMe(cancellationToken);
                        _logger.LogWarning("[HEALTH CHECK:{ChannelName}] Подключение восстановлено.", ChannelName);
                    }
                    catch (Exception retryEx)
                    {
                        _logger.LogError("[HEALTH CHECK:{ChannelName}] Не удалось восстановить: {Message}", ChannelName, retryEx.Message);
                        if (!Webhook)
                            await Stop();
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(150), cancellationToken);
            }
        }
    }
}
