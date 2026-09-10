using CodexMeter.Windows.Services;

namespace CodexMeter.Windows.Tests;

[TestClass]
public sealed class SessionStatsReaderTests
{
    [TestMethod]
    public void ReadToday_UsesLocalDayAndHandlesTokenCounterReset()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-meter-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "session.jsonl");
            File.WriteAllLines(file,
            [
                Event("2026-09-10T15:59:00.000Z", "token_count", 100),
                Event("2026-09-10T16:01:00.000Z", "task_started", null),
                Event("2026-09-10T16:02:00.000Z", "token_count", 160),
                Event("2026-09-10T16:03:00.000Z", "token_count", 20)
            ]);
            File.SetLastWriteTimeUtc(file, new DateTime(2026, 9, 11, 1, 0, 0, DateTimeKind.Utc));

            var timeZone = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
            var stats = SessionStatsReader.ReadToday(
                directory,
                DateTimeOffset.Parse("2026-09-11T02:00:00Z"),
                timeZone);

            Assert.AreEqual(1, stats.Questions);
            Assert.AreEqual(80, stats.Tokens);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Event(string timestamp, string eventType, long? tokens)
    {
        var tokenInfo = tokens is null
            ? string.Empty
            : $",\"info\":{{\"total_token_usage\":{{\"total_tokens\":{tokens}}}}}";
        return $"{{\"timestamp\":\"{timestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"{eventType}\"{tokenInfo}}}}}";
    }
}
