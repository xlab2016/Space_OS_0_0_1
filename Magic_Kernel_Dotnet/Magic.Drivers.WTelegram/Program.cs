using System.Globalization;
using Data.Repository;
using Microsoft.Extensions.Configuration;
using TL;

namespace Magic.Drivers.WTelegram;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var cli = CliOptions.Parse(args);

        if (cli.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        var settings = LoadSettings();

        var openOptions = BuildOpenOptions(settings);

        await using var connection = new WTelegramConnection();
        await connection.OpenAsync(openOptions).ConfigureAwait(false);

        if (cli.ListChats)
        {
            var chats = await connection.GetChatsAsync().ConfigureAwait(false);

            var chat2 = chats.FirstOrDefault(_ => _.Title == "??? ??? CRM ?????????");

            foreach (var chat in chats.Where(x => x.IsActive))
                Console.WriteLine($"{chat.Id}\t{chat.Type}\t{chat.Title}");

            return 0;
        }

        var request = new TelegramHistoryRequest
        {
            ChannelId = cli.ChannelId ?? settings.History.ChannelId,
            ChannelUsername = cli.ChannelUsername ?? settings.History.ChannelUsername,
            ChannelTitle = cli.ChannelTitle ?? settings.History.ChannelTitle,
            ChannelTitleOperator = cli.ChannelTitleOperator ?? settings.History.ChannelTitleOperator ?? FilterOperator.Contains
        };

        var history = connection.History(request);
        await history.OpenAsync(new TelegramHistoryOpenOptions
        {
            Paging = new TelegramHistoryPaging
            {
                Take = cli.Take ?? settings.History.Take ?? 50,
                OffsetId = cli.OffsetId ?? settings.History.OffsetId ?? 0,
                Order = cli.Order ?? settings.History.Order ?? TelegramHistoryOrder.Desc
            },
            Filter = new TelegramHistoryFilter
            {
                TextContains = cli.TextContains ?? settings.History.FilterText,
                FromId = cli.FromId ?? settings.History.FromId,
                IncludeServiceMessages = cli.IncludeServiceMessages,
                WithMediaOnly = cli.WithMediaOnly
            }
        }).ConfigureAwait(false);

        var totalItems = 0;
        var pageNumber = 0;

        while (true)
        {
            var page = await history.ReadPageAsync().ConfigureAwait(false);
            pageNumber++;

            if (page.Items.Count == 0 && !page.HasMore)
                break;

            Console.WriteLine($"--- page {pageNumber} nextOffsetId={page.NextOffsetId} rawFetched={page.RawMessagesFetched} hasMore={page.HasMore} ---");

            foreach (var item in page.Items)
            {
                Console.WriteLine($"[{item.DateUtc:O}] id={item.Id} from={item.FromTitle} peer={item.PeerTitle} type={item.MessageType} media={item.MediaType ?? "-"}");
                if (!string.IsNullOrWhiteSpace(item.Text))
                    Console.WriteLine(item.Text);

                switch (item.Media)
                {
                    case MessageMediaPhoto mediaPhoto:
                    {
                        var bytes = await connection.DownloadPhotoBytesAsync(mediaPhoto).ConfigureAwait(false);
                        Console.WriteLine($"photo.length={bytes.Length}");
                        break;
                    }
                    case MessageMediaDocument mediaDocument:
                    {
                        var bytes = await connection.DownloadDocumentBytesAsync(mediaDocument).ConfigureAwait(false);
                        Console.WriteLine($"document.length={bytes.Length}");
                        break;
                    }
                }

                Console.WriteLine();
            }

            totalItems += page.Items.Count;

            if (!page.HasMore)
                break;
        }

        Console.WriteLine($"pages={pageNumber} items={totalItems} nextOffsetId={history.NextOffsetId} completed={history.IsCompleted}");
        return 0;
    }

    private static WTelegramOpenOptions BuildOpenOptions(AppSettings settings)
    {
        var apiId = settings.Connection.ApiId;
        if (!apiId.HasValue || apiId.Value <= 0)
            throw new InvalidOperationException("WTelegram:Connection:ApiId must be greater than 0 in appsettings.json.");
        if (string.IsNullOrWhiteSpace(settings.Connection.ApiHash))
            throw new InvalidOperationException("WTelegram:Connection:ApiHash is required in appsettings.json.");

        return new WTelegramOpenOptions
        {
            ApiId = apiId.Value,
            ApiHash = settings.Connection.ApiHash,
            PhoneNumber = settings.Connection.PhoneNumber,
            Password = settings.Connection.Password,
            SessionPathname = settings.Connection.SessionPath,
            VerificationCodeFactory = ReadVerificationCode,
            PasswordFactory = ReadPassword
        };
    }

    private static AppSettings LoadSettings()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        return configuration.GetSection("WTelegram").Get<AppSettings>()
            ?? throw new InvalidOperationException("WTelegram section is missing in appsettings.json.");
    }

    private static string? ReadVerificationCode()
    {
        Console.Write("verification_code: ");
        return Console.ReadLine();
    }

    private static string? ReadPassword()
    {
        Console.Write("password: ");
        return Console.ReadLine();
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Magic.Drivers.WTelegram playground");
        Console.WriteLine("Commands:");
        Console.WriteLine("  --list-chats");
        Console.WriteLine("  --channel-id <id> [--take <n>] [--offset-id <id>] [--order asc|desc] [--text <contains>] [--from-id <id>] [--with-service] [--with-media]");
        Console.WriteLine("  --channel <username> [same filters]");
        Console.WriteLine("  --channel-title <title> [--channel-title-op contains|equals] [same filters]");
        Console.WriteLine();
        Console.WriteLine("Config: appsettings.json / WTelegram");
        Console.WriteLine("  Connection: ApiId, ApiHash, PhoneNumber, Password, SessionPath");
        Console.WriteLine("  History: ChannelId, ChannelUsername, ChannelTitle, ChannelTitleOperator, Take, OffsetId, Order, FilterText, FromId");
    }

    private sealed class CliOptions
    {
        public bool ShowHelp { get; private set; }
        public bool ListChats { get; private set; }
        public long? ChannelId { get; private set; }
        public string? ChannelUsername { get; private set; }
        public string? ChannelTitle { get; private set; }
        public FilterOperator? ChannelTitleOperator { get; private set; }
        public int? Take { get; private set; }
        public int? OffsetId { get; private set; }
        public TelegramHistoryOrder? Order { get; private set; }
        public string? TextContains { get; private set; }
        public long? FromId { get; private set; }
        public bool IncludeServiceMessages { get; private set; }
        public bool WithMediaOnly { get; private set; }

        public static CliOptions Parse(IReadOnlyList<string> args)
        {
            var options = new CliOptions();

            for (var i = 0; i < args.Count; i++)
            {
                switch (args[i])
                {
                    case "--help":
                    case "-h":
                        options.ShowHelp = true;
                        break;
                    case "--list-chats":
                        options.ListChats = true;
                        break;
                    case "--channel-id":
                        options.ChannelId = long.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--channel":
                        options.ChannelUsername = args[++i];
                        break;
                    case "--channel-title":
                        options.ChannelTitle = args[++i];
                        break;
                    case "--channel-title-op":
                        options.ChannelTitleOperator = ParseFilterOperator(args[++i]);
                        break;
                    case "--take":
                        options.Take = int.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--offset-id":
                        options.OffsetId = int.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--order":
                        options.Order = Enum.Parse<TelegramHistoryOrder>(args[++i], ignoreCase: true);
                        break;
                    case "--text":
                        options.TextContains = args[++i];
                        break;
                    case "--from-id":
                        options.FromId = long.Parse(args[++i], CultureInfo.InvariantCulture);
                        break;
                    case "--with-service":
                        options.IncludeServiceMessages = true;
                        break;
                    case "--with-media":
                        options.WithMediaOnly = true;
                        break;
                }
            }

            return options;
        }

        private static FilterOperator ParseFilterOperator(string value)
        {
            return value.Trim().ToLowerInvariant() switch
            {
                "contains" => FilterOperator.Contains,
                "equals" => FilterOperator.Equals,
                _ => throw new InvalidOperationException($"Unsupported channel title operator '{value}'. Use contains or equals.")
            };
        }
    }

    private sealed class AppSettings
    {
        public ConnectionSettings Connection { get; init; } = new();
        public HistorySettings History { get; init; } = new();
    }

    private sealed class ConnectionSettings
    {
        public int? ApiId { get; init; }
        public string? ApiHash { get; init; }
        public string? PhoneNumber { get; init; }
        public string? Password { get; init; }
        public string? SessionPath { get; init; }
    }

    private sealed class HistorySettings
    {
        public long? ChannelId { get; init; }
        public string? ChannelUsername { get; init; }
        public string? ChannelTitle { get; init; }
        public FilterOperator? ChannelTitleOperator { get; init; }
        public int? Take { get; init; }
        public int? OffsetId { get; init; }
        public TelegramHistoryOrder? Order { get; init; }
        public string? FilterText { get; init; }
        public long? FromId { get; init; }
    }
}
