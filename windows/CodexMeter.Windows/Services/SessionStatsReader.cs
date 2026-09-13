using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexMeter.Windows.Services;

internal sealed record DailyTokenStats(string Date, long Tokens, string Source = "local");

internal sealed record ConversationStats(
    string TurnId,
    string? ThreadId,
    string? ContextWindowId,
    DateTimeOffset StartedAt,
    string Preview,
    long? Tokens,
    string Date = "",
    string ProjectPath = ProjectResolver.NonProjectKey);

internal sealed record ProjectDailyStats(string Date, string ProjectPath, long Tokens);

internal sealed record ContextHealthStats(
    string? ThreadId,
    string? ContextWindowId,
    string ProjectPath,
    string Preview,
    long UsedTokens,
    long MaxTokens,
    DateTimeOffset UpdatedAt,
    int Compactions);

internal sealed record SessionStats(
    int Questions,
    long Tokens,
    IReadOnlyList<DailyTokenStats> DailyTokens,
    IReadOnlyList<ConversationStats> Conversations,
    IReadOnlyList<ProjectDailyStats> ProjectDailyTokens,
    IReadOnlyList<ContextHealthStats> ContextHealth)
{
    public SessionStats(int questions, long tokens, IReadOnlyList<DailyTokenStats> dailyTokens, IReadOnlyList<ConversationStats> conversations)
        : this(questions, tokens, dailyTokens, conversations, [], []) { }

    public SessionStats(int questions, long tokens) : this(questions, tokens, [], [], [], []) { }
}

internal static class ProjectResolver
{
    public const string NonProjectKey = "__non_project__";
    public const string NonProjectName = "非项目中对话";

    public static string Resolve(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return NonProjectKey;
        string candidate;
        try { candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)); }
        catch { return NonProjectKey; }

        while (!string.IsNullOrWhiteSpace(candidate))
        {
            var marker = Path.Combine(candidate, ".git");
            try
            {
                if (Directory.Exists(marker)) return candidate;
                if (File.Exists(marker))
                {
                    var pointer = File.ReadLines(marker).FirstOrDefault()?.Trim();
                    if (pointer?.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase) is true)
                    {
                        var gitDir = pointer[7..].Trim();
                        if (!Path.IsPathRooted(gitDir)) gitDir = Path.Combine(candidate, gitDir);
                        gitDir = Path.GetFullPath(gitDir).Replace('/', Path.DirectorySeparatorChar);
                        var worktreeMarker = $"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}worktrees{Path.DirectorySeparatorChar}";
                        var index = gitDir.IndexOf(worktreeMarker, StringComparison.OrdinalIgnoreCase);
                        if (index > 0) return Path.TrimEndingDirectorySeparator(gitDir[..index]);
                    }
                    return candidate;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            var parent = Directory.GetParent(candidate)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, candidate, StringComparison.OrdinalIgnoreCase)) break;
            candidate = parent;
        }
        return NonProjectKey;
    }

    public static IReadOnlyDictionary<string, string> DisplayNames(IEnumerable<string> paths)
    {
        var distinct = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var basenames = distinct.Where(path => path != NonProjectKey)
            .GroupBy(path => Path.GetFileName(path) ?? "未识别项目", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in distinct)
        {
            if (path == NonProjectKey) { result[path] = NonProjectName; continue; }
            var name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name)) name = "未识别项目";
            result[path] = basenames.GetValueOrDefault(name) > 1
                ? $"{name} — {Path.GetFileName(Path.GetDirectoryName(path))}"
                : name;
        }
        return result;
    }

    public static string Kind(string path) => path == NonProjectKey ? "non_project" : "project";
}

internal sealed class SessionStatsCache
{
    private const int HistoryDays = 90;
    private sealed record CachedFile(long Length, long LastWriteTicks, SessionStats Stats);
    private readonly object _gate = new();
    private readonly Dictionary<string, CachedFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, DateTimeOffset, TimeZoneInfo, SessionStats> _readFile;
    private string? _windowKey;

    public SessionStatsCache(Func<string, DateTimeOffset, TimeZoneInfo, SessionStats>? readFile = null) =>
        _readFile = readFile ?? SessionStatsReader.ReadFileContribution;

    public SessionStats ReadToday(string sessionsDirectory, DateTimeOffset now, TimeZoneInfo timeZone) => ReadRange(sessionsDirectory, now, timeZone);

