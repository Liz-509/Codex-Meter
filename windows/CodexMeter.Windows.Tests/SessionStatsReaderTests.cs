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
            Assert.AreEqual(80L, stats.Tokens);
            Assert.AreEqual(7, stats.DailyTokens.Count);
            Assert.AreEqual("2026-09-10", stats.DailyTokens[^2].Date);
            Assert.AreEqual(100L, stats.DailyTokens[^2].Tokens);
            Assert.AreEqual("2026-09-11", stats.DailyTokens[^1].Date);
            Assert.AreEqual(80L, stats.DailyTokens[^1].Tokens);
            Assert.AreEqual(1, stats.Conversations.Count);
            Assert.IsNull(stats.Conversations[0].ContextWindowId);
            Assert.AreEqual(80L, stats.Conversations[0].Tokens.GetValueOrDefault());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ReadToday_UsesStructuredTurnUsageAndUserTextWithoutDoubleCounting()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-meter-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var file = Path.Combine(directory, "session.jsonl");
            File.WriteAllLines(file,
            [
                "{not-json",
                SessionMeta("2026-09-10T23:50:00.000Z", "thread-a", "window-a"),
                TaskStarted("2026-09-11T00:01:00.000Z", "turn-a"),
                UserText("2026-09-11T00:01:01.000Z", "turn-a", "  第一条\n问题  "),
                UsageRecord("2026-09-11T00:02:00.000Z", "turn-a", 50, 50),
                Event("2026-09-11T00:02:01.000Z", "token_count", 50),
                TaskStarted("2026-09-11T01:00:00.000Z", "turn-b"),
                UserText("2026-09-11T01:00:01.000Z", "turn-b", "第二条问题"),
                UsageRecord("2026-09-11T01:02:00.000Z", "turn-b", 25, 25),
                Event("2026-09-11T01:02:01.000Z", "token_count", 75)
            ]);
            File.SetLastWriteTimeUtc(file, new DateTime(2026, 9, 11, 2, 0, 0, DateTimeKind.Utc));

            var stats = SessionStatsReader.ReadToday(
                directory,
                DateTimeOffset.Parse("2026-09-11T03:00:00Z"),
                TimeZoneInfo.Utc);

            Assert.AreEqual(2, stats.Questions);
            Assert.AreEqual(75L, stats.Tokens);
            Assert.AreEqual("turn-b", stats.Conversations[0].TurnId);
            Assert.AreEqual("thread-a", stats.Conversations[0].ThreadId);
            Assert.AreEqual("window-a", stats.Conversations[0].ContextWindowId);
            Assert.AreEqual("第二条问题", stats.Conversations[0].Preview);
            Assert.AreEqual(25L, stats.Conversations[0].Tokens.GetValueOrDefault());
            Assert.AreEqual("第一条 问题", stats.Conversations[1].Preview);
            Assert.AreEqual(50L, stats.Conversations[1].Tokens.GetValueOrDefault());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void ReadToday_PreservesContextWindowAcrossMultipleLogFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-meter-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var firstFile = Path.Combine(directory, "first.jsonl");
            var secondFile = Path.Combine(directory, "second.jsonl");
            File.WriteAllLines(firstFile,
            [
                SessionMeta("2026-09-11T00:00:00.000Z", "thread-a", "window-a"),
                TaskStarted("2026-09-11T00:10:00.000Z", "turn-a"),
                UserText("2026-09-11T00:10:01.000Z", "turn-a", "第一轮")
            ]);
            File.WriteAllLines(secondFile,
            [
                SessionMeta("2026-09-11T01:00:00.000Z", "thread-a", "window-a"),
                TaskStarted("2026-09-11T01:10:00.000Z", "turn-b"),
                UserText("2026-09-11T01:10:01.000Z", "turn-b", "第二轮")
            ]);
            var modifiedAt = new DateTime(2026, 9, 11, 2, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(firstFile, modifiedAt);
            File.SetLastWriteTimeUtc(secondFile, modifiedAt);

            var stats = SessionStatsReader.ReadToday(
                directory,
                DateTimeOffset.Parse("2026-09-11T03:00:00Z"),
                TimeZoneInfo.Utc);

            Assert.AreEqual(2, stats.Conversations.Count);
            Assert.IsTrue(stats.Conversations.All(item => item.ContextWindowId == "window-a"));
            Assert.AreEqual("第二轮", stats.Conversations[0].Preview);
            Assert.AreEqual("第一轮", stats.Conversations[1].Preview);
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

    private static string TaskStarted(string timestamp, string turnId)
    {
        return $"{{\"timestamp\":\"{timestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\",\"turn_id\":\"{turnId}\"}}}}";
    }

    private static string SessionMeta(string timestamp, string threadId, string contextWindowId)
    {
        return $"{{\"timestamp\":\"{timestamp}\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{threadId}\",\"context_window\":{{\"window_id\":\"{contextWindowId}\"}}}}}}";
    }

    private static string UserText(string timestamp, string turnId, string text)
    {
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            timestamp,
            type = "response_item",
            payload = new
            {
                type = "message",
                role = "user",
                internal_chat_message_metadata_passthrough = new
                {
                    turn_id = turnId,
                    content_item_kinds = new[] { "user.text" }
                },
                content = new[] { new { type = "input_text", text } }
            }
        });
    }

    private static string UsageRecord(string timestamp, string turnId, long responseTokens, long turnTokens)
    {
        return $"{{\"timestamp\":\"{timestamp}\",\"type\":\"token_usage_record\",\"payload\":{{\"turn_id\":\"{turnId}\",\"usage\":{{\"total_tokens\":{responseTokens}}},\"turn_token_usage\":{{\"total_tokens\":{turnTokens}}}}}}}";
    }
}
