using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// The mini player: plain playback in a small panel over the library, with Edit for the full editor.
public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _isMiniPlayerOpen;

    /// <summary>Videos open in the mini player first. Controller mode always uses the full player.</summary>
    private bool UseMiniPlayer => Features.MiniPlayer && !IsControllerMode;
    public bool ShowMiniPlayerButton => UseMiniPlayer;

    /// <summary>Edit: the same video, same spot, in the full player with the trim tools.</summary>
    [RelayCommand]
    private void ExpandMiniPlayer()
    {
        if (!IsMiniPlayerOpen) return;
        IsMiniPlayerOpen = false;
        IsPlayerOpen = true;
    }

    /// <summary>From the full player: keep watching in the corner while browsing the library.</summary>
    [RelayCommand]
    private void ShrinkToMiniPlayer()
    {
        if (!IsPlayerOpen || !UseMiniPlayer) return;
        IsPlayerOpen = false;
        IsMiniPlayerOpen = true;
    }

    [RelayCommand]
    private void CloseMiniPlayer()
    {
        if (!IsMiniPlayerOpen) return;
        var watched = Player.Current;
        Player.Close();
        IsMiniPlayerOpen = false;
        watched?.RefreshAll();
        _library.SaveCache();
        if (SortIndex == 2) RefreshView();
    }
}
