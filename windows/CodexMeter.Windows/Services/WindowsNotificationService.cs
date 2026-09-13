using System.IO;
using System.Text.Json.Nodes;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace CodexMeter.Windows.Services;

internal sealed class WindowsNotificationService : IDisposable
{
    private readonly LocalSettingsStore _settings;
    private bool _registered;
    public event Action? Invoked;

    public WindowsNotificationService(LocalSettingsStore settings) => _settings = settings;

    internal static int RunRegistrationSelfTest()
    {
        AppNotificationManager? manager = null;
        var registered = false;
        void IgnoreInvocation(AppNotificationManager sender, AppNotificationActivatedEventArgs args) { }

        try
        {
            if (!AppNotificationManager.IsSupported()) return 2;
            manager = AppNotificationManager.Default;
            manager.NotificationInvoked += IgnoreInvocation;
            manager.Register();
            registered = true;
            if (manager.Setting == AppNotificationSetting.Unsupported) return 3;
            var notification = CreateNotification("Test", "Test", "self-test", prominent: true);
            return notification.SuppressDisplay || notification.Priority != AppNotificationPriority.High ? 4 : 0;
        }
        catch (Exception exception)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(Path.GetTempPath(), "CodexMeter.NotificationSelfTest.log"),
                    exception.ToString());
            }
            catch { }
            return 1;
        }
        finally
        {
            if (manager is not null)
            {
                try { manager.NotificationInvoked -= IgnoreInvocation; } catch { }
                if (registered)
                {
                    try { manager.Unregister(); } catch { }
                }
            }
        }
    }

    public void InitializeIfEnabled()
    {
        if (_settings.Read(value => value.NotificationsEnabled)) TryRegister(out _);
    }

    public JsonObject GetSettingsPayload(string? error = null, string? notice = null)
    {
        var supported = IsSupported();
        var systemStatus = ReadSystemStatus(supported);
        var promptSeen = _settings.Read(value => value.NotificationPromptSeen);
        var authorization = systemStatus != AppNotificationSetting.Enabled
            ? "denied"
            : !promptSeen ? "notDetermined" : "authorized";
        return _settings.Read(value => new JsonObject
        {
            ["supported"] = supported,
            ["enabled"] = value.NotificationsEnabled,
            ["thresholds"] = value.NotifyThresholds,
            ["exhausted"] = value.NotifyExhausted,
            ["restored"] = value.NotifyRestored,
            ["menuBarVisible"] = value.TrayPercentageVisible,
            ["statusArea"] = "tray",
            ["settingsLaunchSupported"] = true,
            ["authorization"] = authorization,
            ["error"] = error ?? SystemStatusMessage(systemStatus),
            ["notice"] = notice
        });
    }

    public JsonObject RequestAuthorization()
    {
        _settings.Update(value => value.NotificationPromptSeen = true);
        if (!TryRegister(out var error)) return GetSettingsPayload(error);
        var authorized = AppNotificationManager.Default.Setting == AppNotificationSetting.Enabled;
        _settings.Update(value => value.NotificationsEnabled = authorized);
        return GetSettingsPayload(authorized ? null : SystemStatusMessage(AppNotificationManager.Default.Setting));
    }

    public JsonObject UpdateSettings(JsonObject changes)
    {
        var enableRequested = ReadBool(changes["enabled"]);
        string? error = null;
        if (enableRequested is true && !TryRegister(out error)) enableRequested = false;
        _settings.Update(value =>
        {
            if (enableRequested.HasValue) value.NotificationsEnabled = enableRequested.Value;
            if (ReadBool(changes["thresholds"]) is bool thresholds) value.NotifyThresholds = thresholds;
            if (ReadBool(changes["exhausted"]) is bool exhausted) value.NotifyExhausted = exhausted;
            if (ReadBool(changes["restored"]) is bool restored) value.NotifyRestored = restored;
            value.NotificationPromptSeen = true;
        });
        return GetSettingsPayload(error);
    }

    public void DismissPrompt() => _settings.Update(value => value.NotificationPromptSeen = true);

    public JsonObject SendTest()
    {
        if (!TryRegister(out var error)) return GetSettingsPayload(error);
        try
        {
            var systemStatus = AppNotificationManager.Default.Setting;
            if (systemStatus != AppNotificationSetting.Enabled)
                return GetSettingsPayload(SystemStatusMessage(systemStatus) ?? "Windows 当前禁止显示 Codex Meter 通知。");

            Show(
                "Codex Meter 通知测试",
                "通知已成功启用。点击此通知可打开用量面板。",
                $"test-{Guid.NewGuid():N}",
                prominent: true);
            return GetSettingsPayload(
                notice: "测试通知已发送。若未看到右下角横幅，请在 Windows 通知设置中开启横幅并关闭“请勿打扰”。");
        }
        catch (Exception exception) { return GetSettingsPayload(exception.Message); }
    }

    public void Deliver(IEnumerable<QuotaEvent> events)
    {
        if (!_settings.Read(value => value.NotificationsEnabled) || !TryRegister(out _)) return;
        foreach (var item in events)
        {
            var enabled = _settings.Read(value => item.Kind switch
            {
                QuotaEventKind.Threshold => value.NotifyThresholds,
                QuotaEventKind.Exhausted => value.NotifyExhausted,
                _ => value.NotifyRestored
            });
            if (!enabled) continue;
            var percent = (int)Math.Round(item.Reading.RemainingPercent);
            var (title, body) = item.Kind switch
            {
                QuotaEventKind.Threshold => ($"{item.Reading.Label}偏低", $"当前剩余 {percent}%，请留意本周期用量。"),
                QuotaEventKind.Exhausted => ($"{item.Reading.Label}已耗尽", $"额度将在{FormatReset(item.Reading.ResetsAt)}。"),
                _ => ($"{item.Reading.Label}已恢复", $"当前剩余 {percent}%，可以继续使用。")
            };
            try { Show(title, body, item.DeduplicationKey); } catch { }
        }
    }

    private void Show(string title, string body, string tag, bool prominent = false)
        => AppNotificationManager.Default.Show(CreateNotification(title, body, tag, prominent));

    private static AppNotification CreateNotification(string title, string body, string tag, bool prominent)
    {
        var builder = new AppNotificationBuilder()
            .AddArgument("action", "show")
            .AddText(title)
            .AddText(body);
        if (prominent) builder.SetDuration(AppNotificationDuration.Long);

        var notification = builder.BuildNotification();
        // Be explicit: true silently stores the item in Notification Center without a banner.
        notification.SuppressDisplay = false;
        notification.Priority = prominent ? AppNotificationPriority.High : AppNotificationPriority.Default;
        notification.Tag = tag.Length <= 16 ? tag : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tag)))[..16];
        return notification;
    }

    private bool TryRegister(out string? error)
    {
        error = null;
        if (_registered) return true;
        try
        {
            if (!IsSupported()) { error = "此系统缺少 Windows 通知运行时，请重新运行 Codex Meter 安装程序。"; return false; }
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            AppNotificationManager.Default.Register();
            _registered = true;
            return true;
        }
        catch (Exception exception) { error = exception.Message; return false; }
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => Invoked?.Invoke();
    private static bool IsSupported() { try { return AppNotificationManager.IsSupported(); } catch { return false; } }
    private static AppNotificationSetting ReadSystemStatus(bool supported)
    {
        if (!supported) return AppNotificationSetting.Unsupported;
        try { return AppNotificationManager.Default.Setting; }
        catch { return AppNotificationSetting.Unsupported; }
    }
    private static bool? ReadBool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var result) ? result : null;
    private static string? SystemStatusMessage(AppNotificationSetting setting) => setting switch
    {
        AppNotificationSetting.DisabledForApplication => "Windows 已关闭 Codex Meter 通知，请在系统设置中允许。",
        AppNotificationSetting.DisabledForUser => "Windows 已关闭当前用户的通知，请在系统设置中允许。",
        AppNotificationSetting.DisabledByGroupPolicy => "系统策略已禁用通知，请联系管理员。",
        AppNotificationSetting.DisabledByManifest => "应用通知注册不可用，请重新安装 Codex Meter。",
        AppNotificationSetting.Unsupported => "此系统不支持应用通知。",
        _ => null
    };

    private static string FormatReset(double? timestamp)
    {
        if (!timestamp.HasValue) return "稍后重置";
        var seconds = Math.Max(0, timestamp.Value - DateTimeOffset.Now.ToUnixTimeSeconds());
        if (seconds >= 86_400) return $"{(int)(seconds / 86_400)} 天后重置";
        if (seconds >= 3_600) return $"{(int)(seconds / 3_600)} 小时后重置";
        return $"{Math.Max(1, (int)(seconds / 60))} 分钟后重置";
    }

    public void Dispose()
    {
        if (!_registered) return;
        try { AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked; AppNotificationManager.Default.Unregister(); } catch { }
        _registered = false;
    }
}
