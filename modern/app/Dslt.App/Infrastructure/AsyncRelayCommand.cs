using System.Windows.Input;

namespace Dslt.App.Infrastructure;

public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _isRunning;

    public event EventHandler? CanExecuteChanged;
    public bool IsRunning => _isRunning;
    public Task? ExecutionTask { get; private set; }
    public bool CanExecute(object? parameter) => !_isRunning && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        await ExecuteAsync().ConfigureAwait(true);
    }

    public Task ExecuteAsync()
    {
        if (!CanExecute(null)) return Task.CompletedTask;
        ExecutionTask = ExecuteCoreAsync();
        return ExecutionTask;
    }

    private async Task ExecuteCoreAsync()
    {
        _isRunning = true;
        NotifyCanExecuteChanged();
        try
        {
            await execute().ConfigureAwait(true);
        }
        finally
        {
            _isRunning = false;
            NotifyCanExecuteChanged();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
