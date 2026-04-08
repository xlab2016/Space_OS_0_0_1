using Data.Repository;

namespace Magic.Drivers.WTelegram;

public sealed class WTelegramChatQuery : QueryBase<TelegramChatDescriptor, WTelegramChatFilter, WTelegramChatSort>
{
    public WTelegramChatQuery()
    {
        Paging = new Paging();
        Filter = new WTelegramChatFilter();
        Sort = new WTelegramChatSort();
        Includes = new List<string>();
        Options = new QueryOptions();
    }
}

public sealed class WTelegramChatFilter : FilterBase<TelegramChatDescriptor>
{
    public long? Id { get; set; }
    public string? Username { get; set; }
    public string? Title { get; set; }
    public string? SearchText { get; set; }
    public string? Type { get; set; }
    public bool? IsActive { get; set; }
}

public sealed class WTelegramChatSort : SortBase<TelegramChatDescriptor>
{
    public string? Field { get; set; }
    public SortOperator Operator { get; set; } = SortOperator.Unsorted;
}
