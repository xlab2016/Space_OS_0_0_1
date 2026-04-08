using TL;

namespace Magic.Drivers.WTelegram;

public sealed class WTelegramHistoryStream
{
    private readonly WTelegramConnection _connection;
    private readonly TelegramHistoryRequest _request;
    private InputPeer? _peer;
    private int? _topicTopMessageId;
    private TelegramHistoryOpenOptions _options = new();
    private int _nextOffsetId;
    private bool _opened;
    private bool _completed;
    private Queue<TelegramHistoryPage>? _ascendingPages;

    internal WTelegramHistoryStream(WTelegramConnection connection, TelegramHistoryRequest request)
    {
        _connection = connection;
        _request = request;
    }

    public bool IsOpened => _opened;
    public bool IsCompleted => _completed;
    public int NextOffsetId => _nextOffsetId;

    public async Task OpenAsync(TelegramHistoryOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        _options = options ?? new TelegramHistoryOpenOptions();
        var resolvedPeer = await _connection.ResolvePeerAsync(_request, cancellationToken).ConfigureAwait(false);
        _peer = resolvedPeer.Peer;
        _topicTopMessageId = resolvedPeer.TopicTopMessageId;
        _nextOffsetId = _options.Paging.OffsetId;
        _opened = true;
        _completed = false;
        _ascendingPages = null;

        if (_options.Paging.Order == TelegramHistoryOrder.Asc)
            _ascendingPages = await BuildAscendingPagesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TelegramHistoryPage> ReadPageAsync(CancellationToken cancellationToken = default)
    {
        EnsureOpened();

        if (_completed)
        {
            return new TelegramHistoryPage
            {
                Items = Array.Empty<TelegramHistoryMessage>(),
                HasMore = false,
                NextOffsetId = _nextOffsetId,
                RawMessagesFetched = 0
            };
        }

        if (_options.Paging.Order == TelegramHistoryOrder.Asc)
            return ReadAscendingPage();

        var peer = _peer!;
        var take = Math.Clamp(_options.Paging.Take, 1, 200);
        var items = new List<TelegramHistoryMessage>(take);
        var offsetId = _nextOffsetId;
        var rawMessagesFetched = 0;
        var hasMore = false;

        while (items.Count < take)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchSize = Math.Clamp(Math.Max(take * 2, 50), 1, 100);
            var history = await ReadHistoryBatchAsync(peer, offsetId, batchSize, cancellationToken).ConfigureAwait(false);

            if (history.Messages.Length == 0)
            {
                hasMore = false;
                break;
            }

            rawMessagesFetched += history.Messages.Length;

            foreach (var messageBase in history.Messages)
            {
                var message = TelegramHistoryMessage.From(messageBase, history);
                if (!_options.Filter.Matches(message))
                    continue;

                items.Add(message);
                if (items.Count >= take)
                    break;
            }

            offsetId = history.Messages[^1].ID;
            hasMore = history.Messages.Length == batchSize;

            if (!hasMore)
                break;

            await _connection.DelayBetweenPaginationRequestsAsync(cancellationToken).ConfigureAwait(false);
        }

        _nextOffsetId = offsetId;
        _completed = !hasMore;

        return new TelegramHistoryPage
        {
            Items = items,
            HasMore = hasMore,
            NextOffsetId = _nextOffsetId,
            RawMessagesFetched = rawMessagesFetched
        };
    }

    public Task CloseAsync()
    {
        _peer = null;
        _topicTopMessageId = null;
        _opened = false;
        _completed = true;
        _nextOffsetId = 0;
        _options = new TelegramHistoryOpenOptions();
        _ascendingPages = null;
        return Task.CompletedTask;
    }

    private void EnsureOpened()
    {
        if (!_opened || _peer is null)
            throw new InvalidOperationException("History stream is not open. Call OpenAsync first.");
    }

    private TelegramHistoryPage ReadAscendingPage()
    {
        var pages = _ascendingPages ?? new Queue<TelegramHistoryPage>();
        if (pages.Count == 0)
        {
            _completed = true;
            return new TelegramHistoryPage
            {
                Items = Array.Empty<TelegramHistoryMessage>(),
                HasMore = false,
                NextOffsetId = _nextOffsetId,
                RawMessagesFetched = 0
            };
        }

        var page = pages.Dequeue();
        _nextOffsetId = page.NextOffsetId;
        _completed = pages.Count == 0;

        return new TelegramHistoryPage
        {
            Items = page.Items,
            HasMore = !_completed,
            NextOffsetId = _nextOffsetId,
            RawMessagesFetched = page.RawMessagesFetched
        };
    }

    // Telegram returns history newest-first. For oldest-first paging, we prefetch matching
    // pages in descending order once, then reverse them into ascending pages.
    private async Task<Queue<TelegramHistoryPage>> BuildAscendingPagesAsync(CancellationToken cancellationToken)
    {
        var descendingMessages = new List<TelegramHistoryMessage>();
        var take = Math.Clamp(_options.Paging.Take, 1, 200);
        var offsetId = _options.Paging.OffsetId;
        var rawMessagesFetched = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchSize = Math.Clamp(Math.Max(take * 2, 50), 1, 100);
            var history = await ReadHistoryBatchAsync(_peer!, offsetId, batchSize, cancellationToken).ConfigureAwait(false);

            if (history.Messages.Length == 0)
                break;

            rawMessagesFetched += history.Messages.Length;

            foreach (var messageBase in history.Messages)
            {
                var message = TelegramHistoryMessage.From(messageBase, history);
                if (_options.Filter.Matches(message))
                    descendingMessages.Add(message);
            }

            offsetId = history.Messages[^1].ID;
            if (history.Messages.Length < batchSize)
                break;

            await _connection.DelayBetweenPaginationRequestsAsync(cancellationToken).ConfigureAwait(false);
        }

        descendingMessages.Reverse();

        var pages = new Queue<TelegramHistoryPage>();
        for (var i = 0; i < descendingMessages.Count; i += take)
        {
            var items = descendingMessages.Skip(i).Take(take).ToArray();
            var nextOffsetId = items.Length == 0 ? 0 : items[^1].Id;
            pages.Enqueue(new TelegramHistoryPage
            {
                Items = items,
                HasMore = i + take < descendingMessages.Count,
                NextOffsetId = nextOffsetId,
                RawMessagesFetched = rawMessagesFetched
            });
        }

        _nextOffsetId = pages.Count > 0 ? pages.Peek().NextOffsetId : offsetId;
        return pages;
    }

    private Task<Messages_MessagesBase> ReadHistoryBatchAsync(
        InputPeer peer,
        int offsetId,
        int batchSize,
        CancellationToken cancellationToken)
    {
        if (_topicTopMessageId.HasValue)
        {
            return _connection.ExecuteWithFloodWaitAsync(() => _connection.Client.Messages_GetReplies(
                peer,
                _topicTopMessageId.Value,
                offset_id: offsetId,
                offset_date: default,
                add_offset: _options.Paging.AddOffset,
                limit: batchSize,
                max_id: _options.Paging.MaxId,
                min_id: _options.Paging.MinId,
                hash: 0), cancellationToken);
        }

        return _connection.ExecuteWithFloodWaitAsync(() => _connection.Client.Messages_GetHistory(
            peer,
            offset_id: offsetId,
            add_offset: _options.Paging.AddOffset,
            limit: batchSize,
            max_id: _options.Paging.MaxId,
            min_id: _options.Paging.MinId), cancellationToken);
    }
}
