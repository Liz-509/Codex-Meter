using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexMeter.Windows.Services;

internal sealed record DailyTokenStats(string Date, long Tokens);

internal sealed record ConversationStats(
    string TurnId,
    string? ThreadId,
    string? ContextWindowId,
    DateTimeOffset StartedAt,
    string Preview,
    long? Tokens);

internal sealed record SessionStats(
    int Questions,
    long Tokens,
    IReadOnlyList<DailyTokenStats> DailyTokens,
    IReadOnlyList<ConversationStats> Conversations)
{
    public SessionStats(int questions, long tokens)
        : this(questions, tokens, [], [])
    {
    }
}

internal sealed class SessionStatsCache
{
    private sealed record CachedFile(long Length, long LastWriteTicks, SessionStats Stats);

    private readonly object _gate = new();
    private readonly Dictionary<string, CachedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, DateTimeOffset, TimeZoneInfo, SessionStats> _readFile;
    private string? _windowKey;

    public SessionStatsCache(Func<string, DateTimeOffset, TimeZoneInfo, SessionStats>? readFile = null)
    {
        _readFile = readFile ?? SessionStatsReader.ReadFileContribution;
    }

    public SessionStats ReadToday(string sessionsDirectory, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        lock (_gate)
        {
            var localToday = TimeZoneInfo.ConvertTime(now, timeZone).Date;
            var windowKey = $"{timeZone.Id}|{localToday:yyyy-MM-dd}";
            if (!string.Equals(_windowKey, windowKey, StringComparison.Ordinal))
            {
                _files.Clear();
                _windowKey = windowKey;
            }

            var dayKeys = Enumerable.Range(0, 7)
                .Select(offset => localToday.AddDays(offset - 6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .ToArray();
            var dailyTokens = dayKeys.ToDictionary(key => key, _ => 0L, StringComparer.Ordinal);
            if (!Directory.Exists(sessionsDirectory))
            {
                _files.Clear();
                return BuildResult(dayKeys, dailyTokens, []);
            }

            var historyStartUtc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(localToday.AddDays(-6), DateTimeKind.Unspecified),
                timeZone);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var conversations = new Dictionary<string, ConversationStats>(StringComparer.Ordinal);

            try
            {
                foreach (var path in Directory.EnumerateFiles(sessionsDirectory, "*.jsonl", SearchOption.AllDirectories))
                {
                    FileInfo info;
                    try
                    {
                        info = new FileInfo(path);
                        if (info.LastWriteTimeUtc < historyStartUtc) continue;
                    }
                    catch (IOException)
                    {
                        continue;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        continue;
                    }

                    seen.Add(path);
                    if (!_files.TryGetValue(path, out var cached) ||
                        cached.Length != info.Length ||
                        cached.LastWriteTicks != info.LastWriteTimeUtc.Ticks)
                    {
                        var stats = _readFile(path, now, timeZone);
                        cached = new CachedFile(info.Length, info.LastWriteTimeUtc.Ticks, stats);
                        _files[path] = cached;
                    }

                    foreach (var day in cached.Stats.DailyTokens)
                    {
                        if (dailyTokens.ContainsKey(day.Date)) dailyTokens[day.Date] += day.Tokens;
                    }
                    foreach (var conversation in cached.Stats.Conversations)
                    {
                        conversations[conversation.TurnId] = conversation;
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            foreach (var stalePath in _files.Keys.Where(path => !seen.Contains(path)).ToArray())
            {
                _files.Remove(stalePath);
            }

            return BuildResult(dayKeys, dailyTokens, conversations.Values);
        }
    }

    private static SessionStats BuildResult(
        IReadOnlyList<string> dayKeys,
        IReadOnlyDictionary<string, long> dailyTokens,
        IEnumerable<ConversationStats> conversations)
    {
        var orderedConversations = conversations
            .OrderByDescending(item => item.StartedAt)
            .ToArray();
        var days = dayKeys.Select(key => new DailyTokenStats(key, dailyTokens.GetValueOrDefault(key))).ToArray();
        return new SessionStats(orderedConversations.Length, days[^1].Tokens, days, orderedConversations);
    }
}

internal static partial class SessionStatsReader
{
    private const int HistoryDays = 7;
    private const int PreviewLimit = 160;

    private sealed class ConversationBuilder(
        string turnId,
        string? threadId,
        string? contextWindowId,
        DateTimeOffset startedAt)
    {
        public string TurnId { get; } = turnId;
        public string? ThreadId { get; } = threadId;
        public string? ContextWindowId { get; } = contextWindowId;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public string Preview { get; set; } = "未命名对话";
        public long? Tokens { get; set; }
    }

    public static SessionStats ReadToday(string sessionsDirectory, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var localDates = Enumerable.Range(0, HistoryDays)
            .Select(offset => localNow.Date.AddDays(offset - (HistoryDays - 1)))
            .ToArray();
        var dailyTokens = localDates.ToDictionary(
            date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => 0L);
        var todayKey = localDates[^1].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (!Directory.Exists(sessionsDirectory))
        {
            return CreateResult(dailyTokens, todayKey, []);
        }

        var historyStart = DateTime.SpecifyKind(localDates[0], DateTimeKind.Unspecified);
        var historyStartUtc = TimeZoneInfo.ConvertTimeToUtc(historyStart, timeZone);
        var conversations = new Dictionary<string, ConversationBuilder>(StringComparer.Ordinal);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(sessionsDirectory, "*.jsonl", SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            return CreateResult(dailyTokens, todayKey, []);
        }
        catch (UnauthorizedAccessException)
        {
            return CreateResult(dailyTokens, todayKey, []);
        }

        foreach (var file in files)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < historyStartUtc) continue;
                ReadFile(file, timeZone, todayKey, dailyTokens, conversations);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return CreateResult(dailyTokens, todayKey, conversations.Values);
    }

    internal static SessionStats ReadFileContribution(
        string file,
        DateTimeOffset now,
        TimeZoneInfo timeZone)
    {
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var dayKeys = Enumerable.Range(0, HistoryDays)
            .Select(offset => localNow.Date.AddDays(offset - (HistoryDays - 1)))
            .ToArray();
        var dailyTokens = dayKeys.ToDictionary(
            date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            _ => 0L);
        var todayKey = dayKeys[^1].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var conversations = new Dictionary<string, ConversationBuilder>(StringComparer.Ordinal);

        try
        {
            ReadFile(file, timeZone, todayKey, dailyTokens, conversations);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return CreateResult(dailyTokens, todayKey, conversations.Values);
    }

    private static void ReadFile(
        string file,
        TimeZoneInfo timeZone,
        string todayKey,
        IDictionary<string, long> dailyTokens,
        IDictionary<string, ConversationBuilder> conversations)
    {
        string? threadId = null;
        string? contextWindowId = null;
        string? currentTurnId = null;
        var includeConversations = true;
        long previousTotal = 0;
        var usageRecordSinceTokenCount = false;

        using var stream = new FileStream(
            file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                var rootType = ReadString(root, "type");
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (rootType == "session_meta")
                {
                    threadId = ReadString(payload, "id") ?? threadId;
                    includeConversations = IsUserConversationSession(payload);
                    if (payload.TryGetProperty("context_window", out var contextWindow))
                    {
                        contextWindowId = ReadString(contextWindow, "window_id") ?? contextWindowId;
                    }
                    continue;
                }

                if (rootType == "event_msg")
                {
                    var eventType = ReadString(payload, "type");
                    if (eventType == "task_started")
                    {
                        currentTurnId = ReadString(payload, "turn_id") ?? Guid.NewGuid().ToString("N");
                        if (!TryReadTimestamp(root, out var startedAt)) continue;
                        if (includeConversations && LocalDateKey(startedAt, timeZone) == todayKey)
                        {
                            conversations[currentTurnId] = new ConversationBuilder(
                                currentTurnId,
                                threadId,
                                contextWindowId,
                                startedAt);
                        }
                        continue;
                    }

                    if (eventType == "user_message" && currentTurnId is not null &&
                        conversations.TryGetValue(currentTurnId, out var legacyConversation))
                    {
                        var preview = ReadString(payload, "message");
                        if (!string.IsNullOrWhiteSpace(preview)) legacyConversation.Preview = NormalizePreview(preview);
                        continue;
                    }

                    if (eventType != "token_count" || !TryReadLegacyTotal(payload, out var total)) continue;
                    var delta = total >= previousTotal ? total - previousTotal : total;
                    previousTotal = total;
                    if (usageRecordSinceTokenCount)
                    {
                        usageRecordSinceTokenCount = false;
                        continue;
                    }
                    if (!TryReadTimestamp(root, out var tokenAt)) continue;
                    AddDailyTokens(dailyTokens, LocalDateKey(tokenAt, timeZone), delta);
                    AddConversationTokens(conversations, currentTurnId, delta);
                    continue;
                }

                if (rootType == "token_usage_record")
                {
                    var turnId = ReadString(payload, "turn_id") ?? currentTurnId;
                    var countedUsageRecord = false;
                    if (payload.TryGetProperty("usage", out var usage) &&
                        TryReadInt64(usage, "total_tokens", out var responseTokens) &&
                        TryReadTimestamp(root, out var usageAt))
                    {
                        AddDailyTokens(dailyTokens, LocalDateKey(usageAt, timeZone), responseTokens);
                        countedUsageRecord = true;
                    }
                    if (turnId is not null && conversations.TryGetValue(turnId, out var conversation) &&
                        payload.TryGetProperty("turn_token_usage", out var turnUsage) &&
                        TryReadInt64(turnUsage, "total_tokens", out var turnTokens))
                    {
                        conversation.Tokens = turnTokens;
                    }
                    usageRecordSinceTokenCount = countedUsageRecord;
                    continue;
                }

                if (rootType == "response_item")
                {
                    ReadUserPreview(payload, currentTurnId, conversations);
                }
            }
        }
    }

    private static void ReadUserPreview(
        JsonElement payload,
        string? currentTurnId,
        IDictionary<string, ConversationBuilder> conversations)
    {
        if (ReadString(payload, "type") != "message" || ReadString(payload, "role") != "user") return;
        var turnId = currentTurnId;
        var isUserText = false;
        if (payload.TryGetProperty("internal_chat_message_metadata_passthrough", out var metadata) &&
            metadata.ValueKind == JsonValueKind.Object)
        {
            turnId = ReadString(metadata, "turn_id") ?? turnId;
            if (metadata.TryGetProperty("content_item_kinds", out var kinds) && kinds.ValueKind == JsonValueKind.Array)
            {
                isUserText = kinds.EnumerateArray().Any(item =>
                    item.ValueKind == JsonValueKind.String && item.GetString() == "user.text");
            }
        }
        if (!isUserText || turnId is null || !conversations.TryGetValue(turnId, out var conversation)) return;
        if (!payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;

        var text = string.Join(" ", content.EnumerateArray()
            .Where(item => ReadString(item, "type") is "input_text" or "text")
            .Select(item => ReadString(item, "text"))
            .Where(item => !string.IsNullOrWhiteSpace(item)));
        if (!string.IsNullOrWhiteSpace(text)) conversation.Preview = NormalizePreview(text);
    }

    private static SessionStats CreateResult(
        IReadOnlyDictionary<string, long> dailyTokens,
        string todayKey,
        IEnumerable<ConversationBuilder> conversationBuilders)
    {
        var days = dailyTokens
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new DailyTokenStats(pair.Key, pair.Value))
            .ToArray();
        var conversations = conversationBuilders
            .OrderByDescending(item => item.StartedAt)
            .Select(item => new ConversationStats(
                item.TurnId,
                item.ThreadId,
                item.ContextWindowId,
                item.StartedAt,
                item.Preview,
                item.Tokens))
            .ToArray();
        return new SessionStats(
            conversations.Length,
            dailyTokens.GetValueOrDefault(todayKey),
            days,
            conversations);
    }

    private static void AddDailyTokens(IDictionary<string, long> dailyTokens, string date, long tokens)
    {
        if (tokens < 0 || !dailyTokens.ContainsKey(date)) return;
        dailyTokens[date] += tokens;
    }

    private static void AddConversationTokens(
        IDictionary<string, ConversationBuilder> conversations,
        string? turnId,
        long tokens)
    {
        if (tokens < 0 || turnId is null || !conversations.TryGetValue(turnId, out var conversation)) return;
        conversation.Tokens = (conversation.Tokens ?? 0) + tokens;
    }

    private static bool TryReadTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var value = ReadString(root, "timestamp");
        return value is not null &&
               DateTimeOffset.TryParse(
                   value,
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.RoundtripKind,
                   out timestamp);
    }

    private static bool IsUserConversationSession(JsonElement payload)
    {
        var threadSource = ReadString(payload, "thread_source");
        if (!string.IsNullOrWhiteSpace(threadSource))
        {
            return string.Equals(threadSource, "user", StringComparison.OrdinalIgnoreCase);
        }

        return !payload.TryGetProperty("source", out var source) ||
               source.ValueKind != JsonValueKind.Object ||
               !source.TryGetProperty("subagent", out _);
    }

    private static bool TryReadLegacyTotal(JsonElement payload, out long total)
    {
        total = 0;
        return payload.TryGetProperty("info", out var info) &&
               info.ValueKind == JsonValueKind.Object &&
               info.TryGetProperty("total_token_usage", out var usage) &&
               TryReadInt64(usage, "total_tokens", out total);
    }

    private static bool TryReadInt64(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var node) &&
               node.ValueKind == JsonValueKind.Number &&
               node.TryGetInt64(out value);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var node) &&
               node.ValueKind == JsonValueKind.String
            ? node.GetString()
            : null;
    }

    private static string LocalDateKey(DateTimeOffset timestamp, TimeZoneInfo timeZone)
    {
        return TimeZoneInfo.ConvertTime(timestamp, timeZone)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string NormalizePreview(string value)
    {
        var normalized = WhitespaceRegex().Replace(value, " ").Trim();
        if (normalized.Length <= PreviewLimit) return normalized;
        return $"{normalized[..PreviewLimit].TrimEnd()}…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
