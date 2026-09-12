using Microsoft.Win32;

namespace CodexMeter.Windows.Services;

internal sealed class LaunchAtLoginService
{
    private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Codex Meter";
    private readonly string _command;

    public LaunchAtLoginService(string executablePath)
    {
        _command = BuildCommand(executablePath);
    }

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
        return string.Equals(key?.GetValue(ValueName) as string, _command, StringComparison.OrdinalIgnoreCase);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true)
                ?? throw new InvalidOperationException("无法打开 Windows 启动项设置。");
            key.SetValue(ValueName, _command, RegistryValueKind.String);
            return;
        }

        using var existingKey = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: true);
        existingKey?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    internal static string BuildCommand(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (executablePath.Contains('"'))
        {
            throw new ArgumentException("Executable path cannot contain quotes.", nameof(executablePath));
        }
        return $"\"{executablePath}\" --startup";
    }
}
