using CodexMeter.Windows.Services;
using System.Diagnostics;

namespace CodexMeter.Windows.Tests;

[TestClass]
public sealed class CodexUsageServiceTests
{
    [TestMethod]
    public async Task AppServer_IsReusedAcrossRefreshAndResetRequests()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-meter-server-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var previousBinary = Environment.GetEnvironmentVariable("CODEX_BINARY");
        var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            var script = Path.Combine(directory, "fake-server.ps1");
            var command = Path.Combine(directory, "fake-codex.cmd");
            var launches = Path.Combine(directory, "launches.txt");
            File.WriteAllText(script,
                """
                param([string]$Mode)
                Add-Content -LiteralPath $env:CODEX_METER_TEST_LAUNCHES -Value 'start'
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                  $message = $line | ConvertFrom-Json
                  if ($null -eq $message.id) { continue }
                  $result = switch ($message.method) {
                    'account/rateLimits/read' { @{ planType = 'plus'; primary = @{ remainingPercent = 75 }; secondary = @{ remainingPercent = 60 } } }
                    'account/usage/read' { @{ dailyUsageBuckets = @() } }
                    'thread/list' { @{ data = @() } }
                    'account/rateLimitResetCredit/consume' { @{ outcome = 'reset' } }
                    default { @{} }
                  }
                  @{ id = $message.id; result = $result } | ConvertTo-Json -Compress -Depth 8 | Write-Output
                }
                """);
            File.WriteAllText(command, "@powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0fake-server.ps1\" %*\r\n");
            Environment.SetEnvironmentVariable("CODEX_BINARY", command);
            Environment.SetEnvironmentVariable("CODEX_HOME", directory);
            Environment.SetEnvironmentVariable("CODEX_METER_TEST_LAUNCHES", launches);

            using var service = new CodexUsageService();
            var first = await service.FetchAsync(null, CancellationToken.None);
            var second = await service.FetchAsync(null, CancellationToken.None);
            var reset = await service.ConsumeResetCreditAsync(Guid.NewGuid().ToString("D"), CancellationToken.None);

            Assert.IsNull(first["error"]);
            Assert.IsNull(second["error"]);
            Assert.AreEqual("reset", reset["outcome"]?.GetValue<string>());
            Assert.AreEqual(1, File.ReadAllLines(launches).Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_BINARY", previousBinary);
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            Environment.SetEnvironmentVariable("CODEX_METER_TEST_LAUNCHES", null);
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task Dispose_DoesNotWaitForAnInFlightAccountRequest()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"codex-meter-server-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var previousBinary = Environment.GetEnvironmentVariable("CODEX_BINARY");
        var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            var script = Path.Combine(directory, "hanging-server.ps1");
            var command = Path.Combine(directory, "hanging-codex.cmd");
            var initialized = Path.Combine(directory, "initialized.txt");
            File.WriteAllText(script,
                """
                param([string]$Mode)
                while ($null -ne ($line = [Console]::In.ReadLine())) {
                  $message = $line | ConvertFrom-Json
                  if ($message.method -eq 'initialize') {
                    @{ id = $message.id; result = @{} } | ConvertTo-Json -Compress | Write-Output
                    Set-Content -LiteralPath $env:CODEX_METER_TEST_INITIALIZED -Value 'ready'
                  }
                }
                """);
            File.WriteAllText(command, "@powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"%~dp0hanging-server.ps1\" %*\r\n");
            Environment.SetEnvironmentVariable("CODEX_BINARY", command);
            Environment.SetEnvironmentVariable("CODEX_HOME", directory);
            Environment.SetEnvironmentVariable("CODEX_METER_TEST_INITIALIZED", initialized);

            var service = new CodexUsageService();
            var fetch = service.FetchAsync(null, CancellationToken.None);
            await WaitForFileAsync(initialized, TimeSpan.FromSeconds(10));

            var stopwatch = Stopwatch.StartNew();
            service.Dispose();
            stopwatch.Stop();

            Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Dispose took {stopwatch.Elapsed}.");
            await fetch.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_BINARY", previousBinary);
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            Environment.SetEnvironmentVariable("CODEX_METER_TEST_INITIALIZED", null);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(path))
        {
            if (DateTime.UtcNow >= deadline) Assert.Fail($"Timed out waiting for {path}.");
            await Task.Delay(25);
        }
    }
}
