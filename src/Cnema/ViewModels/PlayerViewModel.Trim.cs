using Cnema.Controls;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// Auto-trim suggestions: the best moments pass notices a blank picture with no sound at the start or end
// (a loading screen, a fade, the menu before the round) and the editor offers to cut it.
public sealed partial class PlayerViewModel
{
    /// <summary>Settings, Features, Smart, Trim suggestions.</summary>
    public static bool TrimSuggestions { get; set; } = true;

    private VideoItemViewModel? _trimDismissed;

    public bool ShowTrimSuggestion =>
        TrimSuggestions && Current?.Entry.Smart is { HasTrimSuggestion: true } && !ReferenceEquals(_trimDismissed, Current) && !HasRange;

    public string TrimSuggestionText
    {
        get
        {
            if (Current?.Entry.Smart is not { } s) return "";
            var length = Current.Entry.DurationMs;
            var head = s.TrimStartMs > 0 ? s.TrimStartMs / 1000.0 : 0;
            var tail = s.TrimEndMs > 0 && length > s.TrimEndMs ? (length - s.TrimEndMs) / 1000.0 : 0;
            return head > 0 && tail > 0 ? F("Nothing happens in the first {0:0.#} s or the last {1:0.#} s.", head, tail)
                : head > 0 ? F("Nothing happens in the first {0:0.#} s.", head)
                : F("Nothing happens in the last {0:0.#} s.", tail);
        }
    }

    [RelayCommand]
    private void ApplyTrimSuggestion()
    {
        if (Current?.Entry.Smart is not { HasTrimSuggestion: true } s) return;
        InPoint = s.TrimStartMs > 0 ? s.TrimStartMs : -1;
        OutPoint = s.TrimEndMs > 0 ? s.TrimEndMs : -1;
        if (InPoint >= 0) Seek(InPoint);
        ShowToast(T("Trimmed. Export saves the part in between."));
        RefreshTrimSuggestion();
    }

    [RelayCommand]
    private void DismissTrimSuggestion()
    {
        _trimDismissed = Current;
        RefreshTrimSuggestion();
    }

    public void RefreshTrimSuggestion()
    {
        OnPropertyChanged(nameof(ShowTrimSuggestion));
        OnPropertyChanged(nameof(TrimSuggestionText));
    }
}
