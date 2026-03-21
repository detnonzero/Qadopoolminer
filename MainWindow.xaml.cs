using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Qadopoolminer.ViewModels;

namespace Qadopoolminer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isShutdownInProgress;
    private bool _isCloseConfirmed;
    private bool _isLogSubscriptionActive;
    private readonly DispatcherTimer _logScrollTimer;
    private bool _pendingLogScroll;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        _logScrollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(150)
        };
        _logScrollTimer.Tick += LogScrollTimerTick;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Logs is INotifyCollectionChanged notifyCollectionChanged && !_isLogSubscriptionActive)
        {
            notifyCollectionChanged.CollectionChanged += LogsCollectionChanged;
            _isLogSubscriptionActive = true;
        }

        await _viewModel.InitializeAsync();
        ScrollLogsToBottom();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isCloseConfirmed)
        {
            DetachLogSubscription();
            return;
        }

        e.Cancel = true;
        if (_isShutdownInProgress)
        {
            return;
        }

        _isShutdownInProgress = true;
        IsEnabled = false;
        Cursor = Cursors.Wait;
        Title = "Qado Pool Miner - Closing...";

        try
        {
            DetachLogSubscription();
            await _viewModel.ShutdownAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "Qado Pool Miner",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _isCloseConfirmed = true;
            _isShutdownInProgress = false;
            Cursor = null;
            Close();
        }
    }

    private void LogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _pendingLogScroll = true;
        if (!_logScrollTimer.IsEnabled)
        {
            _logScrollTimer.Start();
        }
    }

    private void LogScrollTimerTick(object? sender, EventArgs e)
    {
        if (_pendingLogScroll)
        {
            _pendingLogScroll = false;
            ScrollLogsToBottom();
        }

        _logScrollTimer.Stop();
    }

    private void ScrollLogsToBottom()
    {
        if (LogListBox.Items.Count > 0)
        {
            LogListBox.ScrollIntoView(LogListBox.Items[LogListBox.Items.Count - 1]);
        }
    }

    private void DetachLogSubscription()
    {
        if (_viewModel.Logs is INotifyCollectionChanged notifyCollectionChanged && _isLogSubscriptionActive)
        {
            notifyCollectionChanged.CollectionChanged -= LogsCollectionChanged;
            _isLogSubscriptionActive = false;
        }
    }
}
