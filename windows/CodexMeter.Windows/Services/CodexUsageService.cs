using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexMeter.Windows.Services;

internal sealed class CodexUsageService : IDisposable
{
    private sealed record AppServerRequest(string Method, JsonObject? Params = null);

    private readonly CodexExecutableLocator _locator;
    private readonly SessionStatsCache _sessionStats = new();
    private readonly LiveContextReader _liveContextReader = new();
    private readonly RemoteSessionCollector _remoteSessions = new();
    private readonly SemaphoreSlim _serverGate = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _responses = new();
    private readonly StringBuilder _serverErrors = new();
    private CodexCommand? _cachedCommand;
    private Process? _serverProcess;
    private CancellationTokenSource? _serverCancellation;
    private Task? _serverOutputPump;
    private Task? _serverErrorPump;
    private int _nextRequestId;
    private bool _disposed;
    private JsonObject? _cachedLimits;
    private JsonObject? _cachedUsage;
    private IReadOnlyDictionary<string, string> _cachedThreadNames = new Dictionary<string, string>();
    private readonly Dictionary<string, RemoteSessionSnapshot> _cachedRemoteSnapshots = new(StringComparer.Ordinal);

    public CodexUsageService(CodexExecutableLocator? locator = null)
    {
        _locator = locator ?? new CodexExecutableLocator();
    }

    public async Task<JsonObject> FetchAsync(
        Func<JsonObject, Task>? partialCallback,
        CancellationToken cancellationToken) =>
        await FetchAsync(
            includeRemoteSessions: false,
            refreshAccount: true,
            refreshRemoteSessions: false,
            partialCallback: partialCallback,
            cancellationToken: cancellationToken);

    public async Task<JsonObject> FetchAsync(
        bool includeRemoteSessions,
        bool refreshAccount,
        bool refreshRemoteSessions,
        Func<JsonObject, Task>? partialCallback,
        CancellationToken cancellationToken)
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        var stats = await Task.Run(
            () => _sessionStats.ReadRange(
                Path.Combine(codexHome, "sessions"),
                DateTimeOffset.Now,
                TimeZoneInfo.Local),
            cancellationToken);

        var cachedStats = includeRemoteSessions ? StatsIncludingRemote(stats, _cachedRemoteSnapshots.Values) : stats;
        var partial = CreateLocalPayload(cachedStats);
        partial["partial"] = true;
        partial["syncMessage"] = includeRemoteSessions && refreshRemoteSessions
            ? refreshAccount ? "正在同步远程会话与额度" : "正在同步远程会话"
            : "正在同步额度";
        if (partialCallback is not null) await partialCallback(partial);

