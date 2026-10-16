using System.Windows.Input;

namespace HalconTraining.App.ViewModels;

public sealed class AsyncCommand(Func<CancellationToken, Task> execute) : ICommand
{
    private bool _isRunning;
    private CancellationTokenSource? _cancellation;

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_isRunning;

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _isRunning = true;
        _cancellation = new CancellationTokenSource();
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute(_cancellation.Token);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            _isRunning = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add => System.Windows.Input.CommandManager.RequerySuggested += value;
        remove => System.Windows.Input.CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
}
