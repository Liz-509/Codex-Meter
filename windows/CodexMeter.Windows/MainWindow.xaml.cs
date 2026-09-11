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
    private static readonly Guid SessionDisplayStatus = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10)
    ];

    private readonly CodexUsageService _usageService = new();
    private readonly DispatcherTimer _refreshTimer;
    private readonly CancellationTokenSource _lifetime = new();
    private Forms.NotifyIcon? _trayIcon;
    private Drawing.Icon? _trayIconImage;
    private bool _isRefreshing;
    private bool _isResetting;
    private bool _refreshAfterReset;
    private bool _isExiting;
    private bool _webReady;
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

        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _refreshTimer.Tick += async (_, _) => await RefreshUsageAsync();
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
        try
        {
            await InitializeWebViewAsync();
            _refreshTimer.Start();
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
                DeliverOnDispatcherAsync,
                _lifetime.Token);
            await DeliverAsync(payload);

            if (payload["error"] is null)
            {
                _retryAttempt = 0;
            }
            else
            {
                ScheduleRetry();
            }
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
        menu.Items.Add("显示 Codex Meter", null, (_, _) => Dispatcher.Invoke(ShowPanel));
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
    }

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
