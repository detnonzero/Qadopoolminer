using System.Windows.Input;

namespace Qadopoolminer.Infrastructure.Commands;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _executeAsync;
    private readonly Func<bool>? _canExecute;
    private readonly bool _allowConcurrentExecution;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<Task> executeAsync, Func<bool>? canExecute = null, bool allowConcurrentExecution = false)
    {
        _executeAsync = executeAsync;
        _canExecute = canExecute;
        _allowConcurrentExecution = allowConcurrentExecution;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        if (!_allowConcurrentExecution && _isExecuting)
        {
            return false;
        }

        return _canExecute?.Invoke() ?? true;
    }

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        try
        {
            _isExecuting = true;
            NotifyCanExecuteChanged();
            await _executeAsync().ConfigureAwait(true);
        }
        finally
        {
            _isExecuting = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged()
        => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
