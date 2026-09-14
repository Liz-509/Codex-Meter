using System.Text.Json;
using System.Text.Json.Nodes;
using CodexMeter.Windows.Services;

namespace CodexMeter.Windows.Tests;

[TestClass]
public sealed class LiveContextReaderTests
{
    [TestMethod]
    public void CreatePayload_TracksLatestMeasurementCompactionAndFileGrowth()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-meter-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "current.jsonl");
            File.WriteAllLines(path,
            [
                Token("2033-05-13T10:00:00Z", 50_000, 100_000),
                "{\"timestamp\":\"2033-05-13T10:01:00Z\",\"type\":\"compacted\",\"payload\":{\"window_id\":\"window-2\"}}",
                Token("2033-05-13T10:02:00Z", 75_000, 100_000)
            ]);
            var reader = new LiveContextReader();
            var thread = new JsonObject { ["id"] = "thread-a", ["name"] = "当前任务", ["path"] = path, ["cwd"] = directory };

            var first = reader.CreatePayload(thread)["contextHealth"]!["session"]!.AsObject();
            Assert.AreEqual(75_000L, first["usedTokens"]!.GetValue<long>());
            Assert.AreEqual(100_000L, first["maxTokens"]!.GetValue<long>());
            Assert.AreEqual("window-2", first["contextWindowId"]!.GetValue<string>());
            Assert.AreEqual(1, first["compactions"]!.GetValue<int>());
            Assert.AreEqual("attention", first["status"]!.GetValue<string>());

            File.AppendAllLines(path, [Token("2033-05-13T10:03:00Z", 100_000, 100_000)]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
            var second = reader.CreatePayload(thread)["contextHealth"]!["session"]!.AsObject();
            Assert.AreEqual(100_000L, second["usedTokens"]!.GetValue<long>());
            Assert.AreEqual("critical", second["status"]!.GetValue<string>());

            File.AppendAllLines(path, ["{broken-json"]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            var retained = reader.CreatePayload(thread)["contextHealth"]!["session"]!.AsObject();
            Assert.AreEqual(100_000L, retained["usedTokens"]!.GetValue<long>(), "读取失败时应保留旧测量");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void CreatePayload_SwitchesCurrentTaskAndHandlesMissingOrInvalidLimits()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-meter-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var valid = Path.Combine(directory, "valid.jsonl");
            var invalid = Path.Combine(directory, "invalid.jsonl");
            File.WriteAllText(valid, Token("2033-05-13T10:00:00Z", 59_000, 100_000));
            File.WriteAllText(invalid, Token("2033-05-13T10:00:00Z", 59_000, 0));
            var reader = new LiveContextReader();

            var first = reader.CreatePayload(new JsonObject { ["id"] = "thread-a", ["path"] = valid });
            Assert.AreEqual("thread-a", first["contextHealth"]!["currentTaskId"]!.GetValue<string>());
            Assert.AreEqual("healthy", first["contextHealth"]!["session"]!["status"]!.GetValue<string>());

            var second = reader.CreatePayload(new JsonObject { ["id"] = "thread-b", ["preview"] = "第二个任务", ["path"] = invalid });
            Assert.AreEqual("thread-b", second["contextHealth"]!["currentTaskId"]!.GetValue<string>());
            Assert.AreEqual("第二个任务", second["contextHealth"]!["currentTaskName"]!.GetValue<string>());
            Assert.IsNull(second["contextHealth"]!["session"], "无效上下文上限不得输出虚假健康度");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void CreatePayload_PrefersAuthoritativeTurnUsageAndTracksLegacyTurnsIncrementally()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-meter-live-turns-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "current.jsonl");
            File.WriteAllLines(path,
            [
                Event("2033-05-13T10:00:00Z", "task_started", "turn-a"),
                TokenWithTotal("2033-05-13T10:00:01Z", 10_000, 200_000, 35_000_000),
                Usage("2033-05-13T10:00:02Z", "turn-a", 95_000),
                TokenWithTotal("2033-05-13T10:00:03Z", 11_000, 200_000, 70_000_000),
                Event("2033-05-13T10:00:04Z", "task_complete", "turn-a"),
                Event("2033-05-13T10:00:05Z", "task_started", "turn-b"),
                TokenWithTotal("2033-05-13T10:00:06Z", 20_000, 200_000, 70_025_000)
            ]);
            var reader = new LiveContextReader();
            var thread = new JsonObject { ["id"] = "thread-a", ["path"] = path };
            var session = reader.CreatePayload(thread)["contextHealth"]!["session"]!.AsObject();
            Assert.AreEqual(120_000L, session["conversationTokens"]!.GetValue<long>());
            Assert.AreEqual("turn-b", session["currentTurnId"]!.GetValue<string>());
            Assert.AreEqual(25_000L, session["currentTurnTokens"]!.GetValue<long>());
            Assert.IsTrue(session["currentTurnActive"]!.GetValue<bool>());

            var partial = Usage("2033-05-13T10:00:07Z", "turn-b", 40_000);
            File.AppendAllText(path, partial[..^2]);
            var unchanged = reader.CreatePayload(thread)["contextHealth"]!["session"]!.AsObject();
            Assert.AreEqual(25_000L, unchanged["currentTurnTokens"]!.GetValue<long>());
            File.AppendAllText(path, partial[^2..] + Environment.NewLine);
            var completed = reader.CreatePayload(thread)["contextHealth"]!["session"]!.AsObject();
            Assert.AreEqual(40_000L, completed["currentTurnTokens"]!.GetValue<long>());
            Assert.AreEqual(135_000L, completed["conversationTokens"]!.GetValue<long>());

            File.WriteAllLines(path,
            [
                Event("2033-05-13T11:00:00Z", "task_started", "turn-c"),
                TokenWithTotal("2033-05-13T11:00:01Z", 5_000, 200_000, 10_000)
            ]);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(3));
            var replaced = reader.CreatePayload(thread)["contextHealth"]!["session"]!.AsObject();
            Assert.AreEqual("turn-c", replaced["currentTurnId"]!.GetValue<string>());
            Assert.AreEqual(10_000L, replaced["conversationTokens"]!.GetValue<long>());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(59d, "healthy")]
    [DataRow(60d, "attention")]
    [DataRow(80d, "high")]
    [DataRow(90d, "critical")]
    public void ContextStatus_UsesMacBoundaryRules(double usedPercent, string expected) =>
        Assert.AreEqual(expected, SessionStatsReader.ContextHealthStatus(usedPercent));

    private static string Token(string timestamp, long used, long maximum) => JsonSerializer.Serialize(new
    {
        timestamp,
        type = "event_msg",
        payload = new { type = "token_count", info = new { model_context_window = maximum, last_token_usage = new { total_tokens = used } } }
    });

    private static string TokenWithTotal(string timestamp, long used, long maximum, long total) => JsonSerializer.Serialize(new
    {
        timestamp,
        type = "event_msg",
        payload = new { type = "token_count", info = new { model_context_window = maximum, last_token_usage = new { total_tokens = used }, total_token_usage = new { total_tokens = total } } }
    });

    private static string Event(string timestamp, string type, string turnId) => JsonSerializer.Serialize(new
    {
        timestamp,
        type = "event_msg",
        payload = new { type, turn_id = turnId }
    });

    private static string Usage(string timestamp, string turnId, long tokens) => JsonSerializer.Serialize(new
    {
        timestamp,
        type = "token_usage_record",
        payload = new { turn_id = turnId, turn_token_usage = new { total_tokens = tokens } }
    });
}
