using System.Text.Json.Nodes;
using CodexMeter.Windows.Services;

namespace CodexMeter.Windows.Tests;

[TestClass]
public sealed class CodexAnalyticsTests
{
    [TestMethod]
    public void Process_BuildsForecastAndEmitsDeduplicatedEvents()
    {
        using var files = new TemporaryFiles();
        var settings = new LocalSettingsStore(files.Path("settings.json"));
        var monitor = new QuotaMonitor(settings, new UsageSampleStore(files.Path("samples.json")));
        var start = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);
        var reset = start.AddHours(10).ToUnixTimeSeconds();

        var initial = monitor.Process(Payload(100, reset), start);
        Assert.AreEqual(0, initial.Events.Count, "首次成功同步只能建立基线");
        Assert.AreEqual("insufficient", initial.Forecast["primary"]!["status"]!.GetValue<string>());

        monitor.Process(Payload(98, reset), start.AddMinutes(5));
        monitor.Process(Payload(96, reset), start.AddMinutes(10));
        var forecast = monitor.Process(Payload(94, reset), start.AddMinutes(20));
        Assert.AreEqual("will_deplete", forecast.Forecast["primary"]!["status"]!.GetValue<string>());
        Assert.IsTrue(forecast.Forecast["primary"]!["ratePerHour"]!.GetValue<double>() > 0);

        var threshold = monitor.Process(Payload(9, reset), start.AddMinutes(25));
        Assert.AreEqual(1, threshold.Events.Count(item => item.Kind == QuotaEventKind.Threshold));
        Assert.AreEqual(10, threshold.Events.Single(item => item.Kind == QuotaEventKind.Threshold).Threshold);
        Assert.AreEqual(0, monitor.Process(Payload(8, reset), start.AddMinutes(30)).Events.Count);

        var exhausted = monitor.Process(Payload(0, reset), start.AddMinutes(35));
        Assert.IsTrue(exhausted.Events.Any(item => item.Kind == QuotaEventKind.Exhausted));
        var nextReset = reset + 7 * 86_400;
        var restored = monitor.Process(Payload(85, nextReset), start.AddMinutes(40));
        Assert.IsTrue(restored.Events.Any(item => item.Kind == QuotaEventKind.Restored));

        var restarted = new QuotaMonitor(
            new LocalSettingsStore(files.Path("settings.json")),
            new UsageSampleStore(files.Path("samples.json")));
        Assert.IsFalse(restarted.Process(Payload(84, nextReset), start.AddMinutes(45)).Events.Any(), "重启后不得重复通知");
    }

    [TestMethod]
    public void Process_ReportsSafeUntilResetAndIsolatesQuotaCycles()
    {
        using var files = new TemporaryFiles();
        var monitor = new QuotaMonitor(
            new LocalSettingsStore(files.Path("settings.json")),
            new UsageSampleStore(files.Path("samples.json")));
        var start = DateTimeOffset.FromUnixTimeSeconds(2_100_000_000);
        var reset = start.AddMinutes(40).ToUnixTimeSeconds();
        foreach (var (minutes, remaining) in new[] { (0, 100d), (5, 99.4), (10, 98.7), (20, 97d) })
            monitor.Process(Payload(remaining, reset), start.AddMinutes(minutes));

        var result = monitor.Process(Payload(96.5, reset), start.AddMinutes(25));
        Assert.AreEqual("safe_until_reset", result.Forecast["primary"]!["status"]!.GetValue<string>());

        var newReset = reset + 86_400;
        var newCycle = monitor.Process(Payload(100, newReset), start.AddMinutes(30));
        Assert.AreEqual("insufficient", newCycle.Forecast["primary"]!["status"]!.GetValue<string>());
    }

    [TestMethod]
    public void SampleStore_RecordsChangesOrFiveMinuteIntervalsAndPrunesOldData()
    {
        using var files = new TemporaryFiles();
        var path = files.Path("samples.json");
        var store = new UsageSampleStore(path);
        var start = DateTimeOffset.FromUnixTimeSeconds(2_200_000_000);
        store.Record([new QuotaReading("primary", "5 小时额度", 90, 123)], start);
        store.Record([new QuotaReading("primary", "5 小时额度", 90, 123)], start.AddMinutes(1));
        store.Record([new QuotaReading("primary", "5 小时额度", 89, 123)], start.AddMinutes(2));
        store.Record([new QuotaReading("primary", "5 小时额度", 89, 123)], start.AddMinutes(7));

        Assert.AreEqual(3, store.Samples.Count);
        Assert.AreEqual(3, new UsageSampleStore(path).Samples.Count, "样本应跨重启持久化");
    }

    [TestMethod]
    public void Readings_PrefersCodexLimitBucketAndSupportsUsedPercent()
    {
        var payload = JsonNode.Parse("""
            {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":25,"resetsAt":123}}},"rateLimits":{"primary":{"remainingPercent":1}}}
            """)!.AsObject();
        var reading = QuotaMonitor.Readings(payload).Single();
        Assert.AreEqual(75d, reading.RemainingPercent);
        Assert.AreEqual(123d, reading.ResetsAt);
    }

    private static JsonObject Payload(double remaining, double reset) => new()
    {
        ["rateLimits"] = new JsonObject
        {
            ["primary"] = new JsonObject { ["remainingPercent"] = remaining, ["resetsAt"] = reset }
        }
    };

    private sealed class TemporaryFiles : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"codex-meter-analytics-{Guid.NewGuid():N}");
        public TemporaryFiles() => Directory.CreateDirectory(_directory);
        public string Path(string name) => System.IO.Path.Combine(_directory, name);
        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
