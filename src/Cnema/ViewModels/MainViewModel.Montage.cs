using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// Auto montage: the best moments of favourites (or a selection) stitched into one video.
public sealed partial class MainViewModel
{
    /// <summary>Set by the view: an open dialog for an audio file.</summary>
    public Func<Task<string?>>? PickAudio { get; set; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsMakingMontage))] private MontageViewModel? _montage;
    public bool IsMakingMontage => Montage is not null;
    public bool ShowMontage => Features.Montage;

    private IReadOnlyList<VideoItemViewModel> MontageVideos(MontageSource source) => source switch
    {
        MontageSource.Selected => Videos.Where(v => v.IsSelected).ToList(),
        MontageSource.View => Videos.ToList(),
        _ => _items.Values.Where(v => v.Entry.Favorite).ToList(),
    };

    [RelayCommand]
    private void OpenMontage()
    {
        var hasSelection = IsSelecting && Videos.Any(v => v.IsSelected);
        var source = hasSelection ? MontageSource.Selected
            : _items.Values.Any(v => v.Entry.Favorite) ? MontageSource.Favorites : MontageSource.View;
        var fx = _settings.Current.Effects;
        Montage = new MontageViewModel(MontageVideos, source, hasSelection, fx.MusicVolumeDb, fx.MusicDuck);
    }

    [RelayCommand] private void CloseMontage() => Montage = null;

    [RelayCommand]
    private async Task PickMontageMusicAsync()
    {
        if (Montage is null || PickAudio is null || await PickAudio() is not { Length: > 0 } path) return;
        Montage.MusicPath = path;
    }

    [RelayCommand]
    private void MakeMontage()
    {
        if (Montage is not { CanMake: true } m) return;
        var videos = m.Videos().Select(v => (v.Entry.Path, v.Entry.DurationMs)).ToList();
        var seconds = (int)m.Length.Value;
        var transition = (MontageTransition)m.Transition.Value;
        var music = m.HasMusic ? new MusicSpec(m.MusicPath!, (double)m.MusicVolume.Value, m.MusicDuck) : null;
        var fx = _settings.Current.Effects;
        fx.MusicVolumeDb = (double)m.MusicVolume.Value;
        fx.MusicDuck = m.MusicDuck;
        _settings.Save();

        // Next to Cnema's own clips when the library shows them, otherwise in the first library folder.
        var clips = _settings.Current.Clip.ResolvedFolder;
        var root = _settings.Current.Folders.Any(f => Under(Path.Combine(clips, "x"), f)) ? clips
            : _settings.Current.Folders.FirstOrDefault() ?? clips;
        var dir = Path.Combine(root, "Montages");
        var output = Path.Combine(dir, $"Montage {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
        Montage = null;
        Exports.Add(output, P(videos.Count, "Montage of {0} video", "Montage of {0} videos"), async (progress, ct) =>
        {
            Directory.CreateDirectory(dir);
            var parts = await MontageService.PlanAsync(videos, seconds, ct);
            progress.Report(0.05);
            var inner = new Progress<double>(p => progress.Report(0.05 + p * 0.95));
            await MontageService.RenderAsync(new MontageJob(parts, transition, music, output), inner, ct);
        }, () => T("Montage ready"));
        ShowToast(T("Making the montage. It shows up in your library when it's done."));
    }
}