    public SessionStats ReadRange(string sessionsDirectory, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        lock (_gate)
        {
            var localToday = TimeZoneInfo.ConvertTime(now, timeZone).Date;
            var windowKey = $"{timeZone.Id}|{timeZone.GetUtcOffset(now)}|{localToday:yyyy-MM-dd}";
            if (!string.Equals(_windowKey, windowKey, StringComparison.Ordinal)) { _files.Clear(); _windowKey = windowKey; }
            var dayKeys = Enumerable.Range(0, HistoryDays)
                .Select(offset => localToday.AddDays(offset - (HistoryDays - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
            var dailyTokens = dayKeys.ToDictionary(key => key, _ => 0L, StringComparer.Ordinal);
            if (!Directory.Exists(sessionsDirectory)) { _files.Clear(); return BuildResult(dayKeys, dailyTokens, [], [], []); }

            var historyStartUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localToday.AddDays(-(HistoryDays - 1)), DateTimeKind.Unspecified), timeZone);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var conversations = new Dictionary<string, ConversationStats>(StringComparer.Ordinal);
            var projectTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var contexts = new Dictionary<string, ContextHealthStats>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var path in Directory.EnumerateFiles(sessionsDirectory, "*.jsonl", SearchOption.AllDirectories))
                {
                    FileInfo info;
                    try { info = new FileInfo(path); if (info.LastWriteTimeUtc < historyStartUtc) continue; }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    seen.Add(path);
                    if (!_files.TryGetValue(path, out var cached) || cached.Length != info.Length || cached.LastWriteTicks != info.LastWriteTimeUtc.Ticks)
                    {
                        var stats = _readFile(path, now, timeZone);
                        cached = new CachedFile(info.Length, info.LastWriteTimeUtc.Ticks, stats);
                        _files[path] = cached;
                    }
                    foreach (var day in cached.Stats.DailyTokens) if (dailyTokens.ContainsKey(day.Date)) dailyTokens[day.Date] += day.Tokens;
                    foreach (var conversation in cached.Stats.Conversations)
                    {
                        var taskId = conversation.ThreadId ?? conversation.ContextWindowId ?? conversation.TurnId;
                        var key = $"{conversation.Date}\0{conversation.ProjectPath}\0{taskId}\0{conversation.TurnId}";
                        if (!conversations.TryGetValue(key, out var previous) || conversation.StartedAt >= previous.StartedAt)
                            conversations[key] = conversation;
                    }
                    foreach (var project in cached.Stats.ProjectDailyTokens)
                    {
                        var key = $"{project.Date}\0{project.ProjectPath}";
                        projectTotals[key] = projectTotals.GetValueOrDefault(key) + project.Tokens;
                    }
                    foreach (var context in cached.Stats.ContextHealth)
                    {
                        var key = context.ThreadId ?? context.ContextWindowId ?? path;
                        if (!contexts.TryGetValue(key, out var previous) || context.UpdatedAt > previous.UpdatedAt) contexts[key] = context;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            foreach (var stalePath in _files.Keys.Where(path => !seen.Contains(path)).ToArray()) _files.Remove(stalePath);
            var projects = projectTotals.Select(pair => { var parts = pair.Key.Split('\0', 2); return new ProjectDailyStats(parts[0], parts[1], pair.Value); });
            return BuildResult(dayKeys, dailyTokens, conversations.Values, projects, contexts.Values);
        }
    }

    private static SessionStats BuildResult(IReadOnlyList<string> dayKeys, IReadOnlyDictionary<string, long> dailyTokens, IEnumerable<ConversationStats> conversations, IEnumerable<ProjectDailyStats> projects, IEnumerable<ContextHealthStats> contexts)
    {
        var ordered = conversations.OrderByDescending(item => item.StartedAt).ToArray();
        var today = dayKeys[^1];
        var days = dayKeys.Select(key => { var value = dailyTokens.GetValueOrDefault(key); return new DailyTokenStats(key, value, value > 0 ? "local" : "empty"); }).ToArray();
        return new SessionStats(ordered.Count(item => item.Date == today), days[^1].Tokens, days, ordered, projects.ToArray(), contexts.OrderByDescending(item => item.UpdatedAt).ToArray());
    }
}

internal static partial class SessionStatsReader
{
    private const int HistoryDays = 90;
    private const int PreviewLimit = 160;

    private sealed class ConversationBuilder(string turnId, string? threadId, string? windowId, DateTimeOffset startedAt, string date, string projectPath)
    {
        public string TurnId { get; } = turnId;
        public string? ThreadId { get; } = threadId;
        public string? ContextWindowId { get; } = windowId;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public string Date { get; } = date;
        public string ProjectPath { get; } = projectPath;
        public string Preview { get; set; } = "未命名对话";
        public long? Tokens { get; set; }
    }

    public static SessionStats ReadToday(string sessionsDirectory, DateTimeOffset now, TimeZoneInfo timeZone) => new SessionStatsCache().ReadRange(sessionsDirectory, now, timeZone);

    internal static SessionStats ReadFileContribution(string file, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        var today = TimeZoneInfo.ConvertTime(now, timeZone).Date;
        var dayKeys = Enumerable.Range(0, HistoryDays).Select(offset => today.AddDays(offset - (HistoryDays - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
        var daily = dayKeys.ToDictionary(key => key, _ => 0L, StringComparer.Ordinal);
        var conversations = new Dictionary<string, ConversationBuilder>(StringComparer.Ordinal);
        var projects = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        ContextHealthStats? context = null;
        try { ReadFile(file, timeZone, daily, conversations, projects, value => context = value); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        var built = conversations.Values.Select(ToConversation).OrderByDescending(item => item.StartedAt).ToArray();
        return new SessionStats(
            built.Count(item => item.Date == dayKeys[^1]), daily[dayKeys[^1]],
            dayKeys.Select(key => new DailyTokenStats(key, daily[key], daily[key] > 0 ? "local" : "empty")).ToArray(), built,
            projects.Select(pair => { var parts = pair.Key.Split('\0', 2); return new ProjectDailyStats(parts[0], parts[1], pair.Value); }).ToArray(),
            context is null ? [] : [context]);
    }

    private static void ReadFile(string file, TimeZoneInfo timeZone, IDictionary<string, long> dailyTokens, IDictionary<string, ConversationBuilder> conversations, IDictionary<string, long> projectTokens, Action<ContextHealthStats> setContext)
    {
        string? threadId = null, contextWindowId = null, currentTurnId = null;
        var projectPath = ProjectResolver.NonProjectKey;
        var includeConversations = true;
        long previousTotal = 0;
        var usageRecordSinceTokenCount = false;
        var compactions = 0;
        ContextHealthStats? context = null;
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            using (document)
            {
                var root = document.RootElement;
                var rootType = ReadString(root, "type");
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                if (rootType == "session_meta")
                {
                    threadId = ReadString(payload, "id") ?? threadId;
                    includeConversations = IsUserConversationSession(payload);
                    projectPath = ProjectResolver.Resolve(ReadString(payload, "cwd"));
                    if (payload.TryGetProperty("context_window", out var window)) contextWindowId = ReadString(window, "window_id") ?? contextWindowId;
                    continue;
                }
                if (rootType == "compacted")
                {
                    compactions++;
                    contextWindowId = ReadString(payload, "window_id") ?? contextWindowId;
                    if (context is not null) context = context with { ContextWindowId = contextWindowId, Compactions = compactions };
                    continue;
                }
                if (rootType == "event_msg")
                {
                    var eventType = ReadString(payload, "type");
                    if (eventType == "task_started")
                    {
                        currentTurnId = ReadString(payload, "turn_id") ?? Guid.NewGuid().ToString("N");
                        if (!TryReadTimestamp(root, out var startedAt)) continue;
                        var date = LocalDateKey(startedAt, timeZone);
                        if (includeConversations && dailyTokens.ContainsKey(date)) conversations[currentTurnId] = new ConversationBuilder(currentTurnId, threadId, contextWindowId, startedAt, date, projectPath);
                        continue;
                    }
                    if (eventType == "user_message" && currentTurnId is not null && conversations.TryGetValue(currentTurnId, out var legacy))
                    {
                        var preview = ReadString(payload, "message");
                        if (!string.IsNullOrWhiteSpace(preview)) { legacy.Preview = NormalizePreview(preview); if (context?.Preview == "未命名任务") context = context with { Preview = legacy.Preview }; }
                        continue;
                    }
                    if (eventType != "token_count") continue;
                    if (includeConversations && TryReadContext(payload, root, out var used, out var maximum, out var updatedAt))
                    {
                        var preview = context?.Preview ?? (currentTurnId is not null && conversations.TryGetValue(currentTurnId, out var item) ? item.Preview : "未命名任务");
                        context = new ContextHealthStats(threadId, contextWindowId, projectPath, preview, used, maximum, updatedAt, compactions);
                    }
                    if (!TryReadLegacyTotal(payload, out var total)) continue;
                    var delta = total >= previousTotal ? total - previousTotal : total;
                    previousTotal = total;
                    if (usageRecordSinceTokenCount) { usageRecordSinceTokenCount = false; continue; }
                    if (TryReadTimestamp(root, out var tokenAt)) AddTokens(dailyTokens, projectTokens, conversations, currentTurnId, projectPath, LocalDateKey(tokenAt, timeZone), delta);
                    continue;
                }
                if (rootType == "token_usage_record")
                {
                    var turnId = ReadString(payload, "turn_id") ?? currentTurnId;
                    var counted = false;
                    if (payload.TryGetProperty("usage", out var usage) && TryReadInt64(usage, "total_tokens", out var responseTokens) && TryReadTimestamp(root, out var usageAt))
                    { AddTokens(dailyTokens, projectTokens, conversations, null, projectPath, LocalDateKey(usageAt, timeZone), responseTokens); counted = true; }
                    if (turnId is not null && conversations.TryGetValue(turnId, out var conversation) && payload.TryGetProperty("turn_token_usage", out var turnUsage) && TryReadInt64(turnUsage, "total_tokens", out var turnTokens)) conversation.Tokens = Math.Max(0, turnTokens);
                    usageRecordSinceTokenCount = counted;
                    continue;
                }
                if (rootType == "response_item") ReadUserPreview(payload, currentTurnId, conversations, context, value => context = value);
            }
        }
        if (context is not null) setContext(context);
    }

    private static void AddTokens(IDictionary<string, long> days, IDictionary<string, long> projects, IDictionary<string, ConversationBuilder> conversations, string? turnId, string project, string date, long tokens)
    {
        if (tokens < 0 || !days.ContainsKey(date)) return;
        days[date] += tokens;
        var key = $"{date}\0{project}";
        projects.TryGetValue(key, out var existing);
        projects[key] = existing + tokens;
        if (turnId is not null && conversations.TryGetValue(turnId, out var conversation)) conversation.Tokens = (conversation.Tokens ?? 0) + tokens;
    }

    private static bool TryReadContext(JsonElement payload, JsonElement root, out long used, out long maximum, out DateTimeOffset updatedAt)
    {
        used = maximum = 0; updatedAt = default;
        return payload.TryGetProperty("info", out var info) && TryReadInt64(info, "model_context_window", out maximum) && maximum > 0 && info.TryGetProperty("last_token_usage", out var last) && TryReadInt64(last, "total_tokens", out used) && TryReadTimestamp(root, out updatedAt);
    }

    private static void ReadUserPreview(JsonElement payload, string? currentTurnId, IDictionary<string, ConversationBuilder> conversations, ContextHealthStats? context, Action<ContextHealthStats?> setContext)
    {
        if (ReadString(payload, "type") != "message" || ReadString(payload, "role") != "user") return;
        var turnId = currentTurnId;
        var isUserText = false;
        if (payload.TryGetProperty("internal_chat_message_metadata_passthrough", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            turnId = ReadString(metadata, "turn_id") ?? turnId;
            if (metadata.TryGetProperty("content_item_kinds", out var kinds) && kinds.ValueKind == JsonValueKind.Array) isUserText = kinds.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "user.text");
        }
        if (!isUserText || turnId is null || !conversations.TryGetValue(turnId, out var conversation) || !payload.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        var text = string.Join(" ", content.EnumerateArray().Where(item => ReadString(item, "type") is "input_text" or "text").Select(item => ReadString(item, "text")).Where(item => !string.IsNullOrWhiteSpace(item)));
        if (string.IsNullOrWhiteSpace(text)) return;
        conversation.Preview = NormalizePreview(text);
        if (context?.Preview == "未命名任务") setContext(context with { Preview = conversation.Preview });
    }

    private static ConversationStats ToConversation(ConversationBuilder item) => new(item.TurnId, item.ThreadId, item.ContextWindowId, item.StartedAt, item.Preview, item.Tokens, item.Date, item.ProjectPath);
    internal static string ContextHealthStatus(double usedPercent) => usedPercent >= 90 ? "critical" : usedPercent >= 80 ? "high" : usedPercent >= 60 ? "attention" : "healthy";

    private static bool TryReadTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var value = ReadString(root, "timestamp");
        return value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);
    }

    private static bool IsUserConversationSession(JsonElement payload)
    {
        var source = ReadString(payload, "thread_source");
        if (!string.IsNullOrWhiteSpace(source)) return string.Equals(source, "user", StringComparison.OrdinalIgnoreCase);
        return !payload.TryGetProperty("source", out var node) || node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("subagent", out _);
    }

    private static bool TryReadLegacyTotal(JsonElement payload, out long total)
    {
        total = 0;
        return payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object && info.TryGetProperty("total_token_usage", out var usage) && TryReadInt64(usage, "total_tokens", out total);
    }

    internal static bool TryReadInt64(JsonElement element, string propertyName, out long value)
    {
        value = 0;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var node) && node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out value);
    }

    internal static string? ReadString(JsonElement element, string propertyName) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() : null;
    internal static string LocalDateKey(DateTimeOffset timestamp, TimeZoneInfo timeZone) => TimeZoneInfo.ConvertTime(timestamp, timeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    internal static string NormalizePreview(string value) { var normalized = WhitespaceRegex().Replace(value, " ").Trim(); return normalized.Length <= PreviewLimit ? normalized : $"{normalized[..PreviewLimit].TrimEnd()}…"; }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
