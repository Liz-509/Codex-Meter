using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Net;
using System.Text;
using Microsoft.Win32;

namespace CodexMeter.Installer;

internal sealed class InstallProgress
{
    public InstallProgress(int percent, string message)
    {
        Percent = percent;
        Message = message;
    }

    public int Percent { get; }
    public string Message { get; }
}

internal static class InstallerService
{
    private const string PayloadResource = "CodexMeter.Payload.zip";
    private const string AppFileName = "Codex Meter.exe";
    private const string SetupFileName = "Codex Meter Uninstaller.exe";
    private const string DotNetDesktopRuntimeUrl = "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe";
    private const string WindowsAppRuntimeUrl = "https://aka.ms/windowsappsdk/2.4/2.4.0/windowsappruntimeinstall-x64.exe";
    private const string UninstallRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexMeter";
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupRegistryValue = "Codex Meter";

    public static string DefaultInstallDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs",
        "Codex Meter");

    private static string StartMenuDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        "Codex Meter");
    private static string ShortcutPath => Path.Combine(StartMenuDirectory, "Codex Meter.lnk");
    private static string DesktopShortcutPath => Path.Combine(GetDesktopDirectory(), "Codex Meter.lnk");
    private static string AppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Codex Meter");

    public static void Install(
        string requestedInstallDirectory,
        bool createDesktopShortcut,
        IProgress<InstallProgress> progress)
    {
        var installDirectory = ValidateInstallDirectory(requestedInstallDirectory);
        var appPath = Path.Combine(installDirectory, AppFileName);
        var parent = Directory.GetParent(installDirectory)?.FullName
            ?? throw new InvalidOperationException("无法确定安装目录。");
        var staging = Path.Combine(parent, $".codex-meter-install-{Guid.NewGuid():N}");
        var backup = Path.Combine(parent, $".codex-meter-backup-{Guid.NewGuid():N}");
        var movedExistingInstall = false;
        var installedNewPayload = false;

        try
        {
            progress.Report(new InstallProgress(8, "正在准备安装文件…"));
            Directory.CreateDirectory(parent);
            EnsureDotNetDesktopRuntime(progress);
            EnsureWindowsAppRuntime(progress);
            ExtractPayload(staging, progress, 24, 68);

            if (!File.Exists(Path.Combine(staging, AppFileName)))
            {
                throw new InvalidDataException("安装包缺少 Codex Meter 主程序。");
            }

            progress.Report(new InstallProgress(72, "正在关闭已运行的 Codex Meter…"));
            StopInstalledApp(appPath);

            var setupSource = GetCurrentExecutablePath();
            File.Copy(setupSource, Path.Combine(staging, SetupFileName), overwrite: true);

            if (Directory.Exists(installDirectory))
            {
                var existingEntries = Directory.EnumerateFileSystemEntries(installDirectory).Any();
                if (existingEntries && !File.Exists(appPath))
                {
                    throw new InvalidOperationException("所选位置中的 Codex Meter 文件夹不为空。请选择其他位置，避免覆盖已有文件。");
                }

                Directory.Move(installDirectory, backup);
                movedExistingInstall = true;
            }

            Directory.Move(staging, installDirectory);
            installedNewPayload = true;

            progress.Report(new InstallProgress(84, "正在创建开始菜单快捷方式…"));
            CreateStartMenuShortcut(installDirectory);
            UpdateDesktopShortcut(installDirectory, createDesktopShortcut);
            RegisterUninstaller(installDirectory);

            if (Directory.Exists(backup))
            {
                Directory.Delete(backup, recursive: true);
            }

            progress.Report(new InstallProgress(100, "安装完成"));
        }
        catch
        {
            TryDeleteDirectory(staging);
            if (installedNewPayload)
            {
                TryDeleteDirectory(installDirectory);
            }

            if (movedExistingInstall && Directory.Exists(backup) && !Directory.Exists(installDirectory))
            {
                Directory.Move(backup, installDirectory);
            }

            throw;
        }
    }

    public static void Uninstall(
        string requestedInstallDirectory,
        bool preserveSettings,
        IProgress<InstallProgress> progress)
    {
        var installDirectory = ValidateInstallDirectory(requestedInstallDirectory);
        var appPath = Path.Combine(installDirectory, AppFileName);
        progress.Report(new InstallProgress(12, "正在关闭 Codex Meter…"));
        StopInstalledApp(appPath);

        progress.Report(new InstallProgress(42, "正在移除开始菜单快捷方式…"));
        if (File.Exists(ShortcutPath))
        {
            File.Delete(ShortcutPath);
        }
        if (File.Exists(DesktopShortcutPath))
        {
            File.Delete(DesktopShortcutPath);
        }
        if (Directory.Exists(StartMenuDirectory) && !Directory.EnumerateFileSystemEntries(StartMenuDirectory).Any())
        {
            Directory.Delete(StartMenuDirectory);
        }

        progress.Report(new InstallProgress(70, "正在清理卸载信息…"));
        Registry.CurrentUser.DeleteSubKeyTree(UninstallRegistryPath, throwOnMissingSubKey: false);
        using (var startupKey = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: true))
        {
            startupKey?.DeleteValue(StartupRegistryValue, throwOnMissingValue: false);
        }
        if (!preserveSettings)
        {
            progress.Report(new InstallProgress(82, "正在移除 Codex Meter 数据与设置…"));
            ScheduleDirectoryCleanup(AppDataDirectory);
        }
        ScheduleInstallDirectoryCleanup(installDirectory);
        progress.Report(new InstallProgress(100, "卸载完成"));
    }

    public static void LaunchInstalledApp(string requestedInstallDirectory)
    {
        var installDirectory = ValidateInstallDirectory(requestedInstallDirectory);
        var appPath = Path.Combine(installDirectory, AppFileName);
        if (!File.Exists(appPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = appPath,
            WorkingDirectory = installDirectory,
            UseShellExecute = true,
        });
    }

    public static int SelfTest()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"codex-meter-setup-test-{Guid.NewGuid():N}");
        try
        {
            ExtractPayload(testDirectory, progress: null, 0, 100);
            var app = Path.Combine(testDirectory, AppFileName);
            var companion = Path.Combine(testDirectory, "Resources", "companion.html");
            var widget = Path.Combine(testDirectory, "Resources", "usage-widget.js");
            if (!File.Exists(app) || !File.Exists(companion) || !File.Exists(widget))
            {
                return 2;
            }
            return 0;
        }
        catch
        {
            return 1;
        }
        finally
        {
            TryDeleteDirectory(testDirectory);
        }
    }

    private static void EnsureDotNetDesktopRuntime(IProgress<InstallProgress>? progress)
    {
        var runtimeRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet",
            "shared",
            "Microsoft.WindowsDesktop.App");
        if (Directory.Exists(runtimeRoot) &&
            Directory.EnumerateDirectories(runtimeRoot, "8.*", SearchOption.TopDirectoryOnly).Any())
        {
            return;
        }

        progress?.Report(new InstallProgress(10, "正在下载 .NET 8 桌面运行时…"));
        DownloadAndRunInstaller(
            DotNetDesktopRuntimeUrl,
            "dotnet-desktop-runtime",
            "/install /quiet /norestart",
            ".NET 8 桌面运行时",
            requiresElevation: true);
    }

    private static void EnsureWindowsAppRuntime(IProgress<InstallProgress>? progress)
    {
        if (IsWindowsAppRuntimeInstalled())
        {
            return;
        }

        progress?.Report(new InstallProgress(16, "正在下载 Windows 通知组件…"));
        DownloadAndRunInstaller(
            WindowsAppRuntimeUrl,
            "windows-app-runtime",
            "--quiet",
            "Windows App Runtime",
            requiresElevation: false);
    }

    private static bool IsWindowsAppRuntimeInstalled()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe"),
                Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"if (Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.2.4*' -ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null || !process.WaitForExit(15_000))
            {
                TryKill(process);
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void DownloadAndRunInstaller(
        string url,
        string temporaryName,
        string arguments,
        string displayName,
        bool requiresElevation)
    {
        var path = Path.Combine(Path.GetTempPath(), $"codex-meter-{temporaryName}-{Guid.NewGuid():N}.exe");
        try
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            using (var client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = "Codex-Meter-Setup";
                if (client.Proxy is not null)
                {
                    client.Proxy.Credentials = CredentialCache.DefaultCredentials;
                }
                client.DownloadFile(url, path);
            }
            if (!File.Exists(path) || new FileInfo(path).Length < 1024 * 1024)
            {
                throw new InvalidDataException($"{displayName} 下载内容无效。");
            }
            ValidateMicrosoftSignature(path, displayName);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                Arguments = arguments,
                UseShellExecute = requiresElevation,
                Verb = requiresElevation ? "runas" : string.Empty,
                CreateNoWindow = !requiresElevation,
                WindowStyle = requiresElevation ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
            }) ?? throw new InvalidOperationException($"无法启动 {displayName} 安装程序。");
            if (!process.WaitForExit(300_000))
            {
                TryKill(process);
                throw new TimeoutException($"{displayName} 安装超时。");
            }
            if (process.ExitCode != 0 && process.ExitCode != 1641 && process.ExitCode != 3010)
            {
                throw new InvalidOperationException($"{displayName} 安装失败，退出代码 {process.ExitCode}。");
            }
        }
        catch (WebException exception)
        {
            throw new InvalidOperationException($"无法下载 {displayName}，请检查网络连接后重试。", exception);
        }
        finally
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static void ValidateMicrosoftSignature(string path, string displayName)
    {
        var escapedPath = path.Replace("'", "''");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            Arguments = "-NoLogo -NoProfile -NonInteractive -Command \"" +
                "$signature = Get-AuthenticodeSignature -LiteralPath '" + escapedPath + "'; " +
                "Write-Output ($signature.Status.ToString() + '|' + $signature.SignerCertificate.Subject); " +
                "if ($signature.Status -eq 'Valid' -and " +
                "$signature.SignerCertificate.Subject -like '*Microsoft Corporation*') { exit 0 } else { exit 1 }\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        if (process is null || !process.WaitForExit(30_000))
        {
            TryKill(process);
            throw new InvalidDataException($"无法验证 {displayName} 的数字签名。");
        }
        if (process.ExitCode != 0)
        {
            var details = process.StandardOutput.ReadToEnd().Trim();
            if (string.IsNullOrWhiteSpace(details)) details = process.StandardError.ReadToEnd().Trim();
            throw new InvalidDataException($"{displayName} 未通过 Microsoft 数字签名验证，已停止安装。{(string.IsNullOrWhiteSpace(details) ? string.Empty : $"（{details}）")}");
        }
    }

    public static string GetInstalledDirectory()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallRegistryPath);
            if (key?.GetValue("InstallLocation") is string registeredDirectory &&
                !string.IsNullOrWhiteSpace(registeredDirectory))
            {
                return ValidateInstallDirectory(registeredDirectory);
            }
        }
        catch
        {
            // Fall back to the uninstaller's directory or the default location.
        }

        var currentExecutable = GetCurrentExecutablePath();
        var processDirectory = Path.GetDirectoryName(currentExecutable);
        if (string.Equals(Path.GetFileName(currentExecutable), SetupFileName, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(processDirectory))
        {
            return ValidateInstallDirectory(processDirectory);
        }

        return DefaultInstallDirectory;
    }

    private static void ExtractPayload(
        string destination,
        IProgress<InstallProgress>? progress,
        int startPercent,
        int endPercent)
    {
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource)
            ?? throw new InvalidDataException("安装包中的应用文件不可用，请重新下载安装包。");
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        var destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destinationRoot);

        for (var index = 0; index < archive.Entries.Count; index++)
        {
            var entry = archive.Entries[index];
            var target = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
            if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("安装包包含不安全的文件路径。");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            var percent = startPercent + (int)((endPercent - startPercent) * ((index + 1d) / archive.Entries.Count));
            progress?.Report(new InstallProgress(percent, $"正在写入 {entry.Name}…"));
        }
    }

    private static void StopInstalledApp(string appPath)
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(AppFileName)))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (!string.Equals(path, appPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.CloseMainWindow();
                if (!process.WaitForExit(2_000))
                {
                    TryKill(process);
                    process.WaitForExit(3_000);
                }
            }
            catch
            {
                // A locked installation directory will produce a clear error during replacement.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void CreateStartMenuShortcut(string installDirectory)
    {
        Directory.CreateDirectory(StartMenuDirectory);
        CreateShortcut(ShortcutPath, installDirectory);
    }

    private static void UpdateDesktopShortcut(string installDirectory, bool createDesktopShortcut)
    {
        if (!createDesktopShortcut)
        {
            if (File.Exists(DesktopShortcutPath))
            {
                File.Delete(DesktopShortcutPath);
            }
            return;
        }

        CreateShortcut(DesktopShortcutPath, installDirectory);
    }

    private static string GetDesktopDirectory()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktop))
        {
            return desktop;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
            if (key?.GetValue("Desktop") is string configuredDesktop &&
                !string.IsNullOrWhiteSpace(configuredDesktop))
            {
                return Environment.ExpandEnvironmentVariables(configuredDesktop);
            }
        }
        catch
        {
            // Fall through to the conventional profile-relative location.
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Desktop");
    }

    private static void CreateShortcut(string shortcutPath, string installDirectory)
    {
        var appPath = Path.Combine(installDirectory, AppFileName);
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("无法创建开始菜单快捷方式。");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath]);
            var shortcutType = shortcut!.GetType();
            shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [appPath]);
            shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [installDirectory]);
            shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, ["查看 Codex 用量与额度"]);
            shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [$"{appPath},0"]);
            shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }
            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static void RegisterUninstaller(string installDirectory)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallRegistryPath, writable: true)
            ?? throw new InvalidOperationException("无法注册卸载程序。");
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        var appPath = Path.Combine(installDirectory, AppFileName);
        var uninstallPath = Path.Combine(installDirectory, SetupFileName);
        var estimatedSize = Directory.EnumerateFiles(installDirectory, "*", SearchOption.AllDirectories)
            .Sum(file => new FileInfo(file).Length) / 1024;

        key.SetValue("DisplayName", "Codex Meter");
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Codex Meter contributors");
        key.SetValue("DisplayIcon", appPath);
        key.SetValue("InstallLocation", installDirectory);
        key.SetValue("UninstallString", $"\"{uninstallPath}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)Math.Min(estimatedSize, int.MaxValue), RegistryValueKind.DWord);
        key.SetValue("URLInfoAbout", "https://github.com/Liz-509/Codex-Meter");
    }

    private static void ScheduleInstallDirectoryCleanup(string installDirectory)
        => ScheduleDirectoryCleanup(installDirectory);

    private static void ScheduleDirectoryCleanup(string directory)
    {
        var cleanupScript = Path.Combine(Path.GetTempPath(), $"codex-meter-cleanup-{Guid.NewGuid():N}.cmd");
        var script = new StringBuilder()
            .AppendLine("@echo off")
            .AppendLine("for /L %%i in (1,1,600) do (")
            .AppendLine("  rmdir /s /q \"%CODEX_METER_UNINSTALL_DIR%\" 2>nul")
            .AppendLine("  if not exist \"%CODEX_METER_UNINSTALL_DIR%\" goto done")
            .AppendLine("  timeout /t 1 /nobreak >nul")
            .AppendLine(")")
            .AppendLine(":done")
            .AppendLine("del /q \"%~f0\"")
            .ToString();
        File.WriteAllText(cleanupScript, script, Encoding.ASCII);

        var startInfo = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /c \"\"{cleanupScript}\"\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.EnvironmentVariables["CODEX_METER_UNINSTALL_DIR"] = directory;
        Process.Start(startInfo);
    }

    private static string ValidateInstallDirectory(string requestedInstallDirectory)
    {
        if (string.IsNullOrWhiteSpace(requestedInstallDirectory))
        {
            throw new InvalidOperationException("请选择有效的安装位置。");
        }

        var fullPath = Path.GetFullPath(requestedInstallDirectory.Trim());
        if (!Path.IsPathRooted(fullPath) ||
            !string.Equals(
                Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar)),
                "Codex Meter",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("安装位置必须使用名为 Codex Meter 的专用文件夹。");
        }

        if (Directory.GetParent(fullPath) is null)
        {
            throw new InvalidOperationException("不能将应用直接安装到磁盘根目录。");
        }

        return fullPath;
    }

    private static string GetCurrentExecutablePath()
    {
        using var process = Process.GetCurrentProcess();
        return process.MainModule?.FileName
            ?? Assembly.GetExecutingAssembly().Location
            ?? throw new InvalidOperationException("无法定位当前安装程序。");
    }

    private static void TryKill(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try { process.Kill(); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup for staging and self-test folders.
        }
    }
}
