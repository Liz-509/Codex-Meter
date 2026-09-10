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
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;
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
    private bool _isRefreshing;
    private bool _isExiting;
    private bool _webReady;
    private int _retryAttempt;

    public MainWindow()
    {
        InitializeComponent();

        Browser.DefaultBackgroundColor = Drawing.Color.Transparent;
        Browser.MouseEnter += async (_, _) => await NotifyHoverAsync(entered: true);
        Browser.MouseLeave += async (_, _) => await NotifyHoverAsync(entered: false);

        Loaded += OnLoaded;
        Closed += OnClosed;
        SourceInitialized += (_, _) => PositionInitially();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

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
            window.codexMeterBridge = {
              getUsage() {
                window.chrome.webview.postMessage({ action: 'getUsage' });
                return null;
              },
              resize(payload) {
                window.chrome.webview.postMessage({ action: 'resize', ...payload });
              },
              quit() {
                window.chrome.webview.postMessage({ action: 'quit' });
              },
              beginDrag() {
                window.chrome.webview.postMessage({ action: 'beginDrag' });
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
                case "resize":
                    ResizePanel(root);
                    break;
                case "beginDrag":
                    BeginNativeDrag();
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
    }

    private async Task DeliverAsync(JsonObject payload)
    {
        if (!_webReady || Browser.CoreWebView2 is null) return;
        var json = payload.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        await Browser.CoreWebView2.ExecuteScriptAsync($"window.updateCodexUsage({json});");
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

    private async Task NotifyHoverAsync(bool entered)
    {
        if (!_webReady || Browser.CoreWebView2 is null) return;
        var function = entered ? "window.codexUsageHoverEnter" : "window.codexUsageHoverLeave";
        try { await Browser.CoreWebView2.ExecuteScriptAsync($"{function}?.();"); } catch { }
    }

    private void ResizePanel(JsonElement message)
    {
        var requestedWidth = message.TryGetProperty("width", out var widthValue) ? widthValue.GetDouble() : Width;
        var requestedHeight = message.TryGetProperty("height", out var heightValue) ? heightValue.GetDouble() : Height;
        var frame = PanelLayout.ResizeKeepingTopLeft(Left, Top, requestedWidth, requestedHeight);

        Width = frame.Width;
        Height = frame.Height;
        Left = frame.Left;
        Top = frame.Top;
    }

    private void BeginNativeDrag()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        ReleaseCapture();
        SendMessage(handle, WmNcLeftButtonDown, HtCaption, 0);
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

        var executableIcon = Environment.ProcessPath is { } path
            ? Drawing.Icon.ExtractAssociatedIcon(path)
            : null;
        _trayIcon = new Forms.NotifyIcon
        {
            Text = "Codex Meter",
            Icon = executableIcon ?? Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowPanel);
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
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _lifetime.Dispose();
        if (!_isExiting) ExitApplication();
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, int wParam, int lParam);
}
