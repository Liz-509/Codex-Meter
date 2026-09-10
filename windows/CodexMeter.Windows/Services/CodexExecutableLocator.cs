using System.Diagnostics;

namespace CodexMeter.Windows.Services;

internal sealed record CodexCommand(string ExecutablePath)
{
    public ProcessStartInfo CreateStartInfo()
    {
        var extension = Path.GetExtension(ExecutablePath);
        ProcessStartInfo startInfo;
        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            if (ExecutablePath.Contains('"')) throw new InvalidOperationException("Codex 路径包含不支持的引号。");
            startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/d /s /c \"\"{ExecutablePath}\" app-server\""
            };
        }
        else
        {
            startInfo = new ProcessStartInfo { FileName = ExecutablePath };
            startInfo.ArgumentList.Add("app-server");
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return startInfo;
    }
}

internal sealed class CodexExecutableLocator
{
    private readonly Func<string, bool> _fileExists;

    public CodexExecutableLocator(Func<string, bool>? fileExists = null)
    {
        _fileExists = fileExists ?? File.Exists;
    }

    public CodexCommand? FindFromEnvironment()
    {
        return Find(
            Environment.GetEnvironmentVariable("CODEX_BINARY"),
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
    }

    internal CodexCommand? Find(
        string? overridePath,
        string? pathValue,
        string? appData,
        string? localAppData,
        string? programFiles)
    {
        var candidates = new List<string>();
        AddCandidate(candidates, overridePath);

        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                AddCandidate(candidates, Path.Combine(directory.Trim().Trim('"'), "codex.exe"));
                AddCandidate(candidates, Path.Combine(directory.Trim().Trim('"'), "codex.cmd"));
                AddCandidate(candidates, Path.Combine(directory.Trim().Trim('"'), "codex.bat"));
            }
        }

        AddCandidate(candidates, Combine(appData, "npm", "codex.cmd"));
        AddCandidate(candidates, Combine(localAppData, "Microsoft", "WindowsApps", "codex.exe"));
        AddCandidate(candidates, Combine(localAppData, "Programs", "ChatGPT", "resources", "codex.exe"));
        AddCandidate(candidates, Combine(programFiles, "ChatGPT", "resources", "codex.exe"));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var normalized = candidate.Trim().Trim('"');
            if (!seen.Add(normalized)) continue;
            if (_fileExists(normalized)) return new CodexCommand(normalized);
        }
        return null;
    }

    private static void AddCandidate(ICollection<string> candidates, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) candidates.Add(path);
    }

    private static string? Combine(string? root, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(root)) return null;
        return segments.Aggregate(root, Path.Combine);
    }
}
