using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexMeter.Windows.Services;

internal sealed record QuotaReading(string Key, string Label, double RemainingPercent, double? ResetsAt);
internal enum QuotaEventKind { Threshold, Exhausted, Restored }
internal sealed record QuotaEvent(QuotaEventKind Kind, QuotaReading Reading, int? Threshold, string DeduplicationKey);
internal sealed record QuotaMonitorResult(JsonObject Forecast, IReadOnlyList<QuotaReading> Readings, IReadOnlyList<QuotaEvent> Events);
internal sealed record QuotaSample(double Timestamp, string Window, double RemainingPercent, double? ResetsAt);

internal sealed class UsageSampleStore
{
    private readonly string _path;
    private readonly List<QuotaSample> _samples;
    public UsageSampleStore(string? path = null)
    {
        _path = path ?? Path.Combine(LocalSettingsStore.AppDataDirectory, "usage-samples.json");
        try { _samples = File.Exists(_path) ? JsonSerializer.Deserialize<List<QuotaSample>>(File.ReadAllText(_path)) ?? [] : []; }
        catch { _samples = []; }
    }

    public IReadOnlyList<QuotaSample> Samples => _samples;

    public void Record(IEnumerable<QuotaReading> readings, DateTimeOffset now)
    {
        var timestamp = now.ToUnixTimeMilliseconds() / 1000d;
        foreach (var reading in readings)
        {
            var last = _samples.LastOrDefault(item => item.Window == reading.Key);
            var changed = last is null || Math.Abs(last.RemainingPercent - reading.RemainingPercent) >= .01;
            var cycleChanged = last is null || !SameCycle(last.ResetsAt, reading.ResetsAt);
            if (last is null || timestamp - last.Timestamp >= 300 || changed || cycleChanged)
                _samples.Add(new QuotaSample(timestamp, reading.Key, reading.RemainingPercent, reading.ResetsAt));
        }
        var cutoff = timestamp - 14 * 86_400;
        var retained = _samples.Where(item => item.Timestamp >= cutoff).TakeLast(5_000).ToArray();
        _samples.Clear();
        _samples.AddRange(retained);
        Persist();
    }

    private void Persist()
    {
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(_samples));
            File.Move(temporary, _path, true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    internal static bool SameCycle(double? left, double? right) => left.HasValue == right.HasValue && (!left.HasValue || Math.Abs(left.Value - right!.Value) < 60);
}

internal sealed class QuotaMonitor
{
    private readonly LocalSettingsStore _settings;
    private readonly UsageSampleStore _samples;

    public QuotaMonitor(LocalSettingsStore settings, UsageSampleStore? samples = null)
    {
        _settings = settings;
        _samples = samples ?? new UsageSampleStore();
    }

    public QuotaMonitorResult Process(JsonObject payload, DateTimeOffset? now = null)
    {
        var instant = now ?? DateTimeOffset.Now;
        var readings = Readings(payload);
        var events = CreateEvents(readings);
        _samples.Record(readings, instant);
        var forecast = new JsonObject();
        foreach (var reading in readings) forecast[reading.Key] = Forecast(reading, instant);
        return new QuotaMonitorResult(forecast, readings, events);
    }

    internal static IReadOnlyList<QuotaReading> Readings(JsonObject payload)
    {
        JsonObject? limits = payload["rateLimitsByLimitId"]?["codex"] as JsonObject ?? payload["rateLimits"] as JsonObject;
        limits ??= payload;
        var result = new List<QuotaReading>();
        foreach (var (key, fallback) in new[] { ("primary", "5 小时额度"), ("secondary", "每周额度") })
        {
            var bucket = limits[key] as JsonObject ?? payload[key] as JsonObject;
            if (bucket is null) continue;
            var remaining = Number(bucket["remainingPercent"]);
            if (!remaining.HasValue && Number(bucket["usedPercent"]) is double used) remaining = 100 - used;
            if (!remaining.HasValue) continue;
            result.Add(new QuotaReading(key, Text(bucket["label"]) ?? fallback, Math.Clamp(remaining.Value, 0, 100), Number(bucket["resetsAt"])));
        }
        return result;
    }

