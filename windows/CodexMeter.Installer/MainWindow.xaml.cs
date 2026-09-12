using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace CodexMeter.Installer;

public partial class MainWindow : Window
{
    private readonly bool _uninstallMode;
    private string _installDirectory;
    private bool _busy;
    private bool _succeeded;

    public MainWindow(bool uninstallMode)
    {
        InitializeComponent();
        _uninstallMode = uninstallMode;
        _installDirectory = uninstallMode
            ? InstallerService.GetInstalledDirectory()
            : InstallerService.DefaultInstallDirectory;
        ConfigureWelcomePage();
    }

    private void ConfigureWelcomePage()
    {
        LocationText.Text = _installDirectory;
        if (!_uninstallMode)
        {
            return;
        }

        Title = "卸载 Codex Meter";
        WelcomeTitle.Text = "卸载 Codex Meter";
        WelcomeSubtitle.Text = "这将移除应用和快捷方式，你可以选择是否保留偏好设置。";
        LocationLabel.Text = "将移除";
        BrowseButton.Visibility = Visibility.Collapsed;
        OptionCheckBox.Content = "保留 Codex Meter 数据与设置（不影响 Codex 会话）";
        OptionCheckBox.IsChecked = true;
        PrimaryButton.Content = "开始卸载";
    }

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        WelcomePanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        CloseButton.IsEnabled = false;
        ProgressTitle.Text = _uninstallMode ? "正在卸载 Codex Meter" : "正在安装 Codex Meter";
        InstallProgress.Value = 4;

        var progress = new Progress<InstallProgress>(update =>
        {
            ProgressStatus.Text = update.Message;
            InstallProgress.Value = update.Percent;
        });

        try
        {
            if (_uninstallMode)
            {
                var preserveSettings = OptionCheckBox.IsChecked == true;
                await Task.Run(() => InstallerService.Uninstall(_installDirectory, preserveSettings, progress));
            }
            else
            {
                var createDesktopShortcut = OptionCheckBox.IsChecked == true;
                await Task.Run(() => InstallerService.Install(_installDirectory, createDesktopShortcut, progress));
            }

            _succeeded = true;
            ShowResult(success: true, null);
        }
        catch (Exception exception)
        {
            ShowResult(success: false, exception.Message);
        }
        finally
        {
            _busy = false;
            CloseButton.IsEnabled = true;
        }
    }

    private void ShowResult(bool success, string? error)
    {
        ProgressPanel.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Visible;

        if (success)
        {
            ResultGlyph.Text = "✓";
            ResultGlyph.Foreground = new SolidColorBrush(Color.FromRgb(61, 187, 153));
            ResultIcon.Background = new SolidColorBrush(Color.FromArgb(23, 104, 218, 191));
            ResultTitle.Text = _uninstallMode ? "卸载完成" : "安装完成";
            ResultSubtitle.Text = _uninstallMode ? "Codex Meter 已从此电脑移除。" : "Codex Meter 已准备就绪。";
            ResultPrimaryButton.Content = _uninstallMode ? "关闭" : "立即启动";
            ResultSecondaryButton.Visibility = _uninstallMode ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        ResultGlyph.Text = "!";
        ResultGlyph.Foreground = new SolidColorBrush(Color.FromRgb(222, 88, 103));
        ResultIcon.Background = new SolidColorBrush(Color.FromArgb(25, 222, 88, 103));
        ResultTitle.Text = _uninstallMode ? "卸载未完成" : "安装未完成";
        ResultSubtitle.Text = error ?? "发生未知错误，请重试。";
        ResultPrimaryButton.Content = "重试";
        ResultSecondaryButton.Visibility = Visibility.Visible;
    }

    private void ResultPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_succeeded)
        {
            ResultPanel.Visibility = Visibility.Collapsed;
            WelcomePanel.Visibility = Visibility.Visible;
            return;
        }

        if (!_uninstallMode)
        {
            InstallerService.LaunchInstalledApp(_installDirectory);
        }

        Close();
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e) => Close();

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var currentParent = Directory.GetParent(_installDirectory)?.FullName
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dialog = new OpenFolderDialog
        {
            Title = "选择 Codex Meter 的安装位置",
            InitialDirectory = Directory.Exists(currentParent) ? currentParent : null,
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _installDirectory = string.Equals(
            Path.GetFileName(dialog.FolderName.TrimEnd(Path.DirectorySeparatorChar)),
            "Codex Meter",
            StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(dialog.FolderName)
            : Path.Combine(Path.GetFullPath(dialog.FolderName), "Codex Meter");
        LocationText.Text = _installDirectory;
        LocationText.ToolTip = _installDirectory;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy)
        {
            Close();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    internal void SavePreview(string path)
    {
        UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY)),
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(this);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
