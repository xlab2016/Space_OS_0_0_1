using Data.Repository;
using TL;

namespace Magic.Drivers.WTelegram;

public sealed class WTelegramOpenOptions
{
    public required int ApiId { get; init; }
    public required string ApiHash { get; init; }
    public string? PhoneNumber { get; init; }
    public string? VerificationCode { get; init; }
    public Func<string?>? VerificationCodeFactory { get; init; }
    public string? Password { get; init; }
    public Func<string?>? PasswordFactory { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Email { get; init; }
    public Func<string?>? EmailVerificationCodeFactory { get; init; }
    public string? BotToken { get; init; }
    public string? SessionPathname { get; init; }
    public Func<string, string?>? Config { get; init; }
}

public sealed class TelegramHistoryRequest
{
    public long? ChannelId { get; init; }
    public string? ChannelUsername { get; init; }
    public string? ChannelTitle { get; init; }
    public FilterOperator? ChannelTitleOperator { get; init; }
    /// <summary>Forum topic id (message_thread_id) when reading history in a forum supergroup.</summary>
    public int? TopicId { get; init; }
}

public sealed class TelegramHistoryOpenOptions
{
    public TelegramHistoryPaging Paging { get; init; } = new();
    public TelegramHistoryFilter Filter { get; init; } = new();
}

public sealed class TelegramHistoryPaging
{
    public int Take { get; init; } = 50;
    public int OffsetId { get; init; }
    public int AddOffset { get; init; }
    public int MaxId { get; init; }
    public int MinId { get; init; }
    public TelegramHistoryOrder Order { get; init; } = TelegramHistoryOrder.Desc;
}

public enum TelegramHistoryOrder
{
    Desc = 0,
    Asc = 1
}

public sealed class TelegramHistoryFilter
{
    public string? TextContains { get; init; }
    public long? FromId { get; init; }
    public DateTime? SinceUtc { get; init; }
    public DateTime? UntilUtc { get; init; }
    public bool IncludeServiceMessages { get; init; }
    public bool WithMediaOnly { get; init; }

    public bool Matches(TelegramHistoryMessage message)
    {
        if (!IncludeServiceMessages && message.IsService)
            return false;

        if (!string.IsNullOrWhiteSpace(TextContains) &&
            (message.Text?.Contains(TextContains, StringComparison.OrdinalIgnoreCase) != true))
            return false;

        if (FromId.HasValue && message.FromId != FromId.Value)
            return false;

        if (SinceUtc.HasValue && message.DateUtc < SinceUtc.Value)
            return false;

        if (UntilUtc.HasValue && message.DateUtc > UntilUtc.Value)
            return false;

        if (WithMediaOnly && !message.HasMedia)
            return false;

        return true;
    }
}

public sealed class TelegramHistoryPage
{
    public required IReadOnlyList<TelegramHistoryMessage> Items { get; init; }
    public required bool HasMore { get; init; }
    public required int NextOffsetId { get; init; }
    public required int RawMessagesFetched { get; init; }
}

public sealed class TelegramHistoryMessage
{
    public required int Id { get; init; }
    public required DateTime DateUtc { get; init; }
    public required long? PeerId { get; init; }
    public required string PeerTitle { get; init; }
    public string? PeerUsername { get; init; }
    public required string FromTitle { get; init; }
    public required long? FromId { get; init; }
    public string? Username { get; init; }
    public required string? Text { get; init; }
    public required bool HasMedia { get; init; }
    public required bool IsService { get; init; }
    public required string MessageType { get; init; }
    public required string? MediaType { get; init; }
    public MessageMedia? Media { get; init; }

    internal static TelegramHistoryMessage From(MessageBase messageBase, Messages_MessagesBase resolver)
    {
        var peerInfo = resolver.UserOrChat(messageBase.Peer);
        var fromInfo = resolver.UserOrChat(messageBase.From ?? messageBase.Peer);

        if (messageBase is Message message)
        {
            return new TelegramHistoryMessage
            {
                Id = message.ID,
                DateUtc = message.Date.ToUniversalTime(),
                PeerId = message.Peer?.ID,
                PeerTitle = DescribePeer(peerInfo),
                PeerUsername = DescribeUsername(peerInfo),
                FromTitle = DescribePeer(fromInfo),
                FromId = message.From?.ID,
                Username = DescribeUsername(fromInfo),
                Text = message.message,
                HasMedia = message.media is not null,
                IsService = false,
                MessageType = nameof(Message),
                MediaType = message.media?.GetType().Name,
                Media = message.media
            };
        }

        if (messageBase is MessageService service)
        {
            return new TelegramHistoryMessage
            {
                Id = service.ID,
                DateUtc = service.Date.ToUniversalTime(),
                PeerId = service.Peer?.ID,
                PeerTitle = DescribePeer(peerInfo),
                PeerUsername = DescribeUsername(peerInfo),
                FromTitle = DescribePeer(fromInfo),
                FromId = service.From?.ID,
                Username = DescribeUsername(fromInfo),
                Text = service.action?.GetType().Name,
                HasMedia = false,
                IsService = true,
                MessageType = nameof(MessageService),
                MediaType = null,
                Media = null
            };
        }

        return new TelegramHistoryMessage
        {
            Id = messageBase.ID,
            DateUtc = DateTime.MinValue,
            PeerId = messageBase.Peer?.ID,
            PeerTitle = DescribePeer(peerInfo),
            PeerUsername = DescribeUsername(peerInfo),
            FromTitle = DescribePeer(fromInfo),
            FromId = messageBase.From?.ID,
            Username = DescribeUsername(fromInfo),
            Text = messageBase.ToString(),
            HasMedia = false,
            IsService = true,
            MessageType = messageBase.GetType().Name,
            MediaType = null,
            Media = null
        };
    }

    private static string DescribePeer(object? peer) => peer switch
    {
        User user => user.ToString(),
        ChatBase chat => chat.Title,
        null => string.Empty,
        _ => peer.ToString() ?? string.Empty
    };

    private static string? DescribeUsername(object? peer) => peer switch
    {
        User user => user.username,
        Channel channel => channel.username,
        _ => null
    };
}

public sealed class TelegramChatDescriptor
{
    public required long Id { get; init; }
    public required string Title { get; init; }
    public string? Username { get; init; }
    public required string Type { get; init; }
    public required bool IsActive { get; init; }
    /// <summary>When set, this descriptor is a forum topic: Id = topic id, ParentChatId = channel id.</summary>
    public long? ParentChatId { get; init; }

    public override string ToString()
    {
        return Username + " | " + Title;
    }
}

public sealed class TelegramTopicDescriptor
{
    public required long ChatId { get; init; }
    public required int TopicId { get; init; }
    public required string Title { get; init; }
}
