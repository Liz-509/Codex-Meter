using System.Text.Json.Nodes;
using CodexMeter.Windows.Services;

namespace CodexMeter.Windows.Tests;

[TestClass]
public sealed class UsagePayloadBuilderTests
{
    [TestMethod]
    public void Build_UsesAccountHistoryAndKeepsTodayInSync()
    {
        var limits = JsonNode.Parse("""
            {"rateLimits":{"primary":{"usedPercent":18},"secondary":{"usedPercent":36}},"planType":"plus"}
            """)!.AsObject();
        var usage = JsonNode.Parse("""
            {"dailyUsageBuckets":[{"startDate":"2026-09-11","tokens":1234}]}
            """)!.AsObject();

        var payload = UsagePayloadBuilder.Build(
            new SessionStats(3, 999),
            limits,
            usage,
            DateTimeOffset.Parse("2026-09-11T00:30:00+08:00"));

        var today = payload["today"]!.AsObject();
        var rateLimits = payload["rateLimits"]!.AsObject();
        var primary = rateLimits["primary"]!.AsObject();
        Assert.AreEqual(1234L, today["tokens"]!.GetValue<long>());
        Assert.AreEqual(3, today["questions"]!.GetValue<int>());
        Assert.AreEqual("account", today["tokenSource"]!.GetValue<string>());
        Assert.AreEqual(18, primary["usedPercent"]!.GetValue<int>());
        var history = payload["history"]!.AsObject();
        var days = history["dailyTokens"]!.AsArray();
        Assert.AreEqual("account", history["source"]!.GetValue<string>());
        Assert.AreEqual(7, days.Count);
        Assert.AreEqual("2026-09-05", days[0]!["date"]!.GetValue<string>());
        Assert.AreEqual(0L, days[0]!["tokens"]!.GetValue<long>());
        Assert.AreEqual(1234L, days[^1]!["tokens"]!.GetValue<long>());
    }

    [TestMethod]
    public void Build_KeepsLocalHistoryWhenDailyBucketsAreUnavailable()
    {
        var stats = new SessionStats(
            1,
            42,
            [new DailyTokenStats("2026-09-11", 42)],
            [new ConversationStats(
                "turn-a",
                "thread-a",
                "window-a",
                DateTimeOffset.Parse("2026-09-11T09:30:00+08:00"),
                "测试对话",
                42)]);

        var payload = UsagePayloadBuilder.Build(
            stats,
            new JsonObject(),
            new JsonObject { ["dailyUsageBuckets"] = new JsonArray() },
            DateTimeOffset.Parse("2026-09-11T12:00:00+08:00"),
            new Dictionary<string, string> { ["thread-a"] = "Codex 中的任务名称" });

        var today = payload["today"]!.AsObject();
        var history = payload["history"]!.AsObject();
        var conversation = today["conversations"]!.AsArray()[0]!.AsObject();
        Assert.AreEqual(42L, today["tokens"]!.GetValue<long>());
        Assert.AreEqual("local", today["tokenSource"]!.GetValue<string>());
        Assert.AreEqual("local", history["source"]!.GetValue<string>());
        Assert.AreEqual("测试对话", conversation["preview"]!.GetValue<string>());
        Assert.AreEqual("window-a", conversation["contextWindowId"]!.GetValue<string>());
        Assert.AreEqual("Codex 中的任务名称", conversation["threadName"]!.GetValue<string>());
        Assert.AreEqual(42L, conversation["tokens"]!.GetValue<long>());
    }

    [TestMethod]
    public void Build_UsesLocalValuesForDatesMissingFromAccountHistory()
    {
        var stats = new SessionStats(
            1,
            42,
            [
                new DailyTokenStats("2026-09-10", 90),
                new DailyTokenStats("2026-09-11", 42)
            ],
            []);
        var usage = JsonNode.Parse("""
            {"dailyUsageBuckets":[{"startDate":"2026-09-10","tokens":100}]}
            """)!.AsObject();

        var payload = UsagePayloadBuilder.Build(
            stats,
            new JsonObject(),
            usage,
            DateTimeOffset.Parse("2026-09-11T00:30:00+08:00"));

        var today = payload["today"]!.AsObject();
        var history = payload["history"]!.AsObject();
        var days = history["dailyTokens"]!.AsArray();
        Assert.AreEqual(42L, today["tokens"]!.GetValue<long>());
        Assert.AreEqual("local", today["tokenSource"]!.GetValue<string>());
        Assert.IsTrue(history["localFallback"]!.GetValue<bool>());
        Assert.AreEqual(100L, days[^2]!["tokens"]!.GetValue<long>());
        Assert.AreEqual(42L, days[^1]!["tokens"]!.GetValue<long>());
    }
}
