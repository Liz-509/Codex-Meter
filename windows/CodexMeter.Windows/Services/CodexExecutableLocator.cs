using System.Diagnostics;
using System.IO;

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
    private readonly Func<string, IEnumerable<string>> _enumerateExecutables;

    public CodexExecutableLocator(
        Func<string, bool>? fileExists = null,
        Func<string, IEnumerable<string>>? enumerateExecutables = null)
    {
        _fileExists = fileExists ?? File.Exists;
        _enumerateExecutables = enumerateExecutables ?? EnumerateExecutables;
    }

    public CodexCommand? FindFromEnvironment()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        return Find(
            Environment.GetEnvironmentVariable("CODEX_BINARY"),
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetEnvironmentVariable("CODEX_CLI_PATH"),
            Environment.GetEnvironmentVariable("CODEX_INSTALL_DIR"),
            codexHome);
    }

    internal CodexCommand? Find(
        string? overridePath,
        string? pathValue,
        string? appData,
        string? localAppData,
        string? programFiles,
        string? cliPath = null,
        string? installDirectory = null,
        string? codexHome = null)
    {
        var candidates = new List<string>();
        AddCandidate(candidates, overridePath);
        AddCandidate(candidates, cliPath);
        AddCandidate(candidates, Combine(installDirectory, "codex.exe"));
        AddCandidate(candidates, Combine(installDirectory, "bin", "codex.exe"));

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
        AddCandidate(candidates, Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe"));
        AddCandidate(candidates, Combine(codexHome, "packages", "standalone", "current", "bin", "codex.exe"));

        var desktopBin = Combine(localAppData, "OpenAI", "Codex", "bin");
        AddCandidate(candidates, Combine(desktopBin, "codex.exe"));
        AddExecutableCandidates(candidates, desktopBin);

        var packagedDesktopBin = Combine(
            localAppData,
            "Packages",
            "OpenAI.Codex_2p2nqsd0c76g0",
            "LocalCache",
            "Local",
            "OpenAI",
            "Codex",
            "bin");
        AddCandidate(candidates, Combine(packagedDesktopBin, "codex.exe"));
        AddExecutableCandidates(candidates, packagedDesktopBin);

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

    private void AddExecutableCandidates(ICollection<string> candidates, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return;

        try
        {
            foreach (var path in _enumerateExecutables(directory))
            {
                AddCandidate(candidates, path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static IEnumerable<string> EnumerateExecutables(string directory)
    {
        if (!Directory.Exists(directory)) return [];

        try
        {
            return Directory
                .EnumerateFiles(directory, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
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