        Exception? accountError = null;
        try
        {
            if (includeRemoteSessions && refreshRemoteSessions)
            {
                var connected = await Task.Run(() => _remoteSessions.ConnectedHosts(codexHome), cancellationToken);
                var connectedIds = connected.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var stale in _cachedRemoteSnapshots.Keys.Where(id => !connectedIds.Contains(id)).ToArray()) _cachedRemoteSnapshots.Remove(stale);
                var snapshots = await _remoteSessions.CollectAsync(codexHome, DateTimeOffset.Now, stats.DailyTokens.Select(day => day.Date).ToArray(), cancellationToken);
                foreach (var snapshot in snapshots) _cachedRemoteSnapshots[snapshot.Host.Id] = snapshot;
            }
            if (refreshAccount)
            {
                try
                {
                    var account = await ReadAccountDataAsync(cancellationToken);
                    _cachedLimits = (JsonObject)account.Limits.DeepClone();
                    _cachedUsage = (JsonObject)account.Usage.DeepClone();
                    _cachedThreadNames = account.ThreadNames;
                }
                catch (Exception error) when (error is not OperationCanceledException) { accountError = error; }
            }
            var completeStats = includeRemoteSessions ? StatsIncludingRemote(stats, _cachedRemoteSnapshots.Values) : stats;
            JsonObject result;
            if (_cachedLimits is not null && _cachedUsage is not null)
                result = UsagePayloadBuilder.Build(completeStats, _cachedLimits, _cachedUsage, DateTimeOffset.Now, _cachedThreadNames);
            else
                result = CreateLocalPayload(completeStats);
            if (accountError is not null && refreshAccount) result["error"] = accountError.Message;
            return result;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var payload = CreateLocalPayload(includeRemoteSessions ? StatsIncludingRemote(stats, _cachedRemoteSnapshots.Values) : stats);
            payload["error"] = error.Message;
            return payload;
        }
    }

    public int ConnectedRemoteHostCount()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome)) codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        return _remoteSessions.ConnectedHosts(codexHome).Count;
    }

    public void ClearRemoteSessionCache() => _cachedRemoteSnapshots.Clear();

    public async Task<JsonObject> ConsumeResetCreditAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var responses = await SendAccountRequestsAsync(
                [new AppServerRequest(
                    "account/rateLimitResetCredit/consume",
                    new JsonObject { ["idempotencyKey"] = idempotencyKey })],
                cancellationToken);
            var result = GetResult(responses[0], "重置");
            var outcome = result["outcome"]?.GetValue<string>()
                ?? throw new InvalidOperationException("未收到重置结果。");
            return new JsonObject { ["outcome"] = outcome };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new JsonObject { ["error"] = error.Message };
        }
    }

    private async Task<(JsonObject Limits, JsonObject Usage, IReadOnlyDictionary<string, string> ThreadNames)>
        ReadAccountDataAsync(CancellationToken cancellationToken)
    {
        var responses = await SendAccountRequestsAsync(
            [
                new AppServerRequest("account/rateLimits/read"),
                new AppServerRequest("account/usage/read"),
                new AppServerRequest(
                    "thread/list",
                    new JsonObject { ["limit"] = 100, ["sortKey"] = "updated_at" })
            ],
            cancellationToken);
        return (
            GetResult(responses[0], "额度"),
            GetResult(responses[1], "Token"),
            GetThreadNames(responses[2]));
    }

    private async Task<JsonObject[]> SendAccountRequestsAsync(
        IReadOnlyList<AppServerRequest> requests,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _serverGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await EnsureServerAsync(cancellationToken);
                    return await SendBatchAsync(requests, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    await ResetServerAsync();
                    throw;
                }
                catch (Exception) when (attempt == 0)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    await ResetServerAsync();
                    _cachedCommand = null;
                }
            }
        }
        finally
        {
            _serverGate.Release();
        }
    }

    public async Task<JsonObject?> FetchCurrentContextHealthAsync(CancellationToken cancellationToken, int pollIntervalSeconds = 2)
    {
        try
        {
            var responses = await SendAccountRequestsAsync(
                [new AppServerRequest("thread/list", new JsonObject
                {
                    ["limit"] = 8,
                    ["sortKey"] = "recency_at",
                    ["sortDirection"] = "desc",
                    ["useStateDbOnly"] = true
                })], cancellationToken);
            var result = GetResult(responses[0], "当前对话");
            if (result["data"] is not JsonArray threads) return null;
            var current = threads.OfType<JsonObject>().FirstOrDefault(thread =>
                thread["parentThreadId"] is null &&
                !(thread["ephemeral"] is JsonValue value && value.TryGetValue<bool>(out var ephemeral) && ephemeral));
            return current is null ? null : _liveContextReader.CreatePayload(current, pollIntervalSeconds);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task EnsureServerAsync(CancellationToken cancellationToken)
    {
        if (_serverProcess is { HasExited: false }) return;
        await ResetServerAsync();

        _cachedCommand ??= _locator.FindFromEnvironment()
            ?? throw new InvalidOperationException("未找到 Codex。请安装 Windows 版 Codex，或设置 CODEX_BINARY / CODEX_CLI_PATH。");
        var process = new Process { StartInfo = _cachedCommand.CreateStartInfo(), EnableRaisingEvents = true };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Codex App Server 启动失败。");
        }

        _serverErrors.Clear();
        _serverProcess = process;
        process.Exited += OnServerExited;
        _serverCancellation = new CancellationTokenSource();
        _serverOutputPump = PumpOutputAsync(process.StandardOutput, _responses, _serverCancellation.Token);
        _serverErrorPump = PumpErrorsAsync(process.StandardError, _serverCancellation.Token);

        var initializeId = NextRequestId();
        var initialize = Register(_responses, initializeId);
        await SendAsync(process.StandardInput, new JsonObject
        {
            ["method"] = "initialize",
            ["id"] = initializeId,
            ["params"] = new JsonObject
            {
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "codex_usage_widget",
                    ["title"] = "Codex Meter",
                    ["version"] = "1.4.8"
                }
            }
        });
        try
        {
            ThrowIfError(await initialize.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));
        }
        finally
        {
            _responses.TryRemove(initializeId, out _);
        }

        await SendAsync(process.StandardInput, new JsonObject
        {
            ["method"] = "initialized",
            ["params"] = new JsonObject()
        });
    }

    private async Task<JsonObject[]> SendBatchAsync(
        IReadOnlyList<AppServerRequest> requests,
        CancellationToken cancellationToken)
    {
        var process = _serverProcess
            ?? throw new InvalidOperationException("Codex App Server 尚未启动。");
        var pending = new List<(int Id, Task<JsonObject> Task)>(requests.Count);
        try
        {
            foreach (var request in requests)
            {
                var id = NextRequestId();
                var response = Register(_responses, id);
                pending.Add((id, response.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken)));
                var message = new JsonObject
                {
                    ["method"] = request.Method,
                    ["id"] = id
                };
                if (request.Params is not null) message["params"] = request.Params.DeepClone();
                await SendAsync(process.StandardInput, message);
            }

            return await Task.WhenAll(pending.Select(item => item.Task));
        }
        catch (TimeoutException error)
        {
            var stderr = ReadServerErrors();
            throw new TimeoutException(string.IsNullOrWhiteSpace(stderr) ? "Codex 数据请求超时。" : stderr, error);
        }
        finally
        {
            foreach (var item in pending) _responses.TryRemove(item.Id, out _);
        }
    }

    private int NextRequestId() => Interlocked.Increment(ref _nextRequestId);

    private async Task PumpErrorsAsync(StreamReader errors, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        while (true)
        {
            var count = await errors.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            lock (_serverErrors)
            {
                _serverErrors.Append(buffer, 0, count);
                if (_serverErrors.Length > 8192) _serverErrors.Remove(0, _serverErrors.Length - 8192);
            }
        }
    }

    private string ReadServerErrors()
    {
        lock (_serverErrors) return _serverErrors.ToString().Trim();
    }

    private void OnServerExited(object? sender, EventArgs e) =>
        FailPending(new InvalidOperationException("Codex App Server 已退出。"));

    private void FailPending(Exception error)
    {
        foreach (var response in _responses.ToArray())
        {
            if (_responses.TryRemove(response.Key, out var waiter)) waiter.TrySetException(error);
        }
    }

    private async Task ResetServerAsync()
    {
        var process = _serverProcess;
        var cancellation = _serverCancellation;
        var outputPump = _serverOutputPump;
        var errorPump = _serverErrorPump;
        _serverProcess = null;
        _serverCancellation = null;
        _serverOutputPump = null;
        _serverErrorPump = null;

        cancellation?.Cancel();
        if (process is not null)
        {
            try { process.StandardInput.Close(); } catch { }
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
        }
        if (outputPump is not null)
        {
            try { await outputPump; } catch (OperationCanceledException) { } catch (IOException) { }
        }
        if (errorPump is not null)
        {
            try { await errorPump; } catch (OperationCanceledException) { } catch (IOException) { }
        }
        process?.Dispose();
        cancellation?.Dispose();
        FailPending(new InvalidOperationException("Codex App Server 已断开。"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Window shutdown must never wait behind a 20-second account request. Tear
        // down the transport immediately; the active request observes cancellation
        // or the broken pipe and releases the gate on its own.
        var process = Interlocked.Exchange(ref _serverProcess, null);
        var cancellation = Interlocked.Exchange(ref _serverCancellation, null);
        _serverOutputPump = null;
        _serverErrorPump = null;
        cancellation?.Cancel();
        try { process?.StandardInput.Close(); } catch { }
        if (process is not null)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch { }
            try { process.Dispose(); } catch { }
        }
        cancellation?.Dispose();
        FailPending(new ObjectDisposedException(nameof(CodexUsageService)));
    }

    internal static SessionStats StatsIncludingRemote(SessionStats local, IEnumerable<RemoteSessionSnapshot> snapshots)
    {
        var days = local.DailyTokens.ToDictionary(item => item.Date, item => item.Tokens, StringComparer.Ordinal);
        var conversations = local.Conversations.Select(item => (Key: $"local:{item.TurnId}", Value: item))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var projects = local.ProjectDailyTokens.ToList();
        var contexts = local.ContextHealth.Select((item, index) => (Key: $"local:{item.ThreadId ?? item.ContextWindowId ?? index.ToString()}", Value: item))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);

        foreach (var snapshot in snapshots)
        {
            if (snapshot.Payload["dailyTokens"] is JsonObject remoteDays)
            {
                foreach (var pair in remoteDays)
                    if (days.ContainsKey(pair.Key) && Long(pair.Value) is long tokens) days[pair.Key] += Math.Max(0, tokens);
            }
            if (snapshot.Payload["conversations"] is JsonArray remoteConversations)
            {
                foreach (var item in remoteConversations.OfType<JsonObject>())
                {
                    var turnId = Text(item["turnId"]); var date = Text(item["date"]); var rawProject = Text(item["projectPath"]);
                    if (turnId is null || date is null || rawProject is null || !DateTimeOffset.TryParse(Text(item["startedAt"]), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startedAt)) continue;
                    var (projectKey, _) = RemoteProject(snapshot.Host, rawProject, Text(item["projectName"]));
                    conversations[$"{snapshot.Host.Id}:{turnId}"] = new ConversationStats(
                        turnId, Text(item["threadId"]), Text(item["contextWindowId"]), startedAt,
                        Text(item["preview"]) ?? "未命名对话", Long(item["tokens"]), date, projectKey,
                        Text(item["threadName"]), snapshot.Host.DisplayName);
                }
            }
            if (snapshot.Payload["projectDailyTokens"] is JsonArray remoteProjects)
            {
                foreach (var item in remoteProjects.OfType<JsonObject>())
                {
                    var date = Text(item["date"]); var rawProject = Text(item["projectPath"]); var tokens = Long(item["tokens"]);
                    if (date is null || rawProject is null || tokens is null) continue;
                    var (projectKey, projectName) = RemoteProject(snapshot.Host, rawProject, Text(item["projectName"]));
                    projects.Add(new ProjectDailyStats(date, projectKey, Math.Max(0, tokens.Value), projectName, snapshot.Host.DisplayName));
                }
            }
            if (snapshot.Payload["contextHealth"] is JsonArray remoteContexts)
            {
                var index = 0;
                foreach (var item in remoteContexts.OfType<JsonObject>())
                {
                    var rawProject = Text(item["projectPath"]); var used = Long(item["usedTokens"]); var maximum = Long(item["maxTokens"]);
                    if (rawProject is null || used is null || maximum is null || maximum <= 0 || !DateTimeOffset.TryParse(Text(item["updatedAt"]), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt)) { index++; continue; }
                    var (projectKey, projectName) = RemoteProject(snapshot.Host, rawProject, Text(item["projectName"]));
                    var context = new ContextHealthStats(
                        Text(item["threadId"]), Text(item["contextWindowId"]), projectKey, Text(item["preview"]) ?? "未命名任务",
                        Math.Max(0, used.Value), maximum.Value, updatedAt, (int)(Long(item["compactions"]) ?? 0),
                        Long(item["conversationTokens"]), Text(item["currentTurnId"]), Long(item["currentTurnTokens"]), Bool(item["currentTurnActive"]),
                        Text(item["threadName"]), snapshot.Host.DisplayName, projectName);
                    var key = $"{snapshot.Host.Id}:{context.ThreadId ?? context.ContextWindowId ?? index.ToString()}";
                    if (!contexts.TryGetValue(key, out var previous) || context.UpdatedAt > previous.UpdatedAt) contexts[key] = context;
                    index++;
                }
            }
        }

        var combinedProjects = projects.GroupBy(item => $"{item.Date}\0{item.ProjectPath}", StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProjectDailyStats(group.First().Date, group.First().ProjectPath, group.Sum(item => item.Tokens), group.Select(item => item.ProjectName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)), group.Select(item => item.SourceHost).FirstOrDefault(host => !string.IsNullOrWhiteSpace(host))))
            .ToArray();
        var orderedConversations = conversations.Values.OrderByDescending(item => item.StartedAt).ToArray();
        var today = local.DailyTokens.LastOrDefault()?.Date ?? string.Empty;
        return new SessionStats(
            orderedConversations.Count(item => item.Date == today),
            days.GetValueOrDefault(today),
            local.DailyTokens.Select(item => new DailyTokenStats(item.Date, days.GetValueOrDefault(item.Date), days.GetValueOrDefault(item.Date) > 0 ? "local" : "empty")).ToArray(),
            orderedConversations,
            combinedProjects,
            contexts.Values.OrderByDescending(item => item.UpdatedAt).ToArray());
    }

    private static (string Key, string Name) RemoteProject(RemoteCodexHost host, string path, string? suppliedName)
    {
        var nonProject = path == ProjectResolver.NonProjectKey;
        var key = $"{(nonProject ? "__remote_non_project__" : "__remote_project__")}:{host.Id}:{path}";
        var name = nonProject ? ProjectResolver.NonProjectName : !string.IsNullOrWhiteSpace(suppliedName) ? suppliedName! : Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(name)) name = "未识别项目";
        return (key, $"{name} · {host.DisplayName}");
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
    private static long? Long(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : null;
    private static bool? Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : null;

    private static JsonObject CreateLocalPayload(SessionStats stats) => UsagePayloadBuilder.CreateLocal(stats);

    private static TaskCompletionSource<JsonObject> Register(
        ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> responses,
        int id)
    {
        var response = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!responses.TryAdd(id, response)) throw new InvalidOperationException($"重复的 Codex 请求 ID：{id}");
        return response;
    }

    private static async Task SendAsync(StreamWriter input, JsonObject message)
    {
        await input.WriteLineAsync(message.ToJsonString());
        await input.FlushAsync();
    }

    private static async Task PumpOutputAsync(
        StreamReader output,
        ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> responses,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var lines = new JsonLineBuffer();
        while (true)
        {
            var count = await output.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;

            foreach (var message in lines.Append(new string(buffer, 0, count)))
            {
                if (!message.TryGetProperty("id", out var idNode) || !idNode.TryGetInt32(out var id)) continue;
                if (!responses.TryRemove(id, out var waiter)) continue;
                var response = JsonNode.Parse(message.GetRawText())?.AsObject();
                if (response is not null) waiter.TrySetResult(response);
            }
        }
    }

    private static void ThrowIfError(JsonObject response)
    {
        if (response["error"] is not JsonObject error) return;
        throw new InvalidOperationException(error["message"]?.GetValue<string>() ?? "Codex 初始化失败。");
    }

    private static JsonObject GetResult(JsonObject response, string name)
    {
        ThrowIfError(response);
        return response["result"] as JsonObject
            ?? throw new InvalidOperationException($"未收到{name}数据。");
    }

    private static IReadOnlyDictionary<string, string> GetThreadNames(JsonObject response)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (response["error"] is not null || response["result"]?["data"] is not JsonArray threads) return names;
        foreach (var thread in threads.OfType<JsonObject>())
        {
            var name = thread["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;
            foreach (var key in new[]
                     {
                         thread["id"]?.GetValue<string>(),
                         thread["sessionId"]?.GetValue<string>()
                     }.Where(key => !string.IsNullOrWhiteSpace(key)))
            {
                names.TryAdd(key!, name);
            }
        }
        return names;
    }
}

