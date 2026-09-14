using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexMeter.Windows.Services;

internal sealed class LiveContextReader
{
    private sealed class LiveState
    {
        public long Offset { get; set; }
        public long LastWriteTicks { get; set; }
        public int PrefixLength { get; set; }
        public string PrefixSignature { get; set; } = string.Empty;
        public string PendingLine { get; set; } = string.Empty;
        public long PreviousTotal { get; set; }
        public string? CurrentTurnId { get; set; }
        public bool CurrentTurnActive { get; set; }
        public string? ContextWindowId { get; set; }
        public int Compactions { get; set; }
        public long? UsedTokens { get; set; }
        public long? MaxTokens { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public Dictionary<string, long> TurnTokens { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, long> LegacyBaselines { get; } = new(StringComparer.Ordinal);
        public HashSet<string> AuthoritativeTurns { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ContextMeasurement(
        long UsedTokens,
        long MaxTokens,
        DateTimeOffset UpdatedAt,
        string? ContextWindowId,
        int Compactions,
        long ConversationTokens,
        string? CurrentTurnId,
        long? CurrentTurnTokens,
        bool CurrentTurnActive);

    private readonly Dictionary<string, LiveState> _states = new(StringComparer.OrdinalIgnoreCase);

    public JsonObject CreatePayload(JsonObject thread, int pollIntervalSeconds = 2)
    {
        var threadId = StringValue(thread["id"]);
        var context = new JsonObject
        {
            ["source"] = "local",
            ["trackingMode"] = "recent_conversation",
            ["pollIntervalSeconds"] = pollIntervalSeconds
        };
        if (string.IsNullOrWhiteSpace(threadId)) return new JsonObject { ["contextHealth"] = context };

        var name = FirstNonEmpty(StringValue(thread["name"]), StringValue(thread["preview"])) ?? "未命名任务";
        context["currentTaskId"] = threadId;
        context["currentTaskName"] = name;
        var path = StringValue(thread["path"]);
        if (string.IsNullOrWhiteSpace(path)) return new JsonObject { ["contextHealth"] = context };
        var measurement = ReadLatest(path);
        if (measurement is null) return new JsonObject { ["contextHealth"] = context };

        var projectPath = ProjectResolver.Resolve(StringValue(thread["cwd"]));
        var projectName = ProjectResolver.DisplayNames([projectPath])[projectPath];
        var usedPercent = Math.Clamp((double)measurement.UsedTokens / measurement.MaxTokens * 100, 0, 100);
        context["session"] = new JsonObject
        {
            ["taskId"] = threadId,
            ["threadId"] = threadId,
            ["contextWindowId"] = measurement.ContextWindowId,
            ["name"] = name,
            ["projectKey"] = projectPath,
            ["projectName"] = projectName,
            ["projectKind"] = ProjectResolver.Kind(projectPath),
            ["usedTokens"] = measurement.UsedTokens,
            ["maxTokens"] = measurement.MaxTokens,
            ["usedPercent"] = usedPercent,
            ["remainingPercent"] = Math.Max(0, 100 - usedPercent),
            ["status"] = SessionStatsReader.ContextHealthStatus(usedPercent),
            ["lastActive"] = measurement.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["compactions"] = measurement.Compactions,
            ["conversationTokens"] = measurement.ConversationTokens,
            ["currentTurnId"] = measurement.CurrentTurnId,
            ["currentTurnTokens"] = measurement.CurrentTurnTokens,
            ["currentTurnActive"] = measurement.CurrentTurnActive
        };
        return new JsonObject { ["contextHealth"] = context };
    }

    private ContextMeasurement? ReadLatest(string path)
    {
        _states.TryGetValue(path, out var state);
        try
        {
            var info = new FileInfo(path);
            var prefixChanged = state is { PrefixLength: > 0 } && info.Length >= state.PrefixLength &&
                !string.Equals(state.PrefixSignature, PrefixSignature(path, state.PrefixLength), StringComparison.Ordinal);
            if (state is null || info.Length < state.Offset ||
                prefixChanged || (info.Length == state.Offset && state.Offset > 0 && info.LastWriteTimeUtc.Ticks != state.LastWriteTicks))
            {
                state = new LiveState();
                _states[path] = state;
            }
            if (state.PrefixLength == 0 && info.Length > 0)
            {
                state.PrefixLength = (int)Math.Min(256, info.Length);
                state.PrefixSignature = PrefixSignature(path, state.PrefixLength);
            }
            if (info.Length > state.Offset)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(state.Offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: state.Offset == 0, bufferSize: 4096, leaveOpen: true);
                var appended = reader.ReadToEnd();
                state.Offset = stream.Length;
                Consume(state, state.PendingLine + appended);
            }
            state.LastWriteTicks = info.LastWriteTimeUtc.Ticks;
            return Measurement(state);
        }
        catch (IOException) { return state is null ? null : Measurement(state); }
        catch (UnauthorizedAccessException) { return state is null ? null : Measurement(state); }
    }

    private static string PrefixSignature(string path, int length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[length];
        var read = stream.Read(buffer, 0, length);
        return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
    }

    private static void Consume(LiveState state, string text)
    {
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length - 1; index++) ParseLine(state, lines[index].TrimEnd('\r'));
        state.PendingLine = ParseLine(state, lines[^1].TrimEnd('\r')) ? string.Empty : lines[^1];
    }

    private static bool ParseLine(LiveState state, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return true;
        JsonDocument document;
        try { document = JsonDocument.Parse(line); }
        catch (JsonException) { return false; }
        using (document)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return true;
            var rootType = SessionStatsReader.ReadString(root, "type");
            if (rootType == "compacted")
            {
                state.Compactions++;
                state.ContextWindowId = SessionStatsReader.ReadString(payload, "window_id") ?? state.ContextWindowId;
                return true;
            }
            if (rootType == "token_usage_record")
            {
                var turnId = SessionStatsReader.ReadString(payload, "turn_id") ?? state.CurrentTurnId;
                if (turnId is not null && payload.TryGetProperty("turn_token_usage", out var usage) &&
                    SessionStatsReader.TryReadInt64(usage, "total_tokens", out var tokens))
                {
                    state.TurnTokens[turnId] = Math.Max(0, tokens);
                    state.AuthoritativeTurns.Add(turnId);
                }
                return true;
            }
            if (rootType != "event_msg") return true;
            var eventType = SessionStatsReader.ReadString(payload, "type");
            if (eventType == "task_started")
            {
                state.CurrentTurnId = SessionStatsReader.ReadString(payload, "turn_id") ?? Guid.NewGuid().ToString("N");
                state.CurrentTurnActive = true;
                state.TurnTokens.TryAdd(state.CurrentTurnId, 0);
                state.LegacyBaselines[state.CurrentTurnId] = state.PreviousTotal;
                return true;
            }
            if (eventType == "task_complete")
            {
                var completed = SessionStatsReader.ReadString(payload, "turn_id") ?? state.CurrentTurnId;
                if (completed == state.CurrentTurnId) state.CurrentTurnActive = false;
                return true;
            }
            if (eventType != "token_count" || !payload.TryGetProperty("info", out var info)) return true;
            if (info.TryGetProperty("total_token_usage", out var totalUsage) &&
                SessionStatsReader.TryReadInt64(totalUsage, "total_tokens", out var total))
            {
                total = Math.Max(0, total);
                if (state.CurrentTurnId is string turnId && !state.AuthoritativeTurns.Contains(turnId))
                {
                    var baseline = state.LegacyBaselines.GetValueOrDefault(turnId, state.PreviousTotal);
                    var turnTotal = total >= baseline ? total - baseline : total;
                    state.TurnTokens[turnId] = Math.Max(state.TurnTokens.GetValueOrDefault(turnId), Math.Max(0, turnTotal));
                }
                state.PreviousTotal = total;
            }
            if (SessionStatsReader.TryReadInt64(info, "model_context_window", out var maximum) && maximum > 0 &&
                info.TryGetProperty("last_token_usage", out var lastUsage) &&
                SessionStatsReader.TryReadInt64(lastUsage, "total_tokens", out var used) && TryTimestamp(root, out var timestamp))
            {
                state.UsedTokens = Math.Max(0, used);
                state.MaxTokens = maximum;
                state.UpdatedAt = timestamp;
            }
            return true;
        }
    }

    private static ContextMeasurement? Measurement(LiveState state)
    {
        if (state.UsedTokens is not long used || state.MaxTokens is not long maximum || maximum <= 0 || state.UpdatedAt is not DateTimeOffset updatedAt) return null;
        return new ContextMeasurement(
            used,
            maximum,
            updatedAt,
            state.ContextWindowId,
            state.Compactions,
            state.TurnTokens.Values.Sum(),
            state.CurrentTurnId,
            state.CurrentTurnId is string turnId ? state.TurnTokens.GetValueOrDefault(turnId) : null,
            state.CurrentTurnActive);
    }

    private static bool TryTimestamp(JsonElement root, out DateTimeOffset timestamp)
    {
        timestamp = default;
        var value = SessionStatsReader.ReadString(root, "timestamp");
        return value is not null && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);
    }

    private static string? StringValue(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
