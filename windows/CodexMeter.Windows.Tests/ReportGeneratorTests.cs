using System.Text;
using System.Text.Json.Nodes;
using CodexMeter.Windows.Services;

namespace CodexMeter.Windows.Tests;

[TestClass]
public sealed class ReportGeneratorTests
{
    [TestMethod]
    public void Markdown_UsesOnlyRecentSevenDaysAndEscapesTables()
    {
        var markdown = ReportGenerator.Markdown(Payload(), DateTimeOffset.Parse("2033-05-13T12:00:00+08:00"));

        StringAssert.Contains(markdown, "# Codex Meter 最近 7 天报告");
        StringAssert.Contains(markdown, "- Tokens：200");
        StringAssert.Contains(markdown, "- 对话轮次：3");
        StringAssert.Contains(markdown, "- 活跃项目：1");
        StringAssert.Contains(markdown, "修复 \"导出\" \\| 测试");
        StringAssert.Contains(markdown, "非项目中对话");
        Assert.IsFalse(markdown.Contains("2033-05-06", StringComparison.Ordinal), "八天前的数据不得进入报告");
    }

    [TestMethod]
    public void Csv_HasUtf8BomChineseAndRfcEscaping()
    {
        var bytes = ReportGenerator.Csv(Payload(), DateTimeOffset.Parse("2033-05-13T12:00:00+08:00"));
        CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);
        StringAssert.StartsWith(text, "日期,项目,任务,轮次,Tokens,最后活动时间,数据来源\r\n");
        StringAssert.Contains(text, "\"项目,甲\"");
        StringAssert.Contains(text, "\"修复 \"\"导出\"\" | 测试\"");
        StringAssert.Contains(text, "非项目中对话,临时问答");
        Assert.IsFalse(text.Contains("过期任务", StringComparison.Ordinal));
    }

    private static JsonObject Payload() => new()
    {
        ["history"] = new JsonObject
        {
            ["dailyTokens"] = new JsonArray
            {
                new JsonObject { ["date"] = "2033-05-06", ["tokens"] = 900, ["source"] = "account" },
                new JsonObject { ["date"] = "2033-05-12", ["tokens"] = 120, ["source"] = "account" },
                new JsonObject { ["date"] = "2033-05-13", ["tokens"] = 80, ["source"] = "local" }
            }
        },
        ["insights"] = new JsonObject
        {
            ["tasks"] = new JsonArray
            {
                new JsonObject { ["date"] = "2033-05-06", ["projectName"] = "旧项目", ["projectKind"] = "project", ["name"] = "过期任务", ["turns"] = 9, ["tokens"] = 900 },
                new JsonObject { ["date"] = "2033-05-13", ["projectName"] = "项目,甲", ["projectKind"] = "project", ["name"] = "修复 \"导出\" | 测试", ["turns"] = 2, ["tokens"] = 80, ["lastActive"] = "2033-05-13T10:00:00Z" },
                new JsonObject { ["date"] = "2033-05-13", ["projectName"] = "非项目中对话", ["projectKind"] = "non_project", ["name"] = "临时问答", ["turns"] = 1, ["tokens"] = 20, ["lastActive"] = "2033-05-13T11:00:00Z" }
            }
        }
    };
}