internal static class UsagePayloadBuilder
{
    private const int HistoryDays = 90;

    public static JsonObject CreateLocal(
        SessionStats stats,
        IReadOnlyDictionary<string, string>? threadNames = null)
    {
        var todayKey = stats.DailyTokens.LastOrDefault()?.Date ?? string.Empty;
        // Older callers construct SessionStats without the per-conversation date.
        var todayConversations = stats.Conversations.Where(item => string.IsNullOrEmpty(item.Date) || item.Date == todayKey).ToArray();
        var projectPaths = stats.ProjectDailyTokens.Select(item => item.ProjectPath)
            .Concat(stats.Conversations.Select(item => item.ProjectPath))
            .Concat(stats.ContextHealth.Select(item => item.ProjectPath));
        var projectNames = ProjectResolver.DisplayNames(projectPaths)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var project in stats.ProjectDailyTokens.Where(item => !string.IsNullOrWhiteSpace(item.ProjectName)))
            projectNames[project.ProjectPath] = project.ProjectName!;
        foreach (var context in stats.ContextHealth.Where(item => !string.IsNullOrWhiteSpace(item.ProjectName)))
            projectNames[context.ProjectPath] = context.ProjectName!;
        var tasks = BuildTasks(stats, projectNames, threadNames);
        return new JsonObject
        {
            ["today"] = new JsonObject
            {
                ["questions"] = stats.Questions,
                ["tokens"] = stats.Tokens,
                ["tokenSource"] = "local",
                ["conversations"] = new JsonArray(todayConversations.Select(conversation =>
                    (JsonNode)CreateConversationPayload(conversation, threadNames)).ToArray())
            },
            ["history"] = new JsonObject
            {
                ["source"] = "local",
                ["dailyTokens"] = new JsonArray(stats.DailyTokens.Select(day =>
                    (JsonNode)new JsonObject
                    {
                        ["date"] = day.Date,
                        ["tokens"] = day.Tokens,
                        ["source"] = day.Source
                    }).ToArray())
            },
            ["insights"] = new JsonObject
            {
                ["localOnly"] = !stats.Conversations.Any(item => !string.IsNullOrWhiteSpace(item.SourceHost)) &&
                    !stats.ProjectDailyTokens.Any(item => !string.IsNullOrWhiteSpace(item.SourceHost)),
                ["projects"] = new JsonArray(projectNames.OrderBy(pair => pair.Value, StringComparer.CurrentCulture).Select(pair =>
                {
                    var project = new JsonObject
                    {
                        ["key"] = pair.Key,
                        ["name"] = pair.Value,
                        ["projectKind"] = ProjectResolver.Kind(pair.Key)
                    };
                    if (pair.Key != ProjectResolver.NonProjectKey) project["path"] = pair.Key;
                    return (JsonNode)project;
                }).ToArray()),
                ["tasks"] = new JsonArray(tasks.Cast<JsonNode>().ToArray())
            },
            ["contextHealth"] = new JsonObject
            {
                ["source"] = "local",
                ["sessions"] = new JsonArray(stats.ContextHealth
                    .Where(item => item.MaxTokens > 0)
                    .OrderByDescending(item => item.UpdatedAt)
                    .Take(20)
                    .Select(item => (JsonNode)CreateContextPayload(item, projectNames, threadNames)).ToArray())
            }
        };
    }

    private static JsonObject[] BuildTasks(SessionStats stats, IReadOnlyDictionary<string, string> projectNames, IReadOnlyDictionary<string, string>? threadNames)
    {
        var rows = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var conversation in stats.Conversations)
        {
            var taskId = conversation.ThreadId ?? conversation.ContextWindowId ?? conversation.TurnId;
            var key = $"{conversation.Date}\0{conversation.ProjectPath}\0{taskId}";
            if (!rows.TryGetValue(key, out var row))
            {
                row = new JsonObject
                {
                    ["date"] = conversation.Date,
                    ["projectKey"] = conversation.ProjectPath,
                    ["projectName"] = projectNames.GetValueOrDefault(conversation.ProjectPath, "未识别项目"),
                    ["projectKind"] = ProjectResolver.Kind(conversation.ProjectPath),
                    ["taskId"] = taskId,
                    ["name"] = conversation.Preview,
                    ["turns"] = 0,
                    ["tokens"] = 0L,
                    ["lastActive"] = conversation.StartedAt.ToString("O", CultureInfo.InvariantCulture),
                    ["kind"] = "user"
                };
                rows[key] = row;
            }
            row["turns"] = ReadLong(row["turns"]) + 1;
            row["tokens"] = ReadLong(row["tokens"]) + Math.Max(0, conversation.Tokens ?? 0);
            if (!string.IsNullOrWhiteSpace(conversation.ThreadName)) row["name"] = conversation.ThreadName;
            else if (conversation.ThreadId is string threadId && threadNames?.TryGetValue(threadId, out var threadName) is true) row["name"] = threadName;
            if (!string.IsNullOrWhiteSpace(conversation.SourceHost)) row["sourceHost"] = conversation.SourceHost;
            var active = conversation.StartedAt.ToString("O", CultureInfo.InvariantCulture);
            if (string.CompareOrdinal(active, StringValue(row["lastActive"])) > 0) row["lastActive"] = active;
        }

        var attributed = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Values)
        {
            var key = $"{StringValue(row["date"])}\0{StringValue(row["projectKey"])}";
            attributed[key] = attributed.GetValueOrDefault(key) + ReadLong(row["tokens"]);
        }
        foreach (var project in stats.ProjectDailyTokens)
        {
            var key = $"{project.Date}\0{project.ProjectPath}";
            var gap = Math.Max(0, project.Tokens - attributed.GetValueOrDefault(key));
            if (gap == 0) continue;
            rows[$"{key}\0system"] = new JsonObject
            {
                ["date"] = project.Date,
                ["projectKey"] = project.ProjectPath,
                ["projectName"] = projectNames.GetValueOrDefault(project.ProjectPath, "未识别项目"),
                ["projectKind"] = ProjectResolver.Kind(project.ProjectPath),
                ["taskId"] = "system",
                ["name"] = "系统/子代理活动",
                ["turns"] = 0,
                ["tokens"] = gap,
                ["lastActive"] = "",
                ["kind"] = "system"
            };
            if (!string.IsNullOrWhiteSpace(project.SourceHost)) rows[$"{key}\0system"]!["sourceHost"] = project.SourceHost;
        }
        return rows.Values.OrderByDescending(row => StringValue(row["date"])).ThenByDescending(row => ReadLong(row["tokens"])).ToArray();
    }

    private static JsonObject CreateContextPayload(ContextHealthStats context, IReadOnlyDictionary<string, string> projectNames, IReadOnlyDictionary<string, string>? threadNames)
    {
        var usedPercent = Math.Clamp((double)context.UsedTokens / context.MaxTokens * 100, 0, 100);
        var taskId = context.ThreadId ?? context.ContextWindowId ?? $"context-{context.UpdatedAt.ToUnixTimeSeconds()}";
        var result = new JsonObject
        {
            ["taskId"] = taskId,
            ["threadId"] = context.ThreadId,
            ["contextWindowId"] = context.ContextWindowId,
            ["name"] = !string.IsNullOrWhiteSpace(context.ThreadName)
                ? context.ThreadName
                : context.ThreadId is string threadId && threadNames?.TryGetValue(threadId, out var name) is true ? name : context.Preview,
            ["projectKey"] = context.ProjectPath,
            ["projectName"] = projectNames.GetValueOrDefault(context.ProjectPath, "未识别项目"),
            ["projectKind"] = ProjectResolver.Kind(context.ProjectPath),
            ["usedTokens"] = context.UsedTokens,
            ["maxTokens"] = context.MaxTokens,
            ["usedPercent"] = usedPercent,
            ["remainingPercent"] = Math.Max(0, 100 - usedPercent),
            ["status"] = SessionStatsReader.ContextHealthStatus(usedPercent),
            ["lastActive"] = context.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
            ["compactions"] = context.Compactions
        };
        if (context.ConversationTokens is long conversationTokens) result["conversationTokens"] = Math.Max(0, conversationTokens);
        if (context.CurrentTurnId is not null) result["currentTurnId"] = context.CurrentTurnId;
        if (context.CurrentTurnTokens is long currentTurnTokens) result["currentTurnTokens"] = Math.Max(0, currentTurnTokens);
        if (context.CurrentTurnActive is bool currentTurnActive) result["currentTurnActive"] = currentTurnActive;
        if (!string.IsNullOrWhiteSpace(context.SourceHost)) result["sourceHost"] = context.SourceHost;
        return result;
    }

    private static long ReadLong(JsonNode? node) => node is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;
    private static string StringValue(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    private static JsonObject CreateConversationPayload(
        ConversationStats conversation,
        IReadOnlyDictionary<string, string>? threadNames)
    {
        var item = new JsonObject
        {
            ["turnId"] = conversation.TurnId,
            ["threadId"] = conversation.ThreadId,
            ["contextWindowId"] = conversation.ContextWindowId,
            ["startedAt"] = conversation.StartedAt.ToString("O", CultureInfo.InvariantCulture),
            ["preview"] = conversation.Preview,
            ["tokens"] = conversation.Tokens is long tokens ? JsonValue.Create(tokens) : null
        };
        if (!string.IsNullOrWhiteSpace(conversation.ThreadName))
        {
            item["threadName"] = conversation.ThreadName;
        }
        else if (conversation.ThreadId is string threadId &&
            threadNames?.TryGetValue(threadId, out var threadName) is true)
        {
            item["threadName"] = threadName;
        }
        if (!string.IsNullOrWhiteSpace(conversation.SourceHost)) item["sourceHost"] = conversation.SourceHost;
        return item;
    }

    public static JsonObject Build(
        SessionStats stats,
        JsonObject limits,
        JsonObject usage,
        DateTimeOffset now,
        IReadOnlyDictionary<string, string>? threadNames = null)
    {
        var payload = CreateLocal(stats, threadNames);

        foreach (var property in limits)
        {
            payload[property.Key] = property.Value?.DeepClone();
        }

        if (usage["dailyUsageBuckets"] is JsonArray buckets)
        {
            var bucketValues = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var item in buckets.OfType<JsonObject>())
            {
                var date = item["startDate"]?.GetValue<string>();
                if (date is null) continue;
                bucketValues[date] = item["tokens"] is JsonValue value && value.TryGetValue<long>(out var tokens)
                    ? tokens
                    : 0L;
            }
            var startDate = now.Date.AddDays(-(HistoryDays - 1));
            var dates = Enumerable.Range(0, HistoryDays)
                .Select(offset => startDate.AddDays(offset))
                .ToArray();
            var dateKeys = dates
                .Select(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .ToArray();
            if (dateKeys.Any(bucketValues.ContainsKey))
            {
                var localValues = stats.DailyTokens.ToDictionary(day => day.Date, day => day.Tokens);
                var localFallback = false;
                var accountDays = dateKeys
                    .Select(dateKey =>
                    {
                        if (bucketValues.TryGetValue(dateKey, out var accountTokens))
                        {
                            return new DailyTokenStats(dateKey, accountTokens, "account");
                        }
                        var localTokens = localValues.GetValueOrDefault(dateKey);
                        if (localTokens > 0) localFallback = true;
                        return new DailyTokenStats(dateKey, localTokens, localTokens > 0 ? "local" : "empty");
                    })
                    .ToArray();
                payload["history"] = new JsonObject
                {
                    ["source"] = "account",
                    ["localFallback"] = localFallback,
                    ["dailyTokens"] = new JsonArray(accountDays.Select(day =>
                        (JsonNode)new JsonObject
                        {
                            ["date"] = day.Date,
                            ["tokens"] = day.Tokens,
                            ["source"] = day.Source
                        }).ToArray())
                };
                var today = (JsonObject)payload["today"]!;
                today["tokens"] = accountDays[^1].Tokens;
                today["tokenSource"] = bucketValues.ContainsKey(dateKeys[^1]) ? "account" : "local";
            }
        }

        payload["source"] = "Codex App Server";
        return payload;
    }
}
