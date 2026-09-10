using System.Threading;
using System.Windows;

namespace CodexMeter.Windows;

public partial class App : System.Windows.Application
{
    private const string MutexName = @"Local\CodexMeter.Windows.v1";
    private const string ShowSignalName = @"Local\CodexMeter.Windows.Show.v1";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private CancellationTokenSource? _signalCancellation;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            SignalExistingInstance();
            Shutdown();
            return;
        }

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName);
        _signalCancellation = new CancellationTokenSource();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        StartShowSignalListener(window, _signalCancellation.Token);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _signalCancellation?.Cancel();
        _showSignal?.Set();
        _showSignal?.Dispose();
        _signalCancellation?.Dispose();

        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch (ApplicationException) { }
            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    private void StartShowSignalListener(MainWindow window, CancellationToken cancellationToken)
    {
        var signal = _showSignal!;
        _ = Task.Run(() =>
        {
            var handles = new WaitHandle[] { signal, cancellationToken.WaitHandle };
            while (!cancellationToken.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(handles) != 0) break;
                Dispatcher.BeginInvoke(window.ShowPanel);
            }
        }, cancellationToken);
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(ShowSignalName);
            signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first process is still starting. Exiting still prevents duplicates.
        }
    }
}
