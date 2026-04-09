using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows;
using Qadopoolminer.Services;
using Qadopoolminer.ViewModels;

namespace Qadopoolminer;

public partial class App : Application
{
    private const string SingleInstancePrefix = "Local\\QadoPoolMiner";

    private LogService? _logService;
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activationEvent;
    private CancellationTokenSource? _activationListenerCts;
    private bool _pendingActivationRequest;

    protected override void OnStartup(StartupEventArgs e)
    {
        var instanceKey = BuildSingleInstanceKey();
        if (!TryAcquireSingleInstance(instanceKey, out var isPrimaryInstance))
        {
            Shutdown(0);
            return;
        }

        if (!isPrimaryInstance)
        {
            SignalPrimaryInstance(instanceKey);
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
        StartActivationListener();

        _logService = new LogService();
        var settingsService = new MinerSettingsService(_logService);
        var poolApiClient = new PoolApiClient(_logService);
        var clipboardService = new ClipboardService();
        var miningEngine = new MiningEngine(poolApiClient, _logService);

        var viewModel = new MainViewModel(
            settingsService,
            poolApiClient,
            miningEngine,
            clipboardService,
            _logService);

        DispatcherUnhandledException += (_, args) =>
        {
            _logService.Error("App", args.Exception.Message);
            MessageBox.Show(
                args.Exception.Message,
                "Qado Pool Miner",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        var window = new MainWindow(viewModel);
        MainWindow = window;
        window.Loaded += (_, _) => ActivateMainWindowIfPending();
        window.Show();
        ActivateMainWindowIfPending();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_activationListenerCts is not null)
        {
            _activationListenerCts.Cancel();
            _activationEvent?.Set();
            _activationListenerCts.Dispose();
            _activationListenerCts = null;
        }

        _activationEvent?.Dispose();
        _activationEvent = null;

        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }

    private bool TryAcquireSingleInstance(string instanceKey, out bool isPrimaryInstance)
    {
        _activationEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: $"{SingleInstancePrefix}.{instanceKey}.Activate");

        _singleInstanceMutex = new Mutex(initiallyOwned: false, name: $"{SingleInstancePrefix}.{instanceKey}.Mutex");

        try
        {
            isPrimaryInstance = _singleInstanceMutex.WaitOne(TimeSpan.Zero, exitContext: false);
        }
        catch (AbandonedMutexException)
        {
            isPrimaryInstance = true;
        }

        if (isPrimaryInstance)
        {
            return true;
        }

        _singleInstanceMutex.Dispose();
        _singleInstanceMutex = null;
        return true;
    }

    private void StartActivationListener()
    {
        if (_activationEvent is null)
        {
            return;
        }

        _activationListenerCts = new CancellationTokenSource();
        var cancellationToken = _activationListenerCts.Token;
        var activationEvent = _activationEvent;

        _ = Task.Run(() =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                activationEvent.WaitOne();
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _pendingActivationRequest = true;
                _ = Dispatcher.InvokeAsync(ActivateMainWindowIfPending);
            }
        }, cancellationToken);
    }

    private void ActivateMainWindowIfPending()
    {
        if (!_pendingActivationRequest)
        {
            return;
        }

        var window = MainWindow;
        if (window is null || !window.IsLoaded)
        {
            return;
        }

        _pendingActivationRequest = false;

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        var originalTopmost = window.Topmost;
        window.Topmost = true;
        window.Topmost = originalTopmost;
        window.Activate();
        window.Focus();
    }

    private static void SignalPrimaryInstance(string instanceKey)
    {
        try
        {
            using var activationEvent = EventWaitHandle.OpenExisting($"{SingleInstancePrefix}.{instanceKey}.Activate");
            activationEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
        }
    }

    private static string BuildSingleInstanceKey()
    {
        var sid = WindowsIdentity.GetCurrent()?.User?.Value;
        var identity = !string.IsNullOrWhiteSpace(sid)
            ? sid
            : Environment.UserName;

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = Path.Combine(AppContext.BaseDirectory, "Qadopoolminer.exe");
        }

        var normalizedPath = Path.GetFullPath(executablePath).Trim().ToUpperInvariant();
        var pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
        return $"{identity}.{pathHash.Substring(0, 16)}";
    }
}
