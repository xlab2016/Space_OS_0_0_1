using System.Globalization;
using Magic.Drivers.WTelegram;
using Magic.Kernel.Devices;
using Magic.Kernel.Devices.Streams.Drivers;
using Magic.Kernel.Interpretation;

namespace Magic.Kernel.Devices.Streams
{
    /// <summary>Telegram client stream backed by WTelegram (user session, channel history access).</summary>
    public sealed class WTelegramStreamDevice : DefStream
    {
        private readonly WTelegramDriver _driver = new();

        public override async Task<object?> CallObjAsync(string methodName, object?[] args)
        {
            var name = methodName?.Trim() ?? "";
            if (string.Equals(name, "open", StringComparison.OrdinalIgnoreCase))
            {
                var options = CreateOpenOptions(args?.Length > 0 ? args[0] : null);
                var result = await _driver.OpenAsync(options).ConfigureAwait(false);
                if (!result.IsSuccess)
                    throw new InvalidOperationException(result.ErrorMessage ?? "Failed to open WTelegram client.");
                return this;
            }

            if (string.Equals(name, "history", StringComparison.OrdinalIgnoreCase))
            {
                EnsureOpened();
                return CreateHistoryDevice(args?.Length > 0 ? args[0] : null);
            }

            if (string.Equals(name, "close", StringComparison.OrdinalIgnoreCase))
            {
                await CloseAsync().ConfigureAwait(false);
                return this;
            }

            throw new CallUnknownMethodException(name, this);
        }

