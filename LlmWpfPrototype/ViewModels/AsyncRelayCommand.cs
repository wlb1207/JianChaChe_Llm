using System.Windows.Input;

namespace LlmWpfPrototype.ViewModels;

public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly bool _allowConcurrentExecution;
    private int _activeExecutionCount;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, bool allowConcurrentExecution = false)
    {
        _execute = execute;
        _canExecute = canExecute;
        _allowConcurrentExecution = allowConcurrentExecution;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsExecuting => Volatile.Read(ref _activeExecutionCount) > 0;

    public bool CanExecute(object? parameter)
    {
        if (!_allowConcurrentExecution && IsExecuting)
        {
            return false;
        }

        return _canExecute?.Invoke() ?? true;
    }

    public async void Execute(object? parameter)
    {
        await ExecuteAsync();
    }

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null))
        {
            return;
        }

        Interlocked.Increment(ref _activeExecutionCount);
        RaiseCanExecuteChanged();

        try
        {
            await _execute();
        }
        finally
        {
            Interlocked.Decrement(ref _activeExecutionCount);
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class AsyncRelayCommand<T> : ICommand
{
    private readonly Func<T?, Task> _execute;
    private readonly Func<T?, bool>? _canExecute;
    private bool _isExecuting;

    public AsyncRelayCommand(Func<T?, Task> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsExecuting => _isExecuting;

    public bool CanExecute(object? parameter)
    {
        if (_isExecuting)
        {
            return false;
        }

        if (_canExecute is null)
        {
            return true;
        }

        return parameter is T typed ? _canExecute(typed) : _canExecute(default);
    }

    public async void Execute(object? parameter)
    {
        var typed = parameter is T value ? value : default;
        await ExecuteAsync(typed);
    }

    public async Task ExecuteAsync(T? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();

        try
        {
            await _execute(parameter);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged()
    {
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _execute();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;

    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter)
    {
        if (_canExecute is null)
        {
            return true;
        }

        return parameter is T typed ? _canExecute(typed) : _canExecute(default);
    }

    public void Execute(object? parameter)
    {
        if (CanExecute(parameter))
        {
            _execute(parameter is T typed ? typed : default);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
