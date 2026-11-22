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
        MontageSource.Custom => _montageCustom ?? (IReadOnlyList<VideoItemViewModel>)Array.Empty<VideoItemViewModel>(),
        _ => _items.Values.Where(v => v.Entry.Favorite).ToList(),
    };

    [RelayCommand]
    private void OpenMontage()
    {
        _montageCustom = null;
        var hasSelection = IsSelecting && Videos.Any(v => v.IsSelected);
        var source = hasSelection ? MontageSource.Selected
            : _items.Values.Any(v => v.Entry.Favorite) ? MontageSource.Favorites : MontageSource.View;
        var fx = _settings.Current.Effects;
        Montage = new MontageViewModel(MontageVideos, source, hasSelection, fx.MusicVolumeDb, fx.MusicDuck);
    }

    /// <summary>The videos behind the montage dialog's first chip when it was opened from Year in clips.</summary>
    private IReadOnlyList<VideoItemViewModel>? _montageCustom;
    private string? _montageCustomName;

    /// <summary>Opens Auto montage with a ready-made list (the year's highlights) as the first choice.</summary>
    private void OpenMontageWith(IReadOnlyList<VideoItemViewModel> videos, string label)
    {
        _montageCustom = videos;
        _montageCustomName = label;
        var fx = _settings.Current.Effects;
        Montage = new MontageViewModel(MontageVideos, MontageSource.Custom, false, fx.MusicVolumeDb, fx.MusicDuck, label);
        Montage.Order.Value = 0;
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
        var picked = m.Videos();
        var videos = picked.Select(v => (v.Entry.Path, v.Entry.DurationMs)).ToList();
        // What the smart passes know about each clip, read now on this thread (the library can change meanwhile).
        var guides = picked.Select(v => (v.Entry.Path, G: CutGuide.For(v.Entry))).Where(x => x.G is not null)
            .GroupBy(x => x.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().G, StringComparer.Ordinal);
        var seconds = (int)m.Length.Value;
        var transition = (MontageTransition)m.Transition.Value;
        var music = m.HasMusic ? new MusicSpec(m.MusicPath!, (double)m.MusicVolume.Value, m.MusicDuck) : null;
        var fx = _settings.Current.Effects;
        fx.MusicVolumeDb = (double)m.MusicVolume.Value;
        fx.MusicDuck = m.MusicDuck;
        _settings.Save();

        // Next to Cnema's own clips when the library shows them, otherwise in the first library folder.
        var clips = _settings.Current.Clip.ResolvedFolder;
        var root = _settings.Current.UsableExportFolder()
            ?? (_settings.Current.Folders.Any(f => Under(Path.Combine(clips, "x"), f)) ? clips : _settings.Current.Folders.FirstOrDefault() ?? clips);
        var dir = Path.Combine(root, "Montages");
        var name = (MontageSource)m.Source.Value == MontageSource.Custom && _montageCustomName is { } custom
            ? $"{string.Concat(custom.Split(Path.GetInvalidFileNameChars()))} {DateTime.Now:yyyy-MM-dd HH-mm-ss}"
            : $"Montage {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
        var output = Path.Combine(dir, name + ".mp4");
        // Never replace an earlier montage, or one still being made.
        for (var n = 2; File.Exists(output) || Exports.IsTarget(output); n++) output = Path.Combine(dir, $"{name} ({n}).mp4");
        Montage = null;
        Exports.Add(output, P(videos.Count, "Montage of {0} video", "Montage of {0} videos"), async (progress, ct) =>
        {
            Directory.CreateDirectory(dir);
            // The song's beats (analysed once and remembered), so cuts and the picture move with it.
            SongInfo? song = null;
            if (music is not null && PulseFx.Enabled)
            {
                try { song = await MusicAnalyzer.GetAsync(music.Path, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Montage: couldn't read the beats of {music.Path}: {ex.Message}"); }
            }
            var parts = await MontageService.PlanAsync(videos, seconds, ct, p => guides.GetValueOrDefault(p), song, transition);
            progress.Report(0.05);
            var inner = new Progress<double>(p => progress.Report(0.05 + p * 0.95));
            await MontageService.RenderAsync(new MontageJob(parts, transition, music, output) { Song = song }, inner, ct);
        }, () => T("Montage ready"));
        ShowToast(T("Making the montage. It shows up in your library when it's done."));
    }
}