        public override Task<DeviceOperationResult> OpenAsync()
            => Task.FromResult(_driver.IsOpened
                ? DeviceOperationResult.Success
                : DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Use open({ api_id, api_hash, phoneNumber })"));

        public override Task<(DeviceOperationResult Result, byte[] Bytes)> ReadAsync()
            => Task.FromResult((DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Use history(...) for WTelegram client streams"), Array.Empty<byte>()));

        public override Task<DeviceOperationResult> WriteAsync(byte[] bytes)
            => Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Write is not implemented for WTelegram client streams"));

        public override Task<DeviceOperationResult> ControlAsync(DeviceControlBase deviceControl)
            => Task.FromResult(DeviceOperationResult.Success);

        public override async Task<DeviceOperationResult> CloseAsync()
        {
            await _driver.DisposeAsync().ConfigureAwait(false);
            return DeviceOperationResult.Success;
        }

        public override Task<(DeviceOperationResult Result, IStreamChunk? Chunk)> ReadChunkAsync()
            => Task.FromResult((DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Use history(...) for WTelegram client streams"), (IStreamChunk?)null));

        public override Task<DeviceOperationResult> WriteChunkAsync(IStreamChunk chunk)
            => Task.FromResult(DeviceOperationResult.Fail(DeviceOperationState.InvalidState, "Write is not implemented for WTelegram client streams"));

        public override Task<DeviceOperationResult> MoveAsync(StructurePosition? position)
            => Task.FromResult(DeviceOperationResult.Success);

        public override Task<(DeviceOperationResult Result, long Length)> LengthAsync()
            => Task.FromResult((DeviceOperationResult.Success, 0L));

        private void EnsureOpened()
        {
            if (!_driver.IsOpened)
                throw new InvalidOperationException("WTelegram client is not opened. Call stream.open(...) first.");
        }

        private WTelegramHistoryStreamDevice CreateHistoryDevice(object? options)
        {
            var request = CreateHistoryRequest(options);
            var openOptions = CreateHistoryOpenOptions(options);
            return new WTelegramHistoryStreamDevice(_driver.Connection, request, openOptions);
        }

        private static WTelegramOpenOptions CreateOpenOptions(object? options)
        {
            if (options is not IDictionary<string, object?> dict)
                throw new InvalidOperationException("WTelegram open requires object options: { api_id, api_hash, phoneNumber }.");

            var apiId = RequireInt(dict, "api_id", "apiId");
            var apiHash = RequireString(dict, "api_hash", "apiHash");

            return new WTelegramOpenOptions
            {
                ApiId = apiId,
                ApiHash = apiHash,
                PhoneNumber = GetString(dict, "phoneNumber", "phone_number"),
                VerificationCode = GetString(dict, "verificationCode", "verification_code"),
                VerificationCodeFactory = () => PromptFromConsoleIfAvailable("verification_code"),
                Password = GetString(dict, "password"),
                PasswordFactory = () => PromptFromConsoleIfAvailable("password"),
                FirstName = GetString(dict, "firstName", "first_name"),
                LastName = GetString(dict, "lastName", "last_name"),
                Email = GetString(dict, "email"),
                EmailVerificationCodeFactory = () => PromptFromConsoleIfAvailable("email_verification_code"),
                BotToken = GetString(dict, "botToken", "bot_token"),
                SessionPathname = GetString(dict, "sessionPath", "session_path", "sessionPathname")
            };
        }

        private static TelegramHistoryRequest CreateHistoryRequest(object? options)
        {
            var root = options as IDictionary<string, object?>;
            var filter = GetDictionary(root, "filter");

            var channelId = TryGetLong(filter, "channelId", "channel_id")
                ?? TryGetLong(root, "channelId", "channel_id");
            var channelUsername = GetString(filter, "channelUsername", "channel_username")
                ?? GetString(root, "channelUsername", "channel_username");
            var channelTitle = GetString(filter, "channelTitle", "channel_title")
                ?? GetString(root, "channelTitle", "channel_title");
            var topicId = TryGetInt(filter, "topicId", "topic_id") ?? TryGetInt(root, "topicId", "topic_id");

            return new TelegramHistoryRequest
            {
                ChannelId = channelId,
                ChannelUsername = channelUsername,
                ChannelTitle = channelTitle,
                ChannelTitleOperator = ParseFilterOperator(
                    GetString(filter, "channelTitleOperator", "channel_title_operator")
                    ?? GetString(root, "channelTitleOperator", "channel_title_operator")),
                TopicId = topicId
            };
        }

        private static TelegramHistoryOpenOptions CreateHistoryOpenOptions(object? options)
        {
            var root = options as IDictionary<string, object?>;
            var filter = GetDictionary(root, "filter");
            var paging = GetDictionary(root, "paging");

            return new TelegramHistoryOpenOptions
            {
                Paging = new TelegramHistoryPaging
                {
                    Take = TryGetInt(paging, "take") ?? TryGetInt(root, "take") ?? 50,
                    // Поддерживаем и offsetId, и skip (из AGI-кода: skip: offsetId).
                    OffsetId = TryGetInt(paging, "offsetId", "offset_id", "skip")
                               ?? TryGetInt(root, "offsetId", "offset_id", "skip")
                               ?? 0,
                    AddOffset = TryGetInt(paging, "addOffset", "add_offset") ?? TryGetInt(root, "addOffset", "add_offset") ?? 0,
                    MaxId = TryGetInt(paging, "maxId", "max_id") ?? TryGetInt(root, "maxId", "max_id") ?? 0,
                    MinId = TryGetInt(paging, "minId", "min_id") ?? TryGetInt(root, "minId", "min_id") ?? 0,
                    Order = ParseOrder(GetString(paging, "order") ?? GetString(root, "order"))
                },
                Filter = new TelegramHistoryFilter
                {
                    TextContains = GetString(filter, "textContains", "text_contains")
                        ?? GetString(root, "textContains", "text_contains"),
                    FromId = TryGetLong(filter, "fromId", "from_id")
                        ?? TryGetLong(root, "fromId", "from_id"),
                    SinceUtc = TryGetDateTime(filter, "sinceUtc", "since_utc")
                        ?? TryGetDateTime(root, "sinceUtc", "since_utc"),
                    UntilUtc = TryGetDateTime(filter, "untilUtc", "until_utc")
                        ?? TryGetDateTime(root, "untilUtc", "until_utc"),
                    IncludeServiceMessages = TryGetBool(filter, "includeServiceMessages", "include_service_messages")
                        ?? TryGetBool(root, "includeServiceMessages", "include_service_messages")
                        ?? false,
                    WithMediaOnly = TryGetBool(filter, "withMediaOnly", "with_media_only")
                        ?? TryGetBool(root, "withMediaOnly", "with_media_only")
                        ?? false
                }
            };
        }

        private static TelegramHistoryOrder ParseOrder(string? raw)
        {
            return string.Equals(raw, "asc", StringComparison.OrdinalIgnoreCase)
                ? TelegramHistoryOrder.Asc
                : TelegramHistoryOrder.Desc;
        }

        private static global::Data.Repository.FilterOperator? ParseFilterOperator(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            return raw.Trim().ToLowerInvariant() switch
            {
                "equals" => global::Data.Repository.FilterOperator.Equals,
                "startswith" => global::Data.Repository.FilterOperator.StartsWith,
                "endswith" => global::Data.Repository.FilterOperator.EndsWith,
                "like" => global::Data.Repository.FilterOperator.Like,
                "notequals" => global::Data.Repository.FilterOperator.NotEquals,
                _ => global::Data.Repository.FilterOperator.Contains
            };
        }

        private static IDictionary<string, object?>? GetDictionary(IDictionary<string, object?>? source, params string[] keys)
        {
            var value = GetValue(source, keys);
            return value as IDictionary<string, object?>;
        }

        private static string RequireString(IDictionary<string, object?> source, params string[] keys)
            => GetString(source, keys) ?? throw new InvalidOperationException($"Missing required option '{keys[0]}'.");

        private static int RequireInt(IDictionary<string, object?> source, params string[] keys)
            => TryGetInt(source, keys) ?? throw new InvalidOperationException($"Missing required option '{keys[0]}'.");

        private static string? GetString(IDictionary<string, object?>? source, params string[] keys)
            => GetValue(source, keys)?.ToString();

        private static int? TryGetInt(IDictionary<string, object?>? source, params string[] keys)
        {
            var value = GetValue(source, keys);
            if (value is null)
                return null;
            if (value is int i)
                return i;
            if (value is long l && l is >= int.MinValue and <= int.MaxValue)
                return (int)l;
            return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static long? TryGetLong(IDictionary<string, object?>? source, params string[] keys)
        {
            var value = GetValue(source, keys);
            if (value is null)
                return null;
            if (value is long l)
                return l;
            if (value is int i)
                return i;
            return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
        }

        private static bool? TryGetBool(IDictionary<string, object?>? source, params string[] keys)
        {
            var value = GetValue(source, keys);
            if (value is null)
                return null;
            if (value is bool b)
                return b;
            return bool.TryParse(value.ToString(), out var parsed) ? parsed : null;
        }

        private static DateTime? TryGetDateTime(IDictionary<string, object?>? source, params string[] keys)
        {
            var value = GetValue(source, keys);
            if (value is null)
                return null;
            if (value is DateTime dateTime)
                return dateTime;
            return DateTime.TryParse(value.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
        }

        private static object? GetValue(IDictionary<string, object?>? source, params string[] keys)
        {
            if (source is null)
                return null;

            foreach (var key in keys)
            {
                if (source.TryGetValue(key, out var direct))
                    return direct;

                foreach (var pair in source)
                {
                    if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                        return pair.Value;
                }
            }

            return null;
        }

        private static string? PromptFromConsoleIfAvailable(string fieldName)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
                return null;

            try
            {
                if (!Environment.UserInteractive)
                    return null;

                while (true)
                {
                    Console.Write($"{fieldName}: ");
                    var value = string.Equals(fieldName, "password", StringComparison.OrdinalIgnoreCase)
                        ? ReadPasswordFromConsole()
                        : Console.ReadLine();

                    if (value == null)
                        return null;

                    var trimmed = value.Trim();
                    if (trimmed.Length > 0)
                        return trimmed;

                    Console.WriteLine($"{fieldName} is required. Press Ctrl+C to cancel.");
                }
            }
            catch (InvalidOperationException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
        }

        private static string? ReadPasswordFromConsole()
        {
            if (Console.IsInputRedirected)
                return Console.ReadLine();

            var chars = new List<char>();
            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return new string(chars.ToArray());
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (chars.Count == 0)
                        continue;

                    chars.RemoveAt(chars.Count - 1);
                    Console.Write("\b \b");
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    chars.Add(key.KeyChar);
                    Console.Write('*');
                }
            }
        }
    }
}
