using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexMeter.Windows.Services;

internal sealed class CodexUsageService
{
    private readonly CodexExecutableLocator _locator;

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
            () => SessionStatsReader.ReadToday(
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
            var (limits, usage) = await ReadAccountDataAsync(cancellationToken);
            return UsagePayloadBuilder.Build(stats, limits, usage, DateTimeOffset.Now);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            var payload = CreateLocalPayload(stats);
            payload["error"] = error.Message;
            return payload;
        }
    }

    private async Task<(JsonObject Limits, JsonObject Usage)> ReadAccountDataAsync(CancellationToken cancellationToken)
    {
        var command = _locator.FindFromEnvironment()
            ?? throw new InvalidOperationException("未找到 Codex。请安装 Windows 版 ChatGPT/Codex，或设置 CODEX_BINARY。");

        using var process = new Process { StartInfo = command.CreateStartInfo(), EnableRaisingEvents = true };
        if (!process.Start()) throw new InvalidOperationException("Codex App Server 启动失败。");

        using var pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var responses = new ConcurrentDictionary<int, TaskCompletionSource<JsonObject>>();
        var outputPump = PumpOutputAsync(process.StandardOutput, responses, pumpCancellation.Token);
        var errorOutput = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            var initialize = Register(responses, 0);
            await SendAsync(process.StandardInput, new JsonObject
            {
                ["method"] = "initialize",
                ["id"] = 0,
                ["params"] = new JsonObject
                {
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "codex_usage_widget",
                        ["title"] = "Codex Meter",
                        ["version"] = "1.1.0"
                    }
                }
            });
            ThrowIfError(await initialize.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken));

            await SendAsync(process.StandardInput, new JsonObject
            {
                ["method"] = "initialized",
                ["params"] = new JsonObject()
            });

            var limitsResponse = Register(responses, 1);
            var usageResponse = Register(responses, 2);
            await SendAsync(process.StandardInput, new JsonObject
            {
                ["method"] = "account/rateLimits/read",
                ["id"] = 1
            });
            await SendAsync(process.StandardInput, new JsonObject
            {
                ["method"] = "account/usage/read",
                ["id"] = 2
            });

            var completed = await Task.WhenAll(
                limitsResponse.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken),
                usageResponse.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken));
            return (
                GetResult(completed[0], "额度"),
                GetResult(completed[1], "Token"));
        }
        catch (TimeoutException)
        {
            var stderr = errorOutput.IsCompletedSuccessfully ? errorOutput.Result : string.Empty;
            throw new TimeoutException(string.IsNullOrWhiteSpace(stderr) ? "Codex 数据请求超时。" : stderr.Trim());
        }
        finally
        {
            pumpCancellation.Cancel();
            try { process.StandardInput.Close(); } catch { }
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
            }
            try { await outputPump; } catch (OperationCanceledException) { } catch (IOException) { }
        }
    }

    private static JsonObject CreateLocalPayload(SessionStats stats) => new()
    {
        ["today"] = new JsonObject
        {
            ["questions"] = stats.Questions,
            ["tokens"] = stats.Tokens
        }
    };

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
}

internal static class UsagePayloadBuilder
{
    public static JsonObject Build(
        SessionStats stats,
        JsonObject limits,
        JsonObject usage,
        DateTimeOffset now)
    {
        var payload = new JsonObject
        {
            ["today"] = new JsonObject
            {
                ["questions"] = stats.Questions,
                ["tokens"] = stats.Tokens
            }
        };

        foreach (var property in limits)
        {
            payload[property.Key] = property.Value?.DeepClone();
        }

        if (stats.Tokens == 0 &&
            usage["dailyUsageBuckets"] is JsonArray buckets)
        {
            var date = now.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var bucket = buckets
                .OfType<JsonObject>()
                .FirstOrDefault(item => item["startDate"]?.GetValue<string>() == date);
            if (bucket?["tokens"] is JsonValue fallbackTokens && fallbackTokens.TryGetValue<long>(out var tokens))
            {
                ((JsonObject)payload["today"]!)["tokens"] = tokens;
            }
        }

        payload["source"] = "Codex App Server";
        return payload;
    }
}
