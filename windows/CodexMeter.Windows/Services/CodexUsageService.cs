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

    public CodexUsageService(CodexExecutableLocator? locator = null)
    {
        _locator = locator ?? new CodexExecutableLocator();
    }

    public async Task<JsonObject> FetchAsync(
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

        var partial = CreateLocalPayload(stats);
        partial["partial"] = true;
        partial["syncMessage"] = "正在同步额度";
        if (partialCallback is not null) await partialCallback(partial);

        try
        {
            var (limits, usage, threadNames) = await ReadAccountDataAsync(cancellationToken);
            return UsagePayloadBuilder.Build(stats, limits, usage, DateTimeOffset.Now, threadNames);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var payload = CreateLocalPayload(stats);
            payload["error"] = error.Message;
            return payload;
        }
    }

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

    public async Task<JsonObject?> FetchCurrentContextHealthAsync(CancellationToken cancellationToken)
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
            return current is null ? null : _liveContextReader.CreatePayload(current);
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
                    ["version"] = "1.4.1"
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
        var projectNames = ProjectResolver.DisplayNames(projectPaths);
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
                ["localOnly"] = true,
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
            if (conversation.ThreadId is string threadId && threadNames?.TryGetValue(threadId, out var threadName) is true) row["name"] = threadName;
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
        }
        return rows.Values.OrderByDescending(row => StringValue(row["date"])).ThenByDescending(row => ReadLong(row["tokens"])).ToArray();
    }

    private static JsonObject CreateContextPayload(ContextHealthStats context, IReadOnlyDictionary<string, string> projectNames, IReadOnlyDictionary<string, string>? threadNames)
    {
        var usedPercent = Math.Clamp((double)context.UsedTokens / context.MaxTokens * 100, 0, 100);
        var taskId = context.ThreadId ?? context.ContextWindowId ?? $"context-{context.UpdatedAt.ToUnixTimeSeconds()}";
        return new JsonObject
        {
            ["taskId"] = taskId,
            ["threadId"] = context.ThreadId,
            ["contextWindowId"] = context.ContextWindowId,
            ["name"] = context.ThreadId is string threadId && threadNames?.TryGetValue(threadId, out var name) is true ? name : context.Preview,
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
        if (conversation.ThreadId is string threadId &&
            threadNames?.TryGetValue(threadId, out var threadName) is true)
        {
            item["threadName"] = threadName;
        }
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