    private JsonObject Forecast(QuotaReading reading, DateTimeOffset now)
    {
        var timestamp = now.ToUnixTimeMilliseconds() / 1000d;
        var lookback = reading.Key == "primary" ? 3 * 3_600d : 7 * 86_400d;
        var minimumSpan = reading.Key == "primary" ? 15 * 60d : 6 * 3_600d;
        var cycle = _samples.Samples.Where(item => item.Window == reading.Key && item.Timestamp >= timestamp - lookback && UsageSampleStore.SameCycle(item.ResetsAt, reading.ResetsAt)).OrderBy(item => item.Timestamp).ToArray();
        if (cycle.Length < 4 || cycle[^1].Timestamp - cycle[0].Timestamp < minimumSpan || cycle[0].RemainingPercent - cycle[^1].RemainingPercent < 2)
            return new JsonObject { ["status"] = "insufficient", ["message"] = "暂无足够数据", ["confidence"] = "low" };
        var meanX = cycle.Average(item => item.Timestamp);
        var meanY = cycle.Average(item => item.RemainingPercent);
        var numerator = cycle.Sum(item => (item.Timestamp - meanX) * (item.RemainingPercent - meanY));
        var denominator = cycle.Sum(item => Math.Pow(item.Timestamp - meanX, 2));
        var rate = denominator > 0 ? Math.Max(0, -(numerator / denominator) * 3_600) : 0;
        if (rate <= .01) return new JsonObject { ["status"] = "steady", ["message"] = "近期用量稳定", ["ratePerHour"] = 0, ["confidence"] = "low" };
        var exhaustsAt = timestamp + reading.RemainingPercent / rate * 3_600;
        var span = cycle[^1].Timestamp - cycle[0].Timestamp;
        var drop = cycle[0].RemainingPercent - cycle[^1].RemainingPercent;
        var confidence = reading.Key == "primary"
            ? span >= 3_600 && drop >= 10 ? "high" : span >= 1_800 && drop >= 5 ? "medium" : "low"
            : span >= 48 * 3_600 && drop >= 10 ? "high" : span >= 24 * 3_600 && drop >= 5 ? "medium" : "low";
        if (reading.ResetsAt is double reset && exhaustsAt >= reset)
            return new JsonObject { ["status"] = "safe_until_reset", ["message"] = "预计可用至本次重置", ["ratePerHour"] = rate, ["confidence"] = confidence };
        return new JsonObject { ["status"] = "will_deplete", ["message"] = "按近期速度可能提前耗尽", ["ratePerHour"] = rate, ["estimatedExhaustsAt"] = exhaustsAt, ["confidence"] = confidence };
    }

    private IReadOnlyList<QuotaEvent> CreateEvents(IReadOnlyList<QuotaReading> readings)
    {
        var result = new List<QuotaEvent>();
        _settings.Update(settings =>
        {
            foreach (var reading in readings)
            {
                if (!settings.PreviousRemaining.TryGetValue(reading.Key, out var previous))
                {
                    settings.PreviousRemaining[reading.Key] = reading.RemainingPercent;
                    settings.PreviousResetsAt[reading.Key] = reading.ResetsAt;
                    continue;
                }
                settings.PreviousResetsAt.TryGetValue(reading.Key, out var previousReset);
                var cycle = ((long)(reading.ResetsAt ?? 0)).ToString();
                var crossed = new[] { 20, 10, 5 }.Where(value => previous > value && reading.RemainingPercent <= value).ToArray();
                if (crossed.Length > 0)
                {
                    var threshold = crossed.Min();
                    var key = $"{reading.Key}:{cycle}:threshold:{threshold}";
                    if (!settings.DeliveredEvents.Contains(key)) result.Add(new QuotaEvent(QuotaEventKind.Threshold, reading, threshold, key));
                    foreach (var value in crossed) settings.DeliveredEvents.Add($"{reading.Key}:{cycle}:threshold:{value}");
                }
                if (previous > .5 && reading.RemainingPercent <= .5)
                {
                    var key = $"{reading.Key}:{cycle}:exhausted";
                    if (settings.DeliveredEvents.Add(key)) result.Add(new QuotaEvent(QuotaEventKind.Exhausted, reading, null, key));
                }
                if ((reading.ResetsAt ?? 0) > (previousReset ?? 0) + 60 && previous < 20 && reading.RemainingPercent >= previous + 5)
                {
                    var key = $"{reading.Key}:{cycle}:restored";
                    if (settings.DeliveredEvents.Add(key)) result.Add(new QuotaEvent(QuotaEventKind.Restored, reading, null, key));
                }
                settings.PreviousRemaining[reading.Key] = reading.RemainingPercent;
                settings.PreviousResetsAt[reading.Key] = reading.ResetsAt;
            }
            settings.DeliveredEvents = settings.DeliveredEvents.TakeLast(240).ToHashSet(StringComparer.Ordinal);
        });
        return result;
    }

    private static double? Number(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<double>(out var number)) return number;
        if (value.TryGetValue<long>(out var integer)) return integer;
        return null;
    }
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
