using System.Text.Json.Nodes;
using CodexMeter.Windows.Services;

namespace CodexMeter.Windows.Tests;

[TestClass]
public sealed class UsagePayloadBuilderTests
{
    [TestMethod]
    public void Build_UsesDailyBucketWhenLocalTokenTotalIsZero()
    {
        var limits = JsonNode.Parse("""
            {"rateLimits":{"primary":{"usedPercent":18},"secondary":{"usedPercent":36}},"planType":"plus"}
            """)!.AsObject();
        var usage = JsonNode.Parse("""
            {"dailyUsageBuckets":[{"startDate":"2026-09-11","tokens":1234}]}
            """)!.AsObject();

        var payload = UsagePayloadBuilder.Build(
            new SessionStats(3, 0),
            limits,
            usage,
            DateTimeOffset.Parse("2026-09-11T12:00:00+08:00"));

        var today = payload["today"]!.AsObject();
        var rateLimits = payload["rateLimits"]!.AsObject();
        var primary = rateLimits["primary"]!.AsObject();
        Assert.AreEqual(1234L, today["tokens"]!.GetValue<long>());
        Assert.AreEqual(3, today["questions"]!.GetValue<int>());
        Assert.AreEqual(18, primary["usedPercent"]!.GetValue<int>());
    }
}
