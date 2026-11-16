using Cnema.Services;
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
        if (i < 0) { Player.ShowToast(T("This video isn't in the library view right now.")); return; }
        var j = i + delta;
        if (j < 0 || j >= Videos.Count)
        {
            Player.ShowToast(delta > 0 ? T("That was the last video.") : T("This is the first video."));
            return;
        }
        if (IsPlayerOpen && Player.HasEdits && !Features.KeepEdits && !(_armedStep == delta && DateTime.UtcNow < _armedUntil))
        {
            _armedStep = delta;
            _armedUntil = DateTime.UtcNow.AddSeconds(4);
            Player.ShowToast(T(delta > 0
                ? "Press again to open the next video. Your trim marks, parts and effects here will be cleared."
                : "Press again to open the previous video. Your trim marks, parts and effects here will be cleared."));
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
    private Task PreviousClip() => StepClipAsync(-1);

    [RelayCommand]
    private Task NextClip() => StepClipAsync(1);

    // ---------- play all ----------
    /// <summary>In the Quick player, each video is followed by the next one in the library's order.</summary>
    [ObservableProperty] private bool _playAll;
    public bool ShowPlayAll => Features.PlayAll && UseMiniPlayer;
    public bool CanPlayAll => ShowPlayAll && Videos.Count > 1;

    [RelayCommand]
    private void TogglePlayAll()
    {
        PlayAll = !PlayAll;
        Player.ShowToast(PlayAll ? T("Play all: the next video starts when this one ends.") : T("Play all off."));
    }

    /// <summary>The header's Play all: the whole view, from the top, one after another.</summary>
    [RelayCommand]
    private async Task PlayAllFromStartAsync()
    {
        if (!CanPlayAll) return;
        await OpenAsync(Videos[0]);
        if (!IsMiniPlayerOpen) return;
        PlayAll = true;
        Player.ShowToast(P(Videos.Count, "Playing {0} video, one after another", "Playing all {0} videos, one after another"));
    }

    private async void OnPlayerFinished()
    {
        try
        {
            if (!PlayAll || !IsMiniPlayerOpen || Player.Current is not { } current) return;
            var i = Videos.IndexOf(current);
            if (i < 0 || i + 1 >= Videos.Count)
            {
                PlayAll = false;
                Player.ShowToast(T("That was the last one. Thanks for the memories."));
                return;
            }
            // A short breath between clips, like a slideshow.
            await Task.Delay(600);
            if (!PlayAll || !IsMiniPlayerOpen || !ReferenceEquals(Player.Current, current)) return;
            _lastStep = DateTime.MinValue;
            await StepClipAsync(1);
        }
        catch (Exception ex)
        {
            Log.Warn($"Play all: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CloseMiniPlayer()
    {
        if (!IsMiniPlayerOpen) return;
        PlayAll = false;
        var watched = Player.Current;
        Player.Close();
        IsMiniPlayerOpen = false;
        watched?.RefreshAll();
        _library.SaveCache();
        if (SortIndex == 2) RefreshView();
    }
}
