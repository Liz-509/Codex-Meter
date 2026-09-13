using System.Text.Json;
using System.Text.Json.Nodes;
using CodexMeter.Windows.Services;

namespace CodexMeter.Windows.Tests;

[TestClass]
public sealed class SessionInsightsTests
{
    [TestMethod]
    public void Reader_AggregatesNinetyDaysUserTasksAndSubagentGap()
    {
        using var files = new TemporaryTree();
        var project = files.Directory("ExampleProject");
        Directory.CreateDirectory(Path.Combine(project, ".git"));
        files.WriteSession("user.jsonl",
        [
            Meta("2033-05-13T10:00:00Z", "thread-user", project, user: true),
            Started("2033-05-13T10:01:00Z", "turn-user"),
            Message("2033-05-13T10:01:10Z", "实现项目统计"),
            Usage("2033-05-13T10:02:00Z", "turn-user", 50),
            Started("2033-02-13T00:00:00Z", "turn-boundary"),
            Message("2033-02-13T00:00:10Z", "边界任务"),
            Usage("2033-02-13T00:01:00Z", "turn-boundary", 11),
            Started("2033-02-12T23:00:00Z", "turn-old"),
            Usage("2033-02-12T23:01:00Z", "turn-old", 99)
        ]);
        files.WriteSession("subagent.jsonl",
        [
            Meta("2033-05-13T10:03:00Z", "thread-agent", project, user: false),
            Started("2033-05-13T10:04:00Z", "turn-agent"),
            Usage("2033-05-13T10:05:00Z", "turn-agent", 30)
        ]);

        var stats = SessionStatsReader.ReadToday(files.Sessions, DateTimeOffset.Parse("2033-05-13T12:00:00Z"), TimeZoneInfo.Utc);
        Assert.AreEqual(90, stats.DailyTokens.Count);
        Assert.AreEqual("2033-02-13", stats.DailyTokens[0].Date);
        Assert.AreEqual(11L, stats.DailyTokens[0].Tokens);
        Assert.AreEqual(80L, stats.DailyTokens[^1].Tokens);
        Assert.AreEqual(1, stats.Questions);

        var payload = UsagePayloadBuilder.CreateLocal(stats, new Dictionary<string, string> { ["thread-user"] = "App Server 任务名称" });
        var tasks = payload["insights"]!["tasks"]!.AsArray().OfType<JsonObject>().ToArray();
        Assert.IsTrue(tasks.Any(row => row["name"]!.GetValue<string>() == "App Server 任务名称" && row["tokens"]!.GetValue<long>() == 50));
        Assert.IsTrue(tasks.Any(row => row["name"]!.GetValue<string>() == "系统/子代理活动" && row["tokens"]!.GetValue<long>() == 30));
        Assert.IsFalse(tasks.Any(row => row["taskId"]?.GetValue<string>() == "turn-old"));
    }

    [TestMethod]
    public void Reader_UsesLatestUserContextAfterCompactionAndExcludesSubagents()
    {
        using var files = new TemporaryTree();
        files.WriteSession("user.jsonl",
        [
            Meta("2033-05-13T10:00:00Z", "thread-health", files.Root, user: true, window: "window-1"),
            Started("2033-05-13T10:00:10Z", "turn-health"),
            Message("2033-05-13T10:00:20Z", "检查上下文"),
            Token("2033-05-13T10:01:00Z", 85_000, 100_000, 85_000),
            "{\"timestamp\":\"2033-05-13T10:02:00Z\",\"type\":\"compacted\",\"payload\":{\"window_id\":\"window-2\"}}",
            Token("2033-05-13T10:03:00Z", 25_000, 100_000, 110_000)
        ]);
        files.WriteSession("agent.jsonl",
        [
            Meta("2033-05-13T11:00:00Z", "thread-agent", files.Root, user: false),
            Token("2033-05-13T11:01:00Z", 95_000, 100_000, 95_000)
        ]);

        var stats = SessionStatsReader.ReadToday(files.Sessions, DateTimeOffset.Parse("2033-05-13T12:00:00Z"), TimeZoneInfo.Utc);
        Assert.HasCount(1, stats.ContextHealth);
        var context = stats.ContextHealth[0];
        Assert.AreEqual("thread-health", context.ThreadId);
        Assert.AreEqual("window-2", context.ContextWindowId);
        Assert.AreEqual(25_000L, context.UsedTokens);
        Assert.AreEqual(1, context.Compactions);
        Assert.AreEqual("检查上下文", context.Preview);
    }

