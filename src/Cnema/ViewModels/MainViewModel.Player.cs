using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// The quick player: plain playback in a large panel over the library, with Edit for the full editor.
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

    private bool _stepping;
    private DateTime _lastStep;
    private int _armedStep;
    private DateTime _armedUntil;

    /// <summary>
    /// ← / →: the previous or next video in the library's current order, with its search, filters and
    /// sort. In the editor, a video with trim marks, parts or effects asks for a second press before they go.
    /// </summary>
    [RelayCommand]
    private async Task StepClipAsync(int delta)
    {
        if (Player.Current is not { } current || _stepping) return;
        // Holding the key flips through videos at a steady pace instead of queueing every repeat.
        if (DateTime.UtcNow - _lastStep < TimeSpan.FromMilliseconds(180)) return;
        var i = Videos.IndexOf(current);
        if (i < 0) { Player.ShowToast("This video isn't in the library view right now."); return; }
        var j = i + delta;
        if (j < 0 || j >= Videos.Count)
        {
            Player.ShowToast(delta > 0 ? "That was the last video." : "This is the first video.");
            return;
        }
        if (IsPlayerOpen && Player.HasEdits && !(_armedStep == delta && DateTime.UtcNow < _armedUntil))
        {
            _armedStep = delta;
            _armedUntil = DateTime.UtcNow.AddSeconds(4);
            Player.ShowToast($"Press again to open the {(delta > 0 ? "next" : "previous")} video. Your trim marks, parts and effects here will be cleared.");
            return;
        }
        _armedStep = 0;
        _stepping = true;
        try
        {
            await Player.OpenAsync(Videos[j]);
            current.RefreshAll();
        }
        finally
        {
            _stepping = false;
            _lastStep = DateTime.UtcNow;
        }
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
