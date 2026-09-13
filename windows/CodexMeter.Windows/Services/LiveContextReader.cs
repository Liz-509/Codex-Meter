using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexMeter.Windows.Services;

internal sealed class LiveContextReader
{
    private const long MaximumTailBytes = 1_048_576;
    private sealed record Signature(long Length, long LastWriteTicks);
    private sealed record Cached(Signature Signature, ContextMeasurement? Measurement);
    private sealed record ContextMeasurement(long UsedTokens, long MaxTokens, DateTimeOffset UpdatedAt, string? ContextWindowId, int Compactions);
    private readonly Dictionary<string, Cached> _cache = new(StringComparer.OrdinalIgnoreCase);

    public JsonObject CreatePayload(JsonObject thread)
    {
        var threadId = StringValue(thread["id"]);
        if (string.IsNullOrWhiteSpace(threadId)) return new JsonObject { ["contextHealth"] = new JsonObject { ["source"] = "local", ["trackingMode"] = "recent_conversation", ["pollIntervalSeconds"] = 2 } };
        var name = FirstNonEmpty(StringValue(thread["name"]), StringValue(thread["preview"])) ?? "未命名任务";
        var context = new JsonObject
        {
            ["source"] = "local",
            ["trackingMode"] = "recent_conversation",
            ["currentTaskId"] = threadId,
            ["currentTaskName"] = name,
            ["pollIntervalSeconds"] = 2
        };
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
            ["compactions"] = measurement.Compactions
        };
        return new JsonObject { ["contextHealth"] = context };
    }

    private ContextMeasurement? ReadLatest(string path)
    {
        Cached? previous = _cache.GetValueOrDefault(path);
        try
        {
            var info = new FileInfo(path);
            var signature = new Signature(info.Length, info.LastWriteTimeUtc.Ticks);
            if (previous?.Signature == signature) return previous.Measurement;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var start = Math.Max(0, stream.Length - MaximumTailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: false);
            var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            ContextMeasurement? found = null;
            string? currentWindow = null;
            var compactions = 0;
            foreach (var line in lines)
            {
                JsonDocument document;
                try { document = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                using (document)
                {
                    var root = document.RootElement;
                    if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                    var rootType = SessionStatsReader.ReadString(root, "type");
                    if (rootType == "compacted")
                    {
                        compactions++;
                        currentWindow = SessionStatsReader.ReadString(payload, "window_id") ?? currentWindow;
                        continue;
                    }
                    if (rootType != "event_msg" || SessionStatsReader.ReadString(payload, "type") != "token_count" ||
                        !payload.TryGetProperty("info", out var infoNode) || !SessionStatsReader.TryReadInt64(infoNode, "model_context_window", out var maximum) || maximum <= 0 ||
                        !infoNode.TryGetProperty("last_token_usage", out var usage) || !SessionStatsReader.TryReadInt64(usage, "total_tokens", out var used) ||
                        !TryTimestamp(root, out var timestamp)) continue;
                    found = new ContextMeasurement(Math.Max(0, used), maximum, timestamp, currentWindow, compactions);
                }
            }
            var resolved = found ?? previous?.Measurement;
            _cache[path] = new Cached(signature, resolved);
            return resolved;
        }
        catch (IOException) { return previous?.Measurement; }
        catch (UnauthorizedAccessException) { return previous?.Measurement; }
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