    [TestMethod]
    public void ProjectResolver_SupportsGitWorktreesNonProjectsAndSameNameDisambiguation()
    {
        using var files = new TemporaryTree();
        var repo = files.Directory(Path.Combine("ParentA", "Shared"));
        Directory.CreateDirectory(Path.Combine(repo, ".git", "worktrees", "feature"));
        var worktree = files.Directory("Worktree");
        File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {Path.Combine(repo, ".git", "worktrees", "feature")}");
        var second = files.Directory(Path.Combine("ParentB", "Shared"));
        Directory.CreateDirectory(Path.Combine(second, ".git"));
        var loose = files.Directory("Loose");

        Assert.AreEqual(repo, ProjectResolver.Resolve(Path.Combine(repo, "src")));
        Assert.AreEqual(repo, ProjectResolver.Resolve(worktree));
        Assert.AreEqual(ProjectResolver.NonProjectKey, ProjectResolver.Resolve(loose));
        var names = ProjectResolver.DisplayNames([repo, second, ProjectResolver.NonProjectKey]);
        Assert.AreEqual("Shared — ParentA", names[repo]);
        Assert.AreEqual("Shared — ParentB", names[second]);
        Assert.AreEqual("非项目中对话", names[ProjectResolver.NonProjectKey]);
    }

    private static string Meta(string timestamp, string id, string cwd, bool user, string? window = null)
    {
        var source = user ? "\"thread_source\":\"user\"" : "\"source\":{\"subagent\":{\"name\":\"helper\"}}";
        var context = window is null ? "" : $",\"context_window\":{{\"window_id\":\"{window}\"}}";
        return $"{{\"timestamp\":\"{timestamp}\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{id}\",\"cwd\":{JsonValue.Create(cwd)!.ToJsonString()},{source}{context}}}}}";
    }
    private static string Started(string timestamp, string turn) => $"{{\"timestamp\":\"{timestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"task_started\",\"turn_id\":\"{turn}\"}}}}";
    private static string Message(string timestamp, string message) => $"{{\"timestamp\":\"{timestamp}\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"user_message\",\"message\":{JsonValue.Create(message)!.ToJsonString()}}}}}";
    private static string Usage(string timestamp, string turn, long tokens) => JsonSerializer.Serialize(new
    {
        timestamp,
        type = "token_usage_record",
        payload = new { turn_id = turn, usage = new { total_tokens = tokens }, turn_token_usage = new { total_tokens = tokens } }
    });
    private static string Token(string timestamp, long used, long maximum, long total) => JsonSerializer.Serialize(new
    {
        timestamp,
        type = "event_msg",
        payload = new { type = "token_count", info = new { model_context_window = maximum, last_token_usage = new { total_tokens = used }, total_token_usage = new { total_tokens = total } } }
    });

    private sealed class TemporaryTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"codex-meter-insights-{Guid.NewGuid():N}");
        public string Sessions { get; }
        public TemporaryTree() { Sessions = Path.Combine(Root, "sessions"); System.IO.Directory.CreateDirectory(Sessions); }
        public string Directory(string relative) { var path = Path.Combine(Root, relative); System.IO.Directory.CreateDirectory(path); return path; }
        public void WriteSession(string name, IEnumerable<string> lines)
        {
            var path = Path.Combine(Sessions, name);
            File.WriteAllLines(path, lines);
            File.SetLastWriteTimeUtc(path, DateTime.Parse("2033-05-13T12:00:00Z").ToUniversalTime());
        }
        public void Dispose() => System.IO.Directory.Delete(Root, recursive: true);
    }
}
