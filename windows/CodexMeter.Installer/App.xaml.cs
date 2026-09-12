using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace CodexMeter.Installer;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--quiet-install", StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = RunQuietly(uninstall: false);
            Shutdown(Environment.ExitCode);
            return;
        }

        if (e.Args.Contains("--quiet-uninstall", StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = RunQuietly(uninstall: true);
            Shutdown(Environment.ExitCode);
            return;
        }

        var previewIndex = Array.FindIndex(e.Args, argument =>
            string.Equals(argument, "--render-preview", StringComparison.OrdinalIgnoreCase));
        if (previewIndex >= 0 && previewIndex + 1 < e.Args.Length)
        {
            ApplySystemTheme();
            var previewUninstall = e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase);
            var previewWindow = new MainWindow(previewUninstall);
            MainWindow = previewWindow;
            var captured = false;
            previewWindow.ContentRendered += async (_, _) =>
            {
                if (captured)
                {
                    return;
                }

                captured = true;
                await previewWindow.Dispatcher.InvokeAsync(
                    () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                previewWindow.SavePreview(e.Args[previewIndex + 1]);
                previewWindow.Close();
            };
            previewWindow.Show();
            return;
        }

        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            Environment.ExitCode = InstallerService.SelfTest();
            Shutdown(Environment.ExitCode);
            return;
        }

        _mutex = new Mutex(true, @"Local\CodexMeter.Installer.v1", out var ownsMutex);
        if (!ownsMutex)
        {
            _mutex.Dispose();
            _mutex = null;
            MessageBox.Show("Codex Meter 安装程序已经在运行。", "Codex Meter", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        ApplySystemTheme();
        var uninstall = e.Args.Contains("--uninstall", StringComparer.OrdinalIgnoreCase);
        MainWindow = new MainWindow(uninstall);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private void ApplySystemTheme()
    {
        var isLight = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            isLight = !Equals(key?.GetValue("AppsUseLightTheme"), 0);
        }
        catch
        {
            // Keep the light palette when the preference cannot be read.
        }

        if (isLight)
        {
            return;
        }

        Resources["WindowBackground"] = new SolidColorBrush(Color.FromRgb(14, 18, 27));
        Resources["CardBackground"] = new SolidColorBrush(Color.FromArgb(222, 25, 30, 43));
        Resources["CardBorder"] = new SolidColorBrush(Color.FromArgb(44, 151, 164, 190));
        Resources["PrimaryText"] = new SolidColorBrush(Color.FromRgb(243, 246, 252));
        Resources["SecondaryText"] = new SolidColorBrush(Color.FromRgb(173, 184, 204));
        Resources["MutedText"] = new SolidColorBrush(Color.FromRgb(130, 143, 166));
        Resources["GhostButton"] = new SolidColorBrush(Color.FromArgb(24, 159, 173, 199));
    }

    private static int RunQuietly(bool uninstall)
    {
        try
        {
            var progress = new Progress<InstallProgress>(_ => { });
            if (uninstall)
            {
                var preserveSettings = !string.Equals(
                    Environment.GetEnvironmentVariable("CODEX_METER_PRESERVE_SETTINGS"),
                    "0",
                    StringComparison.Ordinal);
                InstallerService.Uninstall(InstallerService.GetInstalledDirectory(), preserveSettings, progress);
            }
            else
            {
                var installDirectory = Environment.GetEnvironmentVariable("CODEX_METER_INSTALL_DIRECTORY")
                    ?? InstallerService.DefaultInstallDirectory;
                var createDesktopShortcut = !string.Equals(
                    Environment.GetEnvironmentVariable("CODEX_METER_CREATE_DESKTOP_SHORTCUT"),
                    "0",
                    StringComparison.Ordinal);
                InstallerService.Install(installDirectory, createDesktopShortcut, progress);
            }
            return 0;
        }
        catch (Exception exception)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "CodexMeterSetup-error.log"),
                    exception.ToString());
            }
            catch
            {
                // The exit code still reports the failure when diagnostics cannot be written.
            }
            return 1;
        }
    }
}
