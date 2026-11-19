using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// One "Are you sure?" dialog for everything that can't be undone, drawn above every other panel.
public sealed partial class MainViewModel
{
    private TaskCompletionSource<bool>? _confirm;
    [ObservableProperty] private bool _isConfirming;
    [ObservableProperty] private string _confirmTitle = "";
    [ObservableProperty] private string _confirmMessage = "";
    [ObservableProperty] private string _confirmYes = "";

    /// <summary>True when the user pressed the button that goes ahead; Cancel, Esc or a new question mean no.</summary>
    public Task<bool> ConfirmAsync(string title, string message, string yes)
    {
        _confirm?.TrySetResult(false);
        var tcs = _confirm = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfirmTitle = title;
        ConfirmMessage = message;
        ConfirmYes = yes;
        IsConfirming = true;
        return tcs.Task;
    }

    [RelayCommand] private void ConfirmYesAnswer() => Answer(true);
    [RelayCommand] private void ConfirmNoAnswer() => Answer(false);

    private void Answer(bool yes)
    {
        IsConfirming = false;
        var tcs = _confirm;
        _confirm = null;
        tcs?.TrySetResult(yes);
    }
}
