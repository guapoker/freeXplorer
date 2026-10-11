using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace FreeXplorer.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ICommand over a plain action or an async one. Async commands ignore clicks while they run.
public sealed class RelayCommand : ICommand
{
    readonly Func<object?, Task>? asyncRun;
    readonly Action<object?>? run;
    readonly Func<object?, bool>? canRun;
    bool running;

    public RelayCommand(Action run, Func<bool>? canRun = null)
    {
        this.run = _ => run();
        this.canRun = canRun is null ? null : _ => canRun();
    }

    public RelayCommand(Func<Task> run, Func<bool>? canRun = null)
    {
        asyncRun = _ => run();
        this.canRun = canRun is null ? null : _ => canRun();
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !running && (canRun?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        await ExecuteAsync(parameter);
    }

    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
            return;
        if (asyncRun is null)
        {
            run!(parameter);
            return;
        }
        running = true;
        Changed();
        try
        {
            await asyncRun(parameter);
        }
        finally
        {
            running = false;
            Changed();
        }
    }

    public void Changed() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
