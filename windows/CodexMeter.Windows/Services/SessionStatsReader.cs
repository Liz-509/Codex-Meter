using System.Globalization;
using System.Text.Json;

namespace CodexMeter.Windows.Services;

internal readonly record struct SessionStats(int Questions, long Tokens);

internal static class SessionStatsReader
{
    public static SessionStats ReadToday(string sessionsDirectory, DateTimeOffset now, TimeZoneInfo timeZone)
    {
        if (!Directory.Exists(sessionsDirectory)) return new SessionStats(0, 0);

        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        var localStart = DateTime.SpecifyKind(localNow.Date, DateTimeKind.Unspecified);
        var localEnd = localStart.AddDays(1);
        var startUtc = TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone);
        var questions = 0;
        long tokens = 0;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(sessionsDirectory, "*.jsonl", SearchOption.AllDirectories);
        }
        catch (IOException)
        {
            return new SessionStats(0, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return new SessionStats(0, 0);
        }

        foreach (var file in files)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < startUtc) continue;
                long previousTotal = 0;
                foreach (var line in File.ReadLines(file))
                {
                    if (!TryReadEvent(line, out var eventType, out var timestamp, out var total)) continue;
                    var isToday = timestamp >= startUtc && timestamp < endUtc;

                    if (eventType == "task_started" && isToday) questions++;
                    if (eventType != "token_count" || total is null) continue;

                    if (isToday) tokens += total.Value >= previousTotal ? total.Value - previousTotal : total.Value;
                    previousTotal = total.Value;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return new SessionStats(questions, tokens);
    }

    private static bool TryReadEvent(
        string line,
        out string eventType,
        out DateTimeOffset timestamp,
        out long? totalTokens)
    {
        eventType = string.Empty;
        timestamp = default;
        totalTokens = null;

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var rootType) || rootType.GetString() != "event_msg") return false;
            if (!root.TryGetProperty("timestamp", out var timestampNode) ||
                !DateTimeOffset.TryParse(timestampNode.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp))
            {
                return false;
            }
            if (!root.TryGetProperty("payload", out var payload) ||
                !payload.TryGetProperty("type", out var eventTypeNode)) return false;

            eventType = eventTypeNode.GetString() ?? string.Empty;
            if (eventType == "token_count" &&
                payload.TryGetProperty("info", out var info) &&
                info.TryGetProperty("total_token_usage", out var usage) &&
                usage.TryGetProperty("total_tokens", out var totalNode) &&
                totalNode.TryGetInt64(out var parsedTotal))
            {
                totalTokens = parsedTotal;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
