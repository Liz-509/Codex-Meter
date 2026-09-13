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
}
