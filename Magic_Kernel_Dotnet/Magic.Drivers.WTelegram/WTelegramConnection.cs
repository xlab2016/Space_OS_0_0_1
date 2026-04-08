using System.Globalization;
using System.Text.RegularExpressions;
using Data.Repository;
using TL;
using WTelegram;

namespace Magic.Drivers.WTelegram;

public sealed class WTelegramConnection : IAsyncDisposable
{
    private static readonly Regex FloodWaitRegex = new(@"FLOOD_WAIT_(\d+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private const int PaginationThrottleMs = 350;
    private Client? _client;
    private WTelegramOpenOptions? _options;

    public bool IsOpen => _client is not null;

    internal Client Client => _client ?? throw new InvalidOperationException("Connection is not open. Call OpenAsync first.");

    public async Task OpenAsync(WTelegramOpenOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_client is not null)
            throw new InvalidOperationException("Connection is already open.");

        _options = options;
        var client = new Client(GetConfigValue);

        try
        {
            if (!string.IsNullOrWhiteSpace(options.BotToken))
            {
                await ExecuteWithFloodWaitAsync(() => client.LoginBotIfNeeded(options.BotToken), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await LoginAsUserAsync(client, cancellationToken).ConfigureAwait(false);
            }

            _client = client;
        }
        catch
        {
            client.Dispose();
            _options = null;
            throw;
        }
    }

    public WTelegramHistoryStream History(TelegramHistoryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new WTelegramHistoryStream(this, request);
    }

    public async Task<IReadOnlyList<TelegramChatDescriptor>> GetChatsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dialogs = await ExecuteWithFloodWaitAsync(() => Client.Messages_GetAllDialogs(), cancellationToken).ConfigureAwait(false);
        var list = new List<TelegramChatDescriptor>();

        foreach (var dialog in dialogs.Dialogs.OfType<Dialog>())
        {
            var peer = dialogs.UserOrChat(dialog.Peer);
            var descriptor = CreateDialogDescriptor(peer);
            if (descriptor is null)
                continue;

            list.Add(descriptor);

            if (descriptor.Title == "AGISecret")
            {

            }

            if (peer is Channel channel && IsForumChannel(channel))
            {
                var topics = await GetForumTopicsInternalAsync(channel, cancellationToken).ConfigureAwait(false);
                foreach (var topic in topics)
                {
                    list.Add(new TelegramChatDescriptor
                    {
                        Id = topic.TopicId,
                        Title = topic.Title,
                        Username = null,
                        Type = "Topic",
                        IsActive = true,
                        ParentChatId = channel.ID
                    });
                }
            }
        }

        return list
            .DistinctBy(x => (x.Type, x.Id, x.ParentChatId))
            .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ParentChatId ?? x.Id)
            .ThenBy(x => x.Id)
            .ToArray();
    }

    /// <summary>Returns forum topics (подуровни) for a supergroup with topics enabled.</summary>
    public async Task<IReadOnlyList<TelegramTopicDescriptor>> GetForumTopicsAsync(long chatId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var dialogs = await ExecuteWithFloodWaitAsync(() => Client.Messages_GetAllDialogs(), cancellationToken).ConfigureAwait(false);
        var peer = dialogs.Dialogs
            .OfType<Dialog>()
            .Select(d => dialogs.UserOrChat(d.Peer))
            .FirstOrDefault(p => p is not null && p.ID == chatId);

        if (peer is not Channel channel || !IsForumChannel(channel))
            return Array.Empty<TelegramTopicDescriptor>();

        return await GetForumTopicsInternalAsync(channel, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TelegramChatDescriptor>> FindChatByQueryAsync(WTelegramChatQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        query.Paging ??= new Paging();
        query.Paging.Calculate();
        query.Filter ??= new WTelegramChatFilter();
        query.Sort ??= new WTelegramChatSort();

        var skip = Math.Max(0, query.Paging.Skip ?? 0);
        var take = Math.Max(1, query.Paging.Take ?? query.Paging.PageSize ?? 100);
        var requiredMatches = skip + take;

        var chats = await GetChatsAsync(cancellationToken).ConfigureAwait(false);
        var matchedChats = chats
            .Where(chat => MatchesQuery(chat, query))
            .Take(requiredMatches)
            .ToArray();

        return ApplyQueryWindow(matchedChats, query, skip, take);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else
            _client?.Dispose();

        _client = null;
        _options = null;
    }

    internal async Task<(InputPeer Peer, int? TopicTopMessageId)> ResolvePeerAsync(TelegramHistoryRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (request.ChannelId.HasValue)
        {
            var dialogs = await ExecuteWithFloodWaitAsync(() => Client.Messages_GetAllDialogs(), cancellationToken).ConfigureAwait(false);
            var peer = dialogs.Dialogs
                .OfType<Dialog>()
                .Select(dialog => dialogs.UserOrChat(dialog.Peer))
                .FirstOrDefault(peer => peer is not null && peer.ID == request.ChannelId.Value);
            var inputPeer = ToInputPeer(peer);
            if (inputPeer is not null)
                return await CreateResolvedPeerAsync(inputPeer, request.TopicId, cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException($"Channel/chat with id '{request.ChannelId.Value}' was not found in joined chats.");
        }

        if (!string.IsNullOrWhiteSpace(request.ChannelUsername))
        {
            var username = request.ChannelUsername.Trim().TrimStart('@');
            var resolved = await ExecuteWithFloodWaitAsync(() => Client.Contacts_ResolveUsername(username), cancellationToken).ConfigureAwait(false);
            if (resolved.Chat is not null)
                return await CreateResolvedPeerAsync(resolved.Chat, request.TopicId, cancellationToken).ConfigureAwait(false);
            if (resolved.User is not null)
                return await CreateResolvedPeerAsync(resolved.User, request.TopicId, cancellationToken).ConfigureAwait(false);

            throw new InvalidOperationException($"Username '{request.ChannelUsername}' was resolved, but no peer was returned.");
        }

        if (!string.IsNullOrWhiteSpace(request.ChannelTitle))
        {
            var channelTitleOperator = request.ChannelTitleOperator ?? FilterOperator.Contains;
            var chats = await FindChatByQueryAsync(new WTelegramChatQuery
            {
                FilterOperator = channelTitleOperator,
                Filter = new WTelegramChatFilter
                {
                    Title = request.ChannelTitle,
                    IsActive = true
                },
                Paging = new Paging
                {
                    Take = 2
                }
            }, cancellationToken).ConfigureAwait(false);

            if (chats.Count == 0)
                throw new InvalidOperationException($"Chat with title '{request.ChannelTitle}' was not found.");
            if (chats.Count > 1)
                throw new InvalidOperationException($"More than one chat matched title '{request.ChannelTitle}'. Use ChannelId or ChannelUsername.");
            return await ResolvePeerByDescriptorAsync(chats[0], cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("History request requires either ChannelId, ChannelUsername, or ChannelTitle.");
    }

    public async Task<Storage_FileType> DownloadPhotoAsync(
        MessageMediaPhoto mediaPhoto,
        Stream outputStream,
        PhotoSizeBase? photoSize = null,
        Client.ProgressCallback? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaPhoto);
        ArgumentNullException.ThrowIfNull(outputStream);

        if (mediaPhoto.photo is not Photo photo)
            throw new InvalidOperationException("MessageMediaPhoto does not contain a downloadable Photo instance.");

        return await ExecuteWithFloodWaitAsync(() => Client.DownloadFileAsync(
            photo,
            outputStream,
            photoSize,
            WrapProgress(progress, cancellationToken)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> DownloadPhotoBytesAsync(
        MessageMediaPhoto mediaPhoto,
        PhotoSizeBase? photoSize = null,
        Client.ProgressCallback? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var memoryStream = new MemoryStream();
        await DownloadPhotoAsync(mediaPhoto, memoryStream, photoSize, progress, cancellationToken).ConfigureAwait(false);
        return memoryStream.ToArray();
    }

    public async Task<Storage_FileType> DownloadPhotoFileAsync(
        MessageMediaPhoto mediaPhoto,
        string filePath,
        PhotoSizeBase? photoSize = null,
        Client.ProgressCallback? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using var fileStream = File.Create(filePath);
        return await DownloadPhotoAsync(mediaPhoto, fileStream, photoSize, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Storage_FileType> DownloadPhotoAsync(
        Message message,
        Stream outputStream,
        PhotoSizeBase? photoSize = null,
        Client.ProgressCallback? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.media is not MessageMediaPhoto mediaPhoto)
            throw new InvalidOperationException("Message does not contain MessageMediaPhoto.");

        return await DownloadPhotoAsync(mediaPhoto, outputStream, photoSize, progress, cancellationToken).ConfigureAwait(false);
    }

    public Task<byte[]> DownloadPhotoBytesAsync(
        Message message,
        PhotoSizeBase? photoSize = null,
        Client.ProgressCallback? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.media is not MessageMediaPhoto mediaPhoto)
            throw new InvalidOperationException("Message does not contain MessageMediaPhoto.");

        return DownloadPhotoBytesAsync(mediaPhoto, photoSize, progress, cancellationToken);
    }

    public Task<Storage_FileType> DownloadPhotoFileAsync(
        Message message,
        string filePath,
        PhotoSizeBase? photoSize = null,
        Client.ProgressCallback? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.media is not MessageMediaPhoto mediaPhoto)
            throw new InvalidOperationException("Message does not contain MessageMediaPhoto.");

        return DownloadPhotoFileAsync(mediaPhoto, filePath, photoSize, progress, cancellationToken);
    }

    public async Task<string> DownloadDocumentAsync(
        MessageMediaDocument mediaDocument,
        Stream outputStream,
        Client.ProgressCallback? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mediaDocument);
        ArgumentNullException.ThrowIfNull(outputStream);

        if (mediaDocument.document is not Document document)
            throw new InvalidOperationException("MessageMediaDocument does not contain a downloadable Document instance.");

        return await ExecuteWithFloodWaitAsync(() => Client.DownloadFileAsync(
            document,
            outputStream,
            (PhotoSizeBase?)null,
            WrapProgress(progress, cancellationToken)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]> DownloadDocumentBytesAsync(
        MessageMediaDocument mediaDocument,
        Client.ProgressCallback? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var memoryStream = new MemoryStream();
        await DownloadDocumentAsync(mediaDocument, memoryStream, progress, cancellationToken).ConfigureAwait(false);
        return memoryStream.ToArray();
    }

    public async Task<string> DownloadDocumentFileAsync(
        MessageMediaDocument mediaDocument,
        string filePath,
        Client.ProgressCallback? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        await using var fileStream = File.Create(filePath);
        return await DownloadDocumentAsync(mediaDocument, fileStream, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> DownloadDocumentAsync(
        Message message,
        Stream outputStream,
        Client.ProgressCallback? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.media is not MessageMediaDocument mediaDocument)
            throw new InvalidOperationException("Message does not contain MessageMediaDocument.");

        return await DownloadDocumentAsync(mediaDocument, outputStream, progress, cancellationToken).ConfigureAwait(false);
    }

    public Task<byte[]> DownloadDocumentBytesAsync(
        Message message,
        Client.ProgressCallback? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.media is not MessageMediaDocument mediaDocument)
            throw new InvalidOperationException("Message does not contain MessageMediaDocument.");

        return DownloadDocumentBytesAsync(mediaDocument, progress, cancellationToken);
    }

    public Task<string> DownloadDocumentFileAsync(
        Message message,
        string filePath,
        Client.ProgressCallback? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.media is not MessageMediaDocument mediaDocument)
            throw new InvalidOperationException("Message does not contain MessageMediaDocument.");

        return DownloadDocumentFileAsync(mediaDocument, filePath, progress, cancellationToken);
    }

    private static bool IsForumChannel(Channel channel)
    {
        return channel.flags.HasFlag(Channel.Flags.forum);
    }

    /// <summary>Implements <see href="https://core.telegram.org/method/channels.getForumTopics"/>: get topics of a forum supergroup.</summary>
    private async Task<IReadOnlyList<TelegramTopicDescriptor>> GetForumTopicsInternalAsync(Channel channel, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inputChannel = new InputChannel(channel.ID, channel.access_hash);
        const int offsetId = 0;
        const int offsetTopic = 0;
        const int limit = 100;

        Messages_ForumTopics forumTopics;
        try
        {
            forumTopics = await ExecuteWithFloodWaitAsync(() => Client.Channels_GetAllForumTopics(inputChannel), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            forumTopics = await ExecuteWithFloodWaitAsync(() => Client.Messages_GetForumTopics(
                channel, default, offsetId, offsetTopic, limit, null), cancellationToken).ConfigureAwait(false);
        }

        var list = new List<TelegramTopicDescriptor>();
        var parentTitle = channel.Title ?? "";
        foreach (var topicBase in forumTopics.topics)
        {
            if (topicBase is ForumTopic topic)
                list.Add(new TelegramTopicDescriptor { ChatId = channel.ID, TopicId = topic.id, Title = $"{parentTitle} | {topic.title}" });
        }
        return list;
    }

    private async Task<IReadOnlyList<TelegramChatDescriptor>> GetDialogDescriptorsAsync(CancellationToken cancellationToken = default)
    {
        var dialogs = await ExecuteWithFloodWaitAsync(() => Client.Messages_GetAllDialogs(), cancellationToken).ConfigureAwait(false);
        return dialogs.Dialogs
            .OfType<Dialog>()
            .Select(dialog => CreateDialogDescriptor(dialogs.UserOrChat(dialog.Peer)))
            .Where(descriptor => descriptor is not null)
            .Select(descriptor => descriptor!)
            .DistinctBy(x => (x.Type, x.Id))
            .ToArray();
    }

    private async Task<(InputPeer Peer, int? TopicTopMessageId)> ResolvePeerByDescriptorAsync(TelegramChatDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        var resolveId = descriptor.ParentChatId ?? descriptor.Id;
        if (descriptor.ParentChatId.HasValue && descriptor.Type == "Topic")
        {
            var dialogs = await ExecuteWithFloodWaitAsync(() => Client.Messages_GetAllDialogs(), cancellationToken).ConfigureAwait(false);
            var peer = dialogs.Dialogs
                .OfType<Dialog>()
                .Select(dialog => dialogs.UserOrChat(dialog.Peer))
                .FirstOrDefault(peer => peer is not null && peer.ID == resolveId);
            var inputPeer = ToInputPeer(peer) ?? throw new InvalidOperationException($"Channel '{descriptor.ParentChatId}' for topic '{descriptor.Title}' could not be resolved.");
            return await CreateResolvedPeerAsync(inputPeer, GetTopicId(descriptor), cancellationToken).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(descriptor.Username))
        {
            var resolved = await ExecuteWithFloodWaitAsync(() => Client.Contacts_ResolveUsername(descriptor.Username), cancellationToken).ConfigureAwait(false);
            if (resolved.Chat is not null)
                return (resolved.Chat, null);
            if (resolved.User is not null)
                return (resolved.User, null);
        }

        var dialogs2 = await ExecuteWithFloodWaitAsync(() => Client.Messages_GetAllDialogs(), cancellationToken).ConfigureAwait(false);
        var peer2 = dialogs2.Dialogs
            .OfType<Dialog>()
            .Select(dialog => dialogs2.UserOrChat(dialog.Peer))
            .FirstOrDefault(peer => peer is not null &&
                                    peer.ID == descriptor.Id &&
                                    string.Equals(GetPeerType(peer), descriptor.Type, StringComparison.OrdinalIgnoreCase));

        var inputPeer2 = ToInputPeer(peer2) ?? throw new InvalidOperationException($"Peer '{descriptor.Title}' could not be resolved.");
        return (inputPeer2, null);
    }

    private static IReadOnlyList<TelegramChatDescriptor> ApplyQueryWindow(
        IEnumerable<TelegramChatDescriptor> source,
        WTelegramChatQuery query,
        int skip,
        int take)
    {
        var sorted = ApplySort(source, query.Sort);
        return sorted
            .DistinctBy(x => (x.Type, x.Id))
            .Skip(skip)
            .Take(take)
            .ToArray();
    }

    private static IEnumerable<TelegramChatDescriptor> ApplySort(
        IEnumerable<TelegramChatDescriptor> source,
        WTelegramChatSort? sort)
    {
        if (sort is null || sort.Operator == SortOperator.Unsorted || string.IsNullOrWhiteSpace(sort.Field))
            return source.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id);

        var desc = sort.Operator == SortOperator.Desc;
        return sort.Field.Trim().ToLowerInvariant() switch
        {
            "id" => desc ? source.OrderByDescending(x => x.Id) : source.OrderBy(x => x.Id),
            "type" => desc
                ? source.OrderByDescending(x => x.Type, StringComparer.OrdinalIgnoreCase).ThenByDescending(x => x.Title, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(x => x.Type, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase),
            "username" => desc
                ? source.OrderByDescending(x => x.Username ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenByDescending(x => x.Title, StringComparer.OrdinalIgnoreCase)
                : source.OrderBy(x => x.Username ?? string.Empty, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase),
            _ => desc
                ? source.OrderByDescending(x => x.Title, StringComparer.OrdinalIgnoreCase).ThenByDescending(x => x.Id)
                : source.OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id)
        };
    }

    private static bool MatchesQuery(TelegramChatDescriptor chat, WTelegramChatQuery query)
    {
        var filter = query.Filter;
        if (filter is null)
            return true;

        if (filter.Id.HasValue && chat.Id != filter.Id.Value)
            return false;
        if (filter.IsActive.HasValue && chat.IsActive != filter.IsActive.Value)
            return false;
        if (!string.IsNullOrWhiteSpace(filter.Type) && !MatchesString(chat.Type, filter.Type, query.FilterOperator))
            return false;
        if (!string.IsNullOrWhiteSpace(filter.Title) && !MatchesString(chat.Title, filter.Title, query.FilterOperator))
            return false;
        if (!string.IsNullOrWhiteSpace(filter.Username) && !MatchesString(chat.Username, filter.Username, query.FilterOperator))
            return false;

        if (!string.IsNullOrWhiteSpace(filter.SearchText))
        {
            var titleMatch = MatchesString(chat.Title, filter.SearchText, query.FilterOperator);
            var usernameMatch = MatchesString(chat.Username, filter.SearchText, query.FilterOperator);
            if (!titleMatch && !usernameMatch)
                return false;
        }

        return true;
    }

    private static bool MatchesString(string? value, string expected, FilterOperator? filterOperator)
    {
        if (string.IsNullOrWhiteSpace(expected))
            return true;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return (filterOperator ?? FilterOperator.Contains) switch
        {
            FilterOperator.Equals => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase),
            FilterOperator.StartsWith => value.StartsWith(expected, StringComparison.OrdinalIgnoreCase),
            FilterOperator.EndsWith => value.EndsWith(expected, StringComparison.OrdinalIgnoreCase),
            FilterOperator.Like or FilterOperator.Contains => value.Contains(expected, StringComparison.OrdinalIgnoreCase),
            FilterOperator.NotEquals => !string.Equals(value, expected, StringComparison.OrdinalIgnoreCase),
            _ => value.Contains(expected, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static InputPeer? ToInputPeer(object? peer) => peer switch
    {
        User user => user,
        ChatBase chat => chat,
        _ => null
    };

    private static TelegramChatDescriptor? CreateDialogDescriptor(object? peer) => peer switch
    {
        User user => new TelegramChatDescriptor
        {
            Id = user.ID,
            Title = user.ToString(),
            Username = user.username,
            Type = GetPeerType(user),
            IsActive = true
        },
        ChatBase chat => CreateChatDescriptor(chat.ID, chat),
        _ => null
    };

    private static string GetPeerType(object peer) => peer switch
    {
        User user when user.flags.HasFlag(User.Flags.bot) => "Bot",
        User => "User",
        _ => peer.GetType().Name
    };

    private static TelegramChatDescriptor CreateChatDescriptor(long id, ChatBase chat)
        => new()
        {
            Id = id,
            Title = chat.Title,
            Username = chat is Channel channel ? channel.username : null,
            Type = GetPeerType(chat),
            IsActive = chat.IsActive
        };

    private async Task<(InputPeer Peer, int? TopicTopMessageId)> CreateResolvedPeerAsync(
        InputPeer peer,
        int? topicId,
        CancellationToken cancellationToken = default)
    {
        if (!topicId.HasValue)
            return (peer, null);

        var topicTopMessageId = await ResolveForumTopicTopMessageIdAsync(peer, topicId.Value, cancellationToken).ConfigureAwait(false);
        return (peer, topicTopMessageId);
    }

    private async Task<int> ResolveForumTopicTopMessageIdAsync(InputPeer peer, int topicId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var forumTopics = await ExecuteWithFloodWaitAsync(() => Client.Channels_GetAllForumTopics(peer), cancellationToken).ConfigureAwait(false);
        var topic = forumTopics.topics
            .OfType<ForumTopic>()
            .FirstOrDefault(candidate => candidate.id == topicId);

        if (topic is null)
            throw new InvalidOperationException($"Forum topic '{topicId}' was not found.");

        // messages.getReplies expects the topic root message id, which is ForumTopic.id.
        return topic.id;
    }

    private static int GetTopicId(TelegramChatDescriptor descriptor)
    {
        if (descriptor.Id is < int.MinValue or > int.MaxValue)
            throw new InvalidOperationException($"Topic id '{descriptor.Id}' is out of Int32 range.");

        return checked((int)descriptor.Id);
    }

    private async Task LoginAsUserAsync(Client client, CancellationToken cancellationToken)
    {
        var loginInfo = GetInitialLoginInfo();

        while (client.User is null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requested = await ExecuteWithFloodWaitAsync(async () => await client.Login(loginInfo), cancellationToken).ConfigureAwait(false);
            if (requested is null)
                break;

            loginInfo = requested switch
            {
                "verification_code" => ResolveValue(_options?.VerificationCode, _options?.VerificationCodeFactory, requested),
                "password" => ResolveValue(_options?.Password, _options?.PasswordFactory, requested),
                "name" => $"{_options?.FirstName} {_options?.LastName}".Trim(),
                "email" => _options?.Email ?? _options?.Config?.Invoke(requested),
                "email_verification_code" => ResolveValue(null, _options?.EmailVerificationCodeFactory, requested),
                _ => _options?.Config?.Invoke(requested)
            };

            if (string.IsNullOrWhiteSpace(loginInfo))
                throw new InvalidOperationException($"WTelegram requested '{requested}', but no value was provided.");
        }
    }

    private string? GetConfigValue(string key)
    {
        var options = _options ?? throw new InvalidOperationException("Open options are not initialized.");

        return key switch
        {
            "api_id" => options.ApiId.ToString(CultureInfo.InvariantCulture),
            "api_hash" => options.ApiHash,
            "phone_number" => options.PhoneNumber,
            "verification_code" => ResolveValue(options.VerificationCode, options.VerificationCodeFactory, key),
            "password" => ResolveValue(options.Password, options.PasswordFactory, key),
            "first_name" => options.FirstName,
            "last_name" => options.LastName,
            "email" => options.Email,
            "email_verification_code" => ResolveValue(null, options.EmailVerificationCodeFactory, key),
            "bot_token" => options.BotToken,
            "session_pathname" => options.SessionPathname,
            _ => options.Config?.Invoke(key)
        };
    }

    private string? GetInitialLoginInfo()
    {
        if (!string.IsNullOrWhiteSpace(_options?.PhoneNumber))
            return _options.PhoneNumber;

        var fromConfig = _options?.Config?.Invoke("phone_number");
        return string.IsNullOrWhiteSpace(fromConfig) ? null : fromConfig;
    }

    private static string? ResolveValue(string? value, Func<string?>? factory, string key)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        var produced = factory?.Invoke();
        if (!string.IsNullOrWhiteSpace(produced))
            return produced;

        return null;
    }

    internal Task<T> ExecuteWithFloodWaitAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
        => ExecuteWithFloodWaitCoreAsync(action, cancellationToken);

    internal async Task DelayBetweenPaginationRequestsAsync(CancellationToken cancellationToken = default)
    {
        await Task.Delay(PaginationThrottleMs, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ExecuteWithFloodWaitCoreAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (RpcException ex) when (TryGetFloodWaitDelay(ex, out var delay))
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool TryGetFloodWaitDelay(RpcException exception, out TimeSpan delay)
    {
        delay = default;

        var message = exception.Message ?? string.Empty;
        var match = FloodWaitRegex.Match(message);
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
            return false;

        delay = TimeSpan.FromSeconds(seconds);
        return true;
    }

    private static Client.ProgressCallback? WrapProgress(Client.ProgressCallback? progress, CancellationToken cancellationToken)
    {
        if (progress is null && !cancellationToken.CanBeCanceled)
            return null;

        return (transmitted, total) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Invoke(transmitted, total);
        };
    }
}
