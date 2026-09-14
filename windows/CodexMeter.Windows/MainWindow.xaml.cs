using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CodexMeter.Windows.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace CodexMeter.Windows;

public partial class MainWindow : Window
{
    private const uint WmNcLeftButtonDown = 0x00A1;
    private const int WmExitSizeMove = 0x0232;
    private const int WmPowerBroadcast = 0x0218;
    private const int PbtPowerSettingChange = 0x8013;
    private const int DeviceNotifyWindowHandle = 0;
    private const int HtCaption = 0x0002;
    private static readonly TimeSpan StartupRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    ];

    private readonly CodexUsageService _usageService = new();
    private readonly LocalSettingsStore _settings;
    private readonly QuotaMonitor _quotaMonitor;
    private readonly WindowsNotificationService _notifications;
    private readonly LaunchAtLoginService _launchAtLoginService = new(
        Environment.ProcessPath ?? throw new InvalidOperationException("无法确定应用程序路径。"));
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _contextHealthTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private Forms.NotifyIcon? _trayIcon;
    private Drawing.Icon? _trayIconImage;
    private Forms.ToolStripMenuItem? _primaryTrayItem;
    private Forms.ToolStripMenuItem? _secondaryTrayItem;
    private Forms.ToolStripMenuItem? _forecastTrayItem;
    private JsonObject? _latestCompletePayload;
    private IReadOnlyList<QuotaReading> _latestReadings = [];
    private JsonObject _latestForecast = new();
    private bool _isRefreshing;
    private bool _isResetting;
    private bool _isRefreshingContext;
    private bool _refreshAfterReset;
    private bool _isExiting;
    private bool _webReady;
    private bool _hasReceivedUsage;
    private int _retryAttempt;
    private System.Windows.Point? _compactOrigin;
    private HwndSource? _windowSource;
    private bool _isNativeDragging;
    private bool _sessionActive = true;
    private bool _powerActive = true;
    private bool _displayActive = true;
    private nint _displayPowerNotification;

    public MainWindow()
    {
        InitializeComponent();

        _settings = new LocalSettingsStore();
        _quotaMonitor = new QuotaMonitor(_settings);
        _notifications = new WindowsNotificationService(_settings);
        _notifications.Invoked += () => Dispatcher.Invoke(ShowPanel);

        Browser.DefaultBackgroundColor = Drawing.Color.Transparent;
        Browser.MouseEnter += async (_, eventArgs) =>
            await NotifyHoverAsync(entered: true, eventArgs.GetPosition(Browser));
        Browser.MouseLeave += async (_, _) => await NotifyHoverAsync(entered: false);

        Loaded += OnLoaded;
        Closed += OnClosed;
        SourceInitialized += OnSourceInitialized;
        IsVisibleChanged += OnIsVisibleChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _refreshTimer.Tick += async (_, _) => await RefreshUsageAsync();
        _contextHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _contextHealthTimer.Tick += async (_, _) => await RefreshCurrentContextHealthAsync();
    }

    public void ShowPanel()
    {
        if (!IsVisible) Show();
        Topmost = true;
        WindowState = WindowState.Normal;
        Activate();
        ClampToCurrentWorkArea();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        InitializeTrayIcon();
        _notifications.InitializeIfEnabled();
        try
        {
            await InitializeWebViewAsync();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowWebViewRuntimeMessage();
            ExitApplication();
        }
        catch (Exception error)
        {
            System.Windows.MessageBox.Show(
                $"Codex Meter 无法启动 WebView2：\n\n{error.Message}",
                "Codex Meter",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ExitApplication();
        }
    }

    private async Task InitializeWebViewAsync()
    {
        _ = CoreWebView2Environment.GetAvailableBrowserVersionString();

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Codex Meter",
            "WebView2");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
        await Browser.EnsureCoreWebView2Async(environment);

        Browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        Browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
        Browser.CoreWebView2.Settings.IsStatusBarEnabled = false;
        Browser.CoreWebView2.Settings.IsZoomControlEnabled = false;
        Browser.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

        await Browser.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            """
            window.codexMeterHostActive = true;
            window.codexMeterBridge = {
              getUsage() {
                window.chrome.webview.postMessage({ action: 'getUsage' });
                return null;
              },
              consumeReset(payload) {
                window.chrome.webview.postMessage({ action: 'consumeReset', confirmed: payload?.confirmed === true });
              },
              resize(payload) {
                window.chrome.webview.postMessage({ action: 'resize', ...payload });
              },
              quit() {
                window.chrome.webview.postMessage({ action: 'quit' });
              },
              beginDrag(payload) {
                window.chrome.webview.postMessage({ action: 'beginDrag', ...payload });
              },
              getLaunchAtLogin() {
                window.chrome.webview.postMessage({ action: 'getLaunchAtLogin' });
              },
              setLaunchAtLogin(payload) {
                window.chrome.webview.postMessage({ action: 'setLaunchAtLogin', enabled: payload?.enabled === true });
              },
              getNotificationSettings() {
                window.chrome.webview.postMessage({ action: 'getNotificationSettings' });
              },
              setNotificationSettings(payload) {
                window.chrome.webview.postMessage({ action: 'setNotificationSettings', ...payload });
              },
              requestNotificationAuthorization() {
                window.chrome.webview.postMessage({ action: 'requestNotificationAuthorization' });
              },
              sendTestNotification() {
                window.chrome.webview.postMessage({ action: 'sendTestNotification' });
              },
              dismissNotificationPrompt() {
                window.chrome.webview.postMessage({ action: 'dismissNotificationPrompt' });
              },
              openNotificationSettings() {
                window.chrome.webview.postMessage({ action: 'openNotificationSettings' });
              },
              setMenuBarVisible(payload) {
                window.chrome.webview.postMessage({ action: 'setMenuBarVisible', enabled: payload?.enabled !== false });
              },
              exportReport(payload) {
                window.chrome.webview.postMessage({ action: 'exportReport', format: payload?.format || 'md' });
              }
            };
            """);

        var resourcesDirectory = Path.Combine(AppContext.BaseDirectory, "Resources");
        if (!File.Exists(Path.Combine(resourcesDirectory, "companion.html")))
        {
            throw new FileNotFoundException("Resources\\companion.html 不存在。请重新下载完整压缩包。");
        }

        Browser.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "app.codex-meter.local",
            resourcesDirectory,
            CoreWebView2HostResourceAccessKind.DenyCors);
        Browser.CoreWebView2.NavigationCompleted += async (_, _) =>
        {
            _webReady = true;
            await PublishHostActiveAsync();
            await RefreshUsageAsync();
        };
        Browser.CoreWebView2.Navigate("https://app.codex-meter.local/companion.html");
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("action", out var actionElement)) return;

            switch (actionElement.GetString())
            {
                case "getUsage":
                    await RefreshUsageAsync();
                    break;
                case "consumeReset":
                    if (root.TryGetProperty("confirmed", out var confirmed) &&
                        confirmed.ValueKind is JsonValueKind.True)
                    {
                        await ConsumeResetCreditAsync();
                    }
                    break;
                case "resize":
                    ResizePanel(root);
                    break;
                case "beginDrag":
                    BeginNativeDrag(root);
                    break;
                case "getLaunchAtLogin":
                    await DeliverLaunchAtLoginResultAsync();
                    break;
                case "setLaunchAtLogin":
                    var enabled = root.TryGetProperty("enabled", out var enabledValue) &&
                        enabledValue.ValueKind is JsonValueKind.True;
                    await SetLaunchAtLoginAsync(enabled);
                    break;
                case "getNotificationSettings":
                    await DeliverNotificationSettingsAsync(_notifications.GetSettingsPayload());
                    break;
                case "setNotificationSettings":
                    await DeliverNotificationSettingsAsync(_notifications.UpdateSettings(JsonNode.Parse(root.GetRawText())!.AsObject()));
                    break;
                case "requestNotificationAuthorization":
                    await DeliverNotificationSettingsAsync(_notifications.RequestAuthorization());
                    break;
                case "sendTestNotification":
                    await DeliverNotificationSettingsAsync(_notifications.SendTest());
                    break;
                case "dismissNotificationPrompt":
                    _notifications.DismissPrompt();
                    await DeliverNotificationSettingsAsync(_notifications.GetSettingsPayload());
                    break;
                case "openNotificationSettings":
                    OpenNotificationSettings();
                    break;
                case "setMenuBarVisible":
                    var trayPercentage = !root.TryGetProperty("enabled", out var trayValue) || trayValue.ValueKind is not JsonValueKind.False;
                    _settings.Update(value => value.TrayPercentageVisible = trayPercentage);
                    UpdateTrayIcon();
                    await DeliverNotificationSettingsAsync(_notifications.GetSettingsPayload());
                    break;
                case "exportReport":
                    await ExportReportAsync(root.TryGetProperty("format", out var format) ? format.GetString() : "md");
                    break;
                case "quit":
                    ExitApplication();
                    break;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed messages from local web content.
        }
    }

    private async Task RefreshUsageAsync()
    {
        if (_isRefreshing || !_webReady) return;
        _isRefreshing = true;

        try
        {
            var payload = await _usageService.FetchAsync(
                async partial =>
                {
                    partial["capabilities"] = Capabilities(notificationPromptNeeded: false);
                    await DeliverOnDispatcherAsync(partial);
                },
                _lifetime.Token);
            var succeeded = payload["error"] is null;
            if (succeeded)
            {
                var analytics = _quotaMonitor.Process(payload);
                payload["forecast"] = analytics.Forecast;
                _latestReadings = analytics.Readings;
                _latestForecast = (JsonObject)analytics.Forecast.DeepClone();
                _latestCompletePayload = (JsonObject)payload.DeepClone();
                _notifications.Deliver(analytics.Events);
                UpdateTrayStatus();
            }
            payload["capabilities"] = Capabilities(succeeded && !_settings.Read(value => value.NotificationPromptSeen));
            await DeliverAsync(payload);

            if (succeeded)
            {
                _hasReceivedUsage = true;
                _retryAttempt = 0;
                if (!_refreshTimer.IsEnabled) _refreshTimer.Start();
            }
            else if (!_hasReceivedUsage)
            {
                ScheduleStartupRetry();
            }
            else
            {
                ScheduleRetry();
            }
            if (!_contextHealthTimer.IsEnabled) _contextHealthTimer.Start();
            await RefreshCurrentContextHealthAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _isRefreshing = false;
        }

        if (_refreshAfterReset)
        {
            _refreshAfterReset = false;
            await RefreshUsageAsync();
        }
    }

    private async Task ConsumeResetCreditAsync()
    {
        if (_isResetting) return;
        _isResetting = true;
        try
        {
            var payload = await _usageService.ConsumeResetCreditAsync(
                Guid.NewGuid().ToString("D"),
                _lifetime.Token);
            await DeliverResetResultAsync(payload);
            if (_isRefreshing)
            {
                _refreshAfterReset = true;
            }
            else
            {
                await RefreshUsageAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _isResetting = false;
        }
    }

    private async Task DeliverAsync(JsonObject payload)
    {
        if (!_webReady || Browser.CoreWebView2 is null) return;
        var json = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        await Browser.CoreWebView2.ExecuteScriptAsync($"window.updateCodexUsage({json});");
    }

    private async Task DeliverResetResultAsync(JsonObject payload)
    {
        if (!_webReady || Browser.CoreWebView2 is null) return;
        var json = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        await Browser.CoreWebView2.ExecuteScriptAsync($"window.codexResetResult?.({json});");
    }

    private static JsonObject Capabilities(bool notificationPromptNeeded) => new()
    {
        ["extendedInsights"] = true,
        ["contextHealth"] = true,
        ["notifications"] = true,
        ["menuBar"] = true,
        ["reportExport"] = true,
        ["notificationPromptNeeded"] = notificationPromptNeeded
    };

    private async Task RefreshCurrentContextHealthAsync()
    {
        if (_isRefreshingContext || !_webReady || !_sessionActive || !_powerActive || !_displayActive || !IsVisible) return;
        _isRefreshingContext = true;
        try
        {
            var payload = await _usageService.FetchCurrentContextHealthAsync(_lifetime.Token);
            if (payload is null || Browser.CoreWebView2 is null) return;
            var json = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            await Browser.CoreWebView2.ExecuteScriptAsync($"window.updateCodexContextHealth?.({json});");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _isRefreshingContext = false; }
    }

    private async Task DeliverNotificationSettingsAsync(JsonObject payload)
    {
        if (!_webReady || Browser.CoreWebView2 is null) return;
        await Browser.CoreWebView2.ExecuteScriptAsync($"window.codexNotificationSettingsResult?.({payload.ToJsonString()});");
    }

    private async Task DeliverExportResultAsync(JsonObject payload)
    {
        if (!_webReady || Browser.CoreWebView2 is null) return;
        await Browser.CoreWebView2.ExecuteScriptAsync($"window.codexExportResult?.({payload.ToJsonString()});");
    }

    private async Task ExportReportAsync(string? requestedFormat)
    {
        if (_latestCompletePayload is null)
        {
            await DeliverExportResultAsync(new JsonObject { ["error"] = "请等待首次完整同步后再导出。" });
            return;
        }
        var format = string.Equals(requestedFormat, "csv", StringComparison.OrdinalIgnoreCase) ? "csv" : "md";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"Codex-Meter-Report-{DateTime.Now:yyyy-MM-dd}.{format}",
            DefaultExt = $".{format}",
            Filter = format == "csv" ? "CSV 文件 (*.csv)|*.csv" : "Markdown 文件 (*.md)|*.md",
            AddExtension = true,
            OverwritePrompt = true
        };
        ShowPanel();
        if (dialog.ShowDialog(this) != true)
        {
            await DeliverExportResultAsync(new JsonObject { ["cancelled"] = true });
            return;
        }
        try
        {
            if (format == "csv") await File.WriteAllBytesAsync(dialog.FileName, ReportGenerator.Csv(_latestCompletePayload), _lifetime.Token);
            else await File.WriteAllTextAsync(dialog.FileName, ReportGenerator.Markdown(_latestCompletePayload), new System.Text.UTF8Encoding(false), _lifetime.Token);
            await DeliverExportResultAsync(new JsonObject { ["success"] = true, ["path"] = dialog.FileName });
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            await DeliverExportResultAsync(new JsonObject { ["error"] = error.Message });
        }
    }

    private static void OpenNotificationSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:notifications") { UseShellExecute = true }); } catch { }
    }

    private async Task DeliverLaunchAtLoginResultAsync(string? error = null)
    {
        if (!_webReady || Browser.CoreWebView2 is null) return;
        bool enabled;
        try
        {
            enabled = _launchAtLoginService.IsEnabled();
        }
        catch (Exception exception)
        {
            enabled = false;
            error ??= exception.Message;
        }

        var payload = JsonSerializer.Serialize(new { supported = true, enabled, error });
        await Browser.CoreWebView2.ExecuteScriptAsync($"window.codexLaunchAtLoginResult?.({payload});");
    }

    private async Task SetLaunchAtLoginAsync(bool enabled)
    {
        string? error = null;
        try
        {
            _launchAtLoginService.SetEnabled(enabled);
        }
        catch (Exception exception)
        {
            error = exception.Message;
        }
        await DeliverLaunchAtLoginResultAsync(error);
    }

    private Task DeliverOnDispatcherAsync(JsonObject payload)
    {
        if (Dispatcher.CheckAccess()) return DeliverAsync(payload);
        return Dispatcher
            .InvokeAsync(new Func<Task>(() => DeliverAsync(payload)))
            .Task
            .Unwrap();
    }

    private void ScheduleRetry()
    {
        if (_retryAttempt >= RetryDelays.Length) return;
        var delay = RetryDelays[_retryAttempt++];
        _ = RetryAfterDelayAsync(delay);
    }

    private void ScheduleStartupRetry() => _ = RetryAfterDelayAsync(StartupRetryDelay);

    private async Task RetryAfterDelayAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _lifetime.Token);
            await RefreshUsageAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task NotifyHoverAsync(bool entered, System.Windows.Point? pointer = null)
    {
        if (!_webReady || Browser.CoreWebView2 is null) return;
        if (!entered && _isNativeDragging) return;
        var function = entered ? "window.codexUsageHoverEnter" : "window.codexUsageHoverLeave";
        var argument = pointer is { } point
            ? JsonSerializer.Serialize(new { x = point.X, y = point.Y })
            : string.Empty;
        try { await Browser.CoreWebView2.ExecuteScriptAsync($"{function}?.({argument});"); } catch { }
    }

    private void ResizePanel(JsonElement message)
    {
        var requestedWidth = message.TryGetProperty("width", out var widthValue) ? widthValue.GetDouble() : Width;
        var requestedHeight = message.TryGetProperty("height", out var heightValue) ? heightValue.GetDouble() : Height;
        var expanding = requestedWidth > PanelLayout.CompactSize || requestedHeight > PanelLayout.CompactSize;
        var area = GetCurrentWorkArea();

        if (expanding && _compactOrigin is null)
        {
            _compactOrigin = new System.Windows.Point(Left, Top);
        }

        var origin = _compactOrigin ?? new System.Windows.Point(Left, Top);
        var frame = expanding
            ? PanelLayout.ResizeWithinWorkArea(
                origin.X,
                origin.Y,
                requestedWidth,
                requestedHeight,
                area.Left,
                area.Top,
                area.Right,
                area.Bottom)
            : PanelLayout.ResizeKeepingTopLeft(
                origin.X,
                origin.Y,
                requestedWidth,
                requestedHeight);

        Width = frame.Width;
        Height = frame.Height;
        Left = frame.Left;
        Top = frame.Top;

        if (expanding && _compactOrigin is { } compactOrigin)
        {
            var anchorX = ReadFiniteNumber(message, "anchorX", PanelLayout.CompactSize / 2);
            var anchorY = ReadFiniteNumber(message, "anchorY", PanelLayout.CompactSize / 2);
            UpdateExpansionAnchor(
                compactOrigin.X - frame.Left,
                compactOrigin.Y - frame.Top,
                anchorX,
                anchorY);
        }
        else
        {
            _compactOrigin = null;
            UpdateExpansionAnchor(0, 0, PanelLayout.CompactSize / 2, PanelLayout.CompactSize / 2);
        }
    }

    private static double ReadFiniteNumber(JsonElement message, string propertyName, double fallback)
    {
        if (!message.TryGetProperty(propertyName, out var value) ||
            !value.TryGetDouble(out var number) ||
            !double.IsFinite(number))
        {
            return fallback;
        }
        return Math.Clamp(number, 0, PanelLayout.CompactSize);
    }

    private void UpdateExpansionAnchor(double compactX, double compactY, double pointerX, double pointerY)
    {
        if (!_webReady || Browser.CoreWebView2 is null) return;
        var payload = JsonSerializer.Serialize(new
        {
            compactX,
            compactY,
            pointerX = compactX + pointerX,
            pointerY = compactY + pointerY
        });
        _ = Browser.CoreWebView2.ExecuteScriptAsync($"window.codexUsageSetPanelAnchor?.({payload});");
    }

    private void BeginNativeDrag(JsonElement message)
    {
        if (!message.TryGetProperty("x", out var xValue) ||
            !message.TryGetProperty("y", out var yValue) ||
            !xValue.TryGetDouble(out var x) ||
            !yValue.TryGetDouble(out var y) ||
            !PanelLayout.IsDragHandle(x, y))
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetCursorPos(out var cursor)) return;
        _isNativeDragging = true;
        _compactOrigin = new System.Windows.Point(Left, Top);
        Width = PanelLayout.CompactSize;
        Height = PanelLayout.CompactSize;
        Left = _compactOrigin.Value.X;
        Top = _compactOrigin.Value.Y;
        UpdateExpansionAnchor(0, 0, PanelLayout.CompactSize / 2, PanelLayout.CompactSize / 2);
        ReleaseCapture();
        SendMessage(handle, WmNcLeftButtonDown, (nint)HtCaption, PackScreenPoint(cursor));
        if (_isNativeDragging)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(CompleteNativeDrag));
        }
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        PositionInitially();
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
        var sessionDisplayStatus = SessionDisplayStatus;
        _displayPowerNotification = RegisterPowerSettingNotification(
            handle,
            ref sessionDisplayStatus,
            DeviceNotifyWindowHandle);
    }

    private nint WindowMessageHook(
        nint hwnd,
        int message,
        nint wParam,
        nint lParam,
        ref bool handled)
    {
        if (message == WmExitSizeMove && _isNativeDragging)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(CompleteNativeDrag));
        }
        else if (message == WmPowerBroadcast && wParam.ToInt32() == PbtPowerSettingChange)
        {
            var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
            if (setting.PowerSetting == SessionDisplayStatus)
            {
                _displayActive = setting.Data != 0;
                _ = PublishHostActiveAsync();
            }
        }
        return 0;
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        _sessionActive = e.Reason switch
        {
            SessionSwitchReason.SessionLock or
            SessionSwitchReason.ConsoleDisconnect or
            SessionSwitchReason.RemoteDisconnect => false,
            SessionSwitchReason.SessionUnlock or
            SessionSwitchReason.ConsoleConnect or
            SessionSwitchReason.RemoteConnect => true,
            _ => _sessionActive
        };
        _ = Dispatcher.InvokeAsync(PublishHostActiveAsync);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) _powerActive = false;
        if (e.Mode == PowerModes.Resume) _powerActive = true;
        _ = Dispatcher.InvokeAsync(PublishHostActiveAsync);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        _ = PublishHostActiveAsync();

    private async Task PublishHostActiveAsync()
    {
        if (!_webReady || Browser.CoreWebView2 is null) return;
        var active = _sessionActive && _powerActive && _displayActive && IsVisible && !_isExiting;
        try
        {
            await Browser.CoreWebView2.ExecuteScriptAsync(
                $"window.codexUsageSetHostActive?.({active.ToString().ToLowerInvariant()});");
        }
        catch
        {
            // The web view may be navigating or shutting down.
        }
    }

    private async void CompleteNativeDrag()
    {
        if (!_isNativeDragging) return;
        _isNativeDragging = false;
        _compactOrigin = new System.Windows.Point(Left, Top);
        if (_compactOrigin is { } compactOrigin)
        {
            Width = PanelLayout.CompactSize;
            Height = PanelLayout.CompactSize;
            Left = compactOrigin.X;
            Top = compactOrigin.Y;
        }
        if (!_webReady || Browser.CoreWebView2 is null) return;
        try { await Browser.CoreWebView2.ExecuteScriptAsync("window.codexUsageDragEnded?.();"); } catch { }
    }

    private static nint PackScreenPoint(NativePoint point)
    {
        var packed = (uint)(ushort)point.X | ((uint)(ushort)point.Y << 16);
        return unchecked((nint)packed);
    }

    private void PositionInitially()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 18;
        Top = workArea.Top + 18;
    }

    private void ClampToCurrentWorkArea()
    {
        var area = GetCurrentWorkArea();
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    private Rect GetCurrentWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;

        var screen = Forms.Screen.FromHandle(handle);
        var source = HwndSource.FromHwnd(handle);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(screen.WorkingArea.Left, screen.WorkingArea.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(screen.WorkingArea.Right, screen.WorkingArea.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private void InitializeTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        _primaryTrayItem = new Forms.ToolStripMenuItem("5 小时额度：—") { Enabled = false };
        _secondaryTrayItem = new Forms.ToolStripMenuItem("每周额度：—") { Enabled = false };
        _forecastTrayItem = new Forms.ToolStripMenuItem("趋势估算：暂无足够数据") { Enabled = false };
        menu.Items.Add(_primaryTrayItem);
        menu.Items.Add(_secondaryTrayItem);
        menu.Items.Add(_forecastTrayItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("显示 Codex Meter", null, (_, _) => Dispatcher.Invoke(ShowPanel));
        menu.Items.Add("立即刷新", null, async (_, _) => await Dispatcher.InvokeAsync(RefreshUsageAsync).Task.Unwrap());
        menu.Items.Add("打开设置", null, (_, _) => Dispatcher.Invoke(OpenSettings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitApplication));

        _trayIconImage = LoadApplicationIcon();
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Codex Meter",
            Icon = _trayIconImage,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowPanel);
        UpdateTrayStatus();
    }

    private void OpenSettings()
    {
        ShowPanel();
        if (_webReady && Browser.CoreWebView2 is not null) _ = Browser.CoreWebView2.ExecuteScriptAsync("window.codexUsageOpenDialog?.('settings');");
    }

    private void UpdateTrayStatus()
    {
        if (_trayIcon is null) return;
        var primary = _latestReadings.FirstOrDefault(item => item.Key == "primary");
        var secondary = _latestReadings.FirstOrDefault(item => item.Key == "secondary");
        _primaryTrayItem!.Text = $"5 小时额度：{FormatTrayReading(primary)}";
        _secondaryTrayItem!.Text = $"每周额度：{FormatTrayReading(secondary)}";
        var primaryForecast = _latestForecast["primary"] as JsonObject;
        _forecastTrayItem!.Text = $"趋势估算：{ForecastDescription(primaryForecast)}";
        _trayIcon.Text = TrimTrayText($"Codex Meter\n5 小时 {FormatTrayPercent(primary)} · 每周 {FormatTrayPercent(secondary)}\n{ForecastDescription(primaryForecast)}");
        UpdateTrayIcon();
    }

    private void UpdateTrayIcon()
    {
        if (_trayIcon is null) return;
        Drawing.Icon next;
        var primary = _latestReadings.FirstOrDefault(item => item.Key == "primary");
        if (_settings.Read(value => value.TrayPercentageVisible) && primary is not null) next = TrayIconRenderer.Render((int)Math.Round(primary.RemainingPercent));
        else next = LoadApplicationIcon();
        var previous = _trayIconImage;
        _trayIconImage = next;
        _trayIcon.Icon = next;
        previous?.Dispose();
    }

    private static string FormatTrayReading(QuotaReading? reading) => reading is null ? "—" : $"{(int)Math.Round(reading.RemainingPercent)}% · {FormatTrayReset(reading.ResetsAt)}";
    private static string FormatTrayPercent(QuotaReading? reading) => reading is null ? "—" : $"{(int)Math.Round(reading.RemainingPercent)}%";
    private static string FormatTrayReset(double? timestamp)
    {
        if (!timestamp.HasValue) return "等待同步";
        var seconds = Math.Max(0, timestamp.Value - DateTimeOffset.Now.ToUnixTimeSeconds());
        if (seconds >= 86_400) return $"{(int)(seconds / 86_400)} 天后重置";
        if (seconds >= 3_600) return $"{(int)(seconds / 3_600)} 小时后重置";
        return $"{Math.Max(1, (int)(seconds / 60))} 分钟后重置";
    }

    private static string ForecastDescription(JsonObject? forecast)
    {
        if (forecast is null) return "暂无足够数据";
        if (forecast["status"]?.GetValue<string>() == "will_deplete" && forecast["estimatedExhaustsAt"] is JsonValue value && value.TryGetValue<double>(out var timestamp))
            return $"预计 {DateTimeOffset.FromUnixTimeSeconds((long)timestamp).ToLocalTime():M月d日 HH:mm} 耗尽";
        return forecast["message"]?.GetValue<string>() ?? "暂无足够数据";
    }

    private static string TrimTrayText(string value) => value.Length <= 127 ? value : value[..127];

    private static Drawing.Icon LoadApplicationIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/AppIcon.ico", UriKind.Absolute));
        if (resource is not null)
        {
            using var stream = resource.Stream;
            using var icon = new Drawing.Icon(stream);
            return (Drawing.Icon)icon.Clone();
        }

        return Environment.ProcessPath is { } path
            ? Drawing.Icon.ExtractAssociatedIcon(path) ?? (Drawing.Icon)Drawing.SystemIcons.Application.Clone()
            : (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    private static void ShowWebViewRuntimeMessage()
    {
        var result = System.Windows.MessageBox.Show(
            "Codex Meter 需要 Microsoft Edge WebView2 Runtime。是否打开官方下载页面？",
            "Codex Meter",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information);
        if (result == MessageBoxResult.Yes)
        {
            Process.Start(new ProcessStartInfo(
                "https://developer.microsoft.com/microsoft-edge/webview2/#download-section")
            {
                UseShellExecute = true
            });
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.Invoke(ClampToCurrentWorkArea);

    private void ExitApplication()
    {
        if (_isExiting) return;
        _isExiting = true;
        System.Windows.Application.Current.Shutdown();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _lifetime.Cancel();
        _refreshTimer.Stop();
        _contextHealthTimer.Stop();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        IsVisibleChanged -= OnIsVisibleChanged;
        if (_displayPowerNotification != 0)
        {
            UnregisterPowerSettingNotification(_displayPowerNotification);
            _displayPowerNotification = 0;
        }
        _windowSource?.RemoveHook(WindowMessageHook);
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _trayIconImage?.Dispose();
        Browser.Dispose();
        _notifications.Dispose();
        _usageService.Dispose();
        _lifetime.Dispose();
        if (!_isExiting) ExitApplication();
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(IntPtr hWnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint RegisterPowerSettingNotification(
        nint recipient,
        ref Guid powerSettingGuid,
        int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterPowerSettingNotification(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerBroadcastSetting
    {
        public Guid PowerSetting;
        public int DataLength;
        public int Data;
    }
}
