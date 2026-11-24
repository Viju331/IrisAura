using System.Windows.Input;

namespace IrisAura.Island.Views;

/// <summary>Minimal always-enabled <see cref="ICommand"/>.</summary>
public sealed class RelayCommand(Action execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute();
}
