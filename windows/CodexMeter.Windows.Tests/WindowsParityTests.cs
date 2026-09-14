using System.Text.Json.Nodes;
using CodexMeter.Windows.Services;

namespace CodexMeter.Windows.Tests;

[TestClass]
public sealed class WindowsParityTests
{
    [TestMethod]
    public void RefreshPreferences_ValidatePersistAndExposeWhitelists()
    {
        var settings = new AppSettings
        {
            LiveRefreshIntervalSeconds = 3,
            GeneralRefreshIntervalSeconds = 12,
            SshRefreshIntervalSeconds = 5
        };
        var defaults = RefreshPreferences.From(settings);
        Assert.AreEqual(new RefreshPreferences(), defaults);

        var updated = defaults.Update(new JsonObject { ["liveSeconds"] = 1, ["generalSeconds"] = 120, ["sshSeconds"] = 30 });
        updated.Persist(settings);
        var payload = RefreshPreferences.From(settings).Payload(sshEnabled: true);
        CollectionAssert.AreEqual(new[] { 1, 2, 5, 10 }, payload["liveOptions"]!.AsArray().Select(item => item!.GetValue<int>()).ToArray());
        CollectionAssert.AreEqual(new[] { 15, 30, 60, 120 }, payload["generalOptions"]!.AsArray().Select(item => item!.GetValue<int>()).ToArray());
        CollectionAssert.AreEqual(new[] { 30, 60, 120, 300 }, payload["sshOptions"]!.AsArray().Select(item => item!.GetValue<int>()).ToArray());
        Assert.IsTrue(payload["sshEnabled"]!.GetValue<bool>());
    }

    [TestMethod]
    public void RemoteHostDiscoveryAndNetstat_OnlySelectSshConnectionsAndDeduplicateConfiguration()
    {
        var state = JsonNode.Parse("""
        {
          "codex-managed-remote-connections": [
            {"hostId":"remote-ssh-managed:one","displayName":"构建机","alias":"build","hostname":"192.168.0.71","sshPort":2222},
            {"hostId":"remote-ssh-managed:one","alias":"duplicate"},
            {"hostId":"not-ssh","hostname":"ignored"}
          ]
        }
        """)!.AsObject();
        var hosts = RemoteSessionCollector.DiscoverHosts(state);
        Assert.AreEqual(1, hosts.Count);
        Assert.AreEqual("构建机", hosts[0].DisplayName);
        Assert.AreEqual(2222, hosts[0].Port);

        var endpoints = RemoteSessionCollector.EstablishedSshRemoteEndpointKeys("""
          TCP    127.0.0.1:50100       192.168.0.71:2222      ESTABLISHED     42
          TCP    127.0.0.1:50101       192.168.0.72:22        ESTABLISHED     99
          TCP    [::1]:50102           [fe80::71]:2222        ESTABLISHED     42
        """, pid => pid == 42);
        CollectionAssert.AreEquivalent(new[] { "192.168.0.71|2222", "fe80::71|2222" }, endpoints.ToArray());

        var duplicate = new RemoteCodexHost("remote-ssh-managed:two", "同地址别名", "same", 2222, null);
        var selected = RemoteSessionCollector.SelectConnectedHosts(
            [hosts[0], duplicate],
            duplicate.Id,
            new Dictionary<string, IReadOnlyList<string>>
            {
                [hosts[0].Id] = ["192.168.0.71|2222"],
                [duplicate.Id] = ["192.168.0.71|2222"]
            },
            endpoints);
        Assert.AreEqual(1, selected.Count);
        Assert.AreEqual(duplicate.Id, selected[0].Id);
    }

    [TestMethod]
    public void RemoteSnapshots_AggregateTokensTasksProjectsAndCurrentContext()
    {
        var local = new SessionStats(
            0,
            10,
            [new DailyTokenStats("2026-09-14", 10)],
            [],
            [],
            []);
        var host = new RemoteCodexHost("remote-ssh-managed:one", "构建机", "build", 22, null);
        var payload = JsonNode.Parse("""
        {
          "dailyTokens":{"2026-09-14":42},
          "conversations":[{"turnId":"turn-远端","threadId":"thread-r","startedAt":"2026-09-14T10:00:00Z","date":"2026-09-14","projectPath":"/srv/repo","projectName":"服务端","threadName":"修复部署","preview":"中文问题","tokens":42}],
          "projectDailyTokens":[{"date":"2026-09-14","projectPath":"/srv/repo","projectName":"服务端","tokens":42}],
          "contextHealth":[{"threadId":"thread-r","projectPath":"/srv/repo","projectName":"服务端","preview":"中文问题","usedTokens":80,"maxTokens":100,"updatedAt":"2026-09-14T10:01:00Z","compactions":1,"conversationTokens":1000,"currentTurnId":"turn-远端","currentTurnTokens":42,"currentTurnActive":true}]
        }
        """)!.AsObject();

        var combined = CodexUsageService.StatsIncludingRemote(local, [new RemoteSessionSnapshot(host, payload)]);
        Assert.AreEqual(52L, combined.Tokens);
        Assert.AreEqual(1, combined.Questions);
        Assert.AreEqual("修复部署", combined.Conversations[0].ThreadName);
        Assert.AreEqual("构建机", combined.Conversations[0].SourceHost);
        Assert.AreEqual("服务端 · 构建机", combined.ProjectDailyTokens[0].ProjectName);
        Assert.AreEqual(1000L, combined.ContextHealth[0].ConversationTokens);
        Assert.AreEqual(42L, combined.ContextHealth[0].CurrentTurnTokens);
    }

    [TestMethod]
    public void FullSnapshot_ExportsConversationAndCurrentTurnTokens()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-meter-full-turns-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "session.jsonl");
            File.WriteAllLines(path,
            [
                "{\"timestamp\":\"2026-09-14T10:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"thread-a\",\"thread_source\":\"user\"}}",
                "{\"timestamp\":\"2026-09-14T10:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn-a\"}}",
                "{\"timestamp\":\"2026-09-14T10:00:02Z\",\"type\":\"token_usage_record\",\"payload\":{\"turn_id\":\"turn-a\",\"turn_token_usage\":{\"total_tokens\":90000}}}",
                "{\"timestamp\":\"2026-09-14T10:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":200000,\"last_token_usage\":{\"total_tokens\":100000},\"total_token_usage\":{\"total_tokens\":35000000}}}}",
                "{\"timestamp\":\"2026-09-14T10:00:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"turn-a\"}}",
                "{\"timestamp\":\"2026-09-14T10:00:05Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"turn-b\"}}",
                "{\"timestamp\":\"2026-09-14T10:00:06Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"model_context_window\":200000,\"last_token_usage\":{\"total_tokens\":120000},\"total_token_usage\":{\"total_tokens\":35040000}}}}"
            ]);
            var stats = SessionStatsReader.ReadToday(directory, DateTimeOffset.Parse("2026-09-14T12:00:00Z"), TimeZoneInfo.Utc);
            var context = stats.ContextHealth.Single();
            Assert.AreEqual(130_000L, context.ConversationTokens);
            Assert.AreEqual("turn-b", context.CurrentTurnId);
            Assert.AreEqual(40_000L, context.CurrentTurnTokens);
            Assert.IsTrue(context.CurrentTurnActive == true);
            var payload = UsagePayloadBuilder.CreateLocal(stats);
            Assert.AreEqual(130_000L, payload["contextHealth"]!["sessions"]![0]!["conversationTokens"]!.GetValue<long>());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
