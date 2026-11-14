using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cnema.ViewModels;

public enum MontageSource { Favorites, Selected, View }

/// <summary>The Auto montage dialog: which videos, how much of each, the transition and an optional song.</summary>
public sealed partial class MontageViewModel : ObservableObject
{
    private readonly Func<MontageSource, IReadOnlyList<VideoItemViewModel>> _videos;

    public ChipGroup Source { get; }
    public ChipGroup Length { get; } = new(8, ("5 s", 5), ("8 s", 8), ("12 s", 12), ("Whole clips", 0));
    public ChipGroup Transition { get; } = new(MontageTransition.Crossfade,
        ("Crossfade", MontageTransition.Crossfade), ("Fade to black", MontageTransition.FadeBlack),
        ("Slide", MontageTransition.Slide), ("Cut", MontageTransition.Cut));
    public ChipGroup Order { get; } = new(0, ("Oldest first", 0), ("As in the library", 1));
    public ChipGroup MusicVolume { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMusic), nameof(MusicName))] private string? _musicPath;
    [ObservableProperty] private bool _musicDuck;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _canMake;

    public bool HasMusic => !string.IsNullOrEmpty(MusicPath);
    public string MusicName => HasMusic ? Path.GetFileName(MusicPath!) : T("No song (the clips keep their own sound)");

    public MontageViewModel(Func<MontageSource, IReadOnlyList<VideoItemViewModel>> videos, MontageSource initial, bool hasSelection,
        double musicDb, bool musicDuck)
    {
        _videos = videos;
        var options = new List<(string, object)> { ("Favourites", MontageSource.Favorites) };
        if (hasSelection) options.Add(("Selected", MontageSource.Selected));
        options.Add(("This view", MontageSource.View));
        Source = new ChipGroup(initial, options.ToArray());
        MusicVolume = PlayerViewModel.MusicVolumeChips(musicDb);
        _musicDuck = musicDuck;
        Source.PropertyChanged += (_, _) => Recount();
        Length.PropertyChanged += (_, _) => Recount();
        Transition.PropertyChanged += (_, _) => Recount();
        Recount();
    }

    /// <summary>The videos the montage will use, in the chosen order (at most 40).</summary>
    public List<VideoItemViewModel> Videos()
    {
        var list = _videos((MontageSource)Source.Value).Where(v => v.Entry.DurationMs >= 1000).ToList();
        if ((int)Order.Value == 0) list = list.OrderBy(v => v.Entry.ModifiedUtc).ToList();
        return list.Take(40).ToList();
    }

    private void Recount()
    {
        var videos = Videos();
        var seconds = (int)Length.Value;
        var total = videos.Sum(v => seconds <= 0 ? v.Entry.DurationMs : Math.Min(v.Entry.DurationMs, seconds * 1000L));
        if ((MontageTransition)Transition.Value != MontageTransition.Cut && videos.Count > 1) total -= MontageService.TransitionMs * (videos.Count - 1);
        CanMake = videos.Count > 0;
        Summary = videos.Count == 0
            ? T("No videos to use. Mark some as favourites, or pick another source.")
            : F("{0}, about {1} long.", P(videos.Count, "{0} video", "{0} videos"), Controls.TrimTimeline.FormatTime(Math.Max(0, total)));
    }
}
