using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace LaserConsole.ViewModels;

public sealed class RelayCommand : ICommand
{
    readonly Action      _execute;
    readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute    = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? _) => _canExecute?.Invoke() ?? true;
    public void Execute(object? _)    => _execute();
    public void Raise()               => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand : ICommand
{
    readonly Func<Task> _execute;
    readonly Func<bool>? _canExecute;
    bool _busy;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute    = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? _) => !_busy && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? _)
    {
        if (!CanExecute(null)) return;
        _busy = true;
        Raise();
        try   { await _execute(); }
        catch { /* callers are expected to catch inside the delegate */ }
        finally
        {
            _busy = false;
            Raise();
        }
    }

    public void Raise() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
