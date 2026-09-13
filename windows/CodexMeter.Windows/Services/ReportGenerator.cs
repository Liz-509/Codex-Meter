using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexMeter.Windows.Services;

internal static class ReportGenerator
{
    private sealed record Day(string Date, long Tokens, string Source);
    private sealed record TaskRow(string Date, string Project, string ProjectKind, string Name, long Turns, long Tokens, string LastActive);
    private sealed record Total(string Name, string Project, long Turns, long Tokens);

    public static string Markdown(JsonObject payload, DateTimeOffset? now = null)
    {
        var (end, days, tasks) = Data(payload, now ?? DateTimeOffset.Now);
        var projectTotals = tasks.GroupBy(item => item.Project).Select(group => new Total(group.Key, group.Key, group.Sum(item => item.Turns), group.Sum(item => item.Tokens))).OrderByDescending(item => item.Tokens).ToArray();
        var taskTotals = tasks.GroupBy(item => $"{item.Project}\0{item.Name}").Select(group => new Total(group.First().Name, group.First().Project, group.Sum(item => item.Turns), group.Sum(item => item.Tokens))).OrderByDescending(item => item.Tokens).ToArray();
        var lines = new List<string>
        {
            "# Codex Meter 最近 7 天报告", "", $"- 报告日期：{end}", $"- Tokens：{days.Sum(item => item.Tokens)}",
            $"- 对话轮次：{tasks.Sum(item => item.Turns)}", $"- 活跃项目：{tasks.Where(item => item.ProjectKind != "non_project").Select(item => item.Project).Distinct().Count()}",
            "", "## 每日趋势", "", "| 日期 | Tokens | 数据来源 |", "| --- | ---: | --- |"
        };
        lines.AddRange(days.Select(day => $"| {day.Date} | {day.Tokens} | {SourceLabel(day.Source)} |"));
        lines.AddRange(["", "## 项目排行", "", "| 项目 | Tokens | 轮次 |", "| --- | ---: | ---: |"]);
        lines.AddRange(projectTotals.Take(20).Select(item => $"| {MarkdownEscape(item.Name)} | {item.Tokens} | {item.Turns} |"));
        lines.AddRange(["", "## Top 任务", "", "| 任务 | 项目 | Tokens | 轮次 |", "| --- | --- | ---: | ---: |"]);
        lines.AddRange(taskTotals.Take(20).Select(item => $"| {MarkdownEscape(item.Name)} | {MarkdownEscape(item.Project)} | {item.Tokens} | {item.Turns} |"));
        lines.AddRange(["", "> 整体每日用量优先采用账户数据，缺失日期由本地会话补齐；项目和任务统计仅来自本机 Codex 会话。", ""]);
        return string.Join("\n", lines);
    }

    public static byte[] Csv(JsonObject payload, DateTimeOffset? now = null)
    {
        var (_, _, tasks) = Data(payload, now ?? DateTimeOffset.Now);
        var rows = new List<string[]> { new[] { "日期", "项目", "任务", "轮次", "Tokens", "最后活动时间", "数据来源" } };
        rows.AddRange(tasks.Select(item => new[] { item.Date, item.Project, item.Name, item.Turns.ToString(CultureInfo.InvariantCulture), item.Tokens.ToString(CultureInfo.InvariantCulture), item.LastActive, "本机" }));
        var text = string.Join("\r\n", rows.Select(row => string.Join(",", row.Select(CsvEscape)))) + "\r\n";
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray();
    }

    private static (string End, Day[] Days, TaskRow[] Tasks) Data(JsonObject payload, DateTimeOffset now)
    {
        var endDate = now.Date;
        var startKey = endDate.AddDays(-6).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var endKey = endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var days = (payload["history"]?["dailyTokens"] as JsonArray)?.OfType<JsonObject>()
            .Select(item => new Day(Text(item["date"]), Integer(item["tokens"]), Text(item["source"], "local")))
            .Where(item => string.CompareOrdinal(item.Date, startKey) >= 0 && string.CompareOrdinal(item.Date, endKey) <= 0).ToArray() ?? [];
        var tasks = (payload["insights"]?["tasks"] as JsonArray)?.OfType<JsonObject>()
            .Select(item => new TaskRow(Text(item["date"]), Text(item["projectName"], "未识别项目"), Text(item["projectKind"], "project"), Text(item["name"], "未命名任务"), Integer(item["turns"]), Integer(item["tokens"]), Text(item["lastActive"])))
            .Where(item => string.CompareOrdinal(item.Date, startKey) >= 0 && string.CompareOrdinal(item.Date, endKey) <= 0)
            .OrderBy(item => item.Date).ThenByDescending(item => item.Tokens).ToArray() ?? [];
        return (endKey, days, tasks);
    }

    private static string MarkdownEscape(string value) => value.Replace("|", "\\|").Replace('\r', ' ').Replace('\n', ' ');
    private static string CsvEscape(string value) => value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";
    private static string SourceLabel(string source) => source == "account" ? "账户" : source == "empty" ? "无记录" : "本机";
    private static string Text(JsonNode? node, string fallback = "") => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;
    private static long Integer(JsonNode? node)
    {
        if (node is not JsonValue value) return 0;
        if (value.TryGetValue<long>(out var number)) return number;
        if (value.TryGetValue<int>(out var integer)) return integer;
        if (value.TryGetValue<double>(out var floating)) return (long)floating;
        return 0;
    }
}
