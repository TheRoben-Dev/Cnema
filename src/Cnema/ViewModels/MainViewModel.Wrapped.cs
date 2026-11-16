using System.Collections.ObjectModel;
using Avalonia.Threading;
using Cnema.Models;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

/// <summary>One of the songs the year's video is cut to.</summary>
public sealed partial class WrappedSongRow : ObservableObject
{
    public string Path { get; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    [ObservableProperty] private string _detail = "";
    public SongInfo? Info { get; set; }

    public WrappedSongRow(string path) => Path = path;
}

// Wrapped: a video of the year (stat cards, then the best moments cut to the beat of the person's own songs),
// made on this computer at low priority. In December it's made by itself and the blob announces it; Year in
// clips holds the songs and makes it any time.
public sealed partial class MainViewModel
{
    public ObservableCollection<WrappedSongRow> WrappedSongs { get; } = new();
    [ObservableProperty] private string _wrappedStatus = "";

    /// <summary>Set by the view: an open dialog for several audio files.</summary>
    public Func<Task<IReadOnlyList<string>>>? PickAudios { get; set; }

    /// <summary>Wrapped is held back until its animated version is done; nothing of it shows before then.</summary>
    internal static bool WrappedReady = Environment.GetEnvironmentVariable("SAVEFILE_WRAPPED") == "1";
    public bool WrappedReadyUi => WrappedReady;
    public bool ShowWrapped => WrappedReady && Features.Wrapped && Features.Recap;
    private int WrappedYear => Recap?.SelectedYear ?? DateTime.Now.Year;
    public string WrappedTitle => F("Your {0} Wrapped", WrappedYear);
    public bool HasWrappedVideo => WrappedVideo(WrappedYear) is not null;
    public bool IsMakingWrapped => WrappedStatus.Length > 0;
    public string MakeWrappedLabel => HasWrappedVideo ? T("Make it again") : T("Make my Wrapped");

    private string? WrappedVideo(int year) =>
        _settings.Current.Wrapped.Made.TryGetValue(year, out var p) && File.Exists(p) ? p : null;

    partial void OnWrappedStatusChanged(string value) => OnPropertyChanged(nameof(IsMakingWrapped));

    /// <summary>What the video will be and how much music it wants, for Year in clips.</summary>
    public string WrappedPlanText
    {
        get
        {
            var year = WrappedYear;
            var clips = WrappedService.YearClips(_items.Values.Select(v => v.Entry), year);
            if (clips.Count < 3) return F("Save a few more clips in {0} and Savefile can make a video of your year.", year);
            var moments = Math.Min(clips.Count, 44);
            var cards = WrappedService.Cards(WrappedStats(clips, year)).Count;
            var length = WrappedDirector.TargetSeconds(moments, cards);
            var music = WrappedDirector.RecommendedMusicSeconds(moments, cards);
            var have = WrappedSongs.Sum(s => s.Info?.Seconds ?? 0);
            var intro = F("About {0} long: stat cards, then your best {1}, cut to the beat.", WrappedService.Span((long)(length * 1000)), P(moments, "{0} moment", "{0} moments"));
            return WrappedSongs.Count == 0
                ? intro + " " + F("Add about {0} of music (MP3 or similar); without songs, the clips keep their own sound.", WrappedService.Span((long)(music * 1000)))
                : have + 5 < length
                    ? intro + " " + F("Your songs cover {0}; add about {1} more, or the video will be as long as the music.", WrappedService.Span((long)(have * 1000)), WrappedService.Span((long)((music - have) * 1000)))
                    : intro + " " + F("Your songs cover {0}: plenty. Savefile uses the parts that fit best.", WrappedService.Span((long)(have * 1000)));
        }
    }

    public string WrappedCostText
    {
        get
        {
            var clips = WrappedService.YearClips(_items.Values.Select(v => v.Entry), DateTime.Now.Year);
            var length = WrappedDirector.TargetSeconds(Math.Min(clips.Count, 44), 6);
            var p = CostMeter.Of(CostMeter.Wrapped);
            var time = CostMeter.SecondsFor(CostMeter.Wrapped, (long)(length * 1000));
            return F("Once a year: {0} · about {1} MB more memory · nothing to download.", T(p.Uses), p.ExtraMemoryMb) + "\n" +
                   F("A {0} video takes {1} on this computer ({2}).", WrappedService.Span((long)(length * 1000)), CostMeter.Span(time),
                       CostMeter.IsMeasured(CostMeter.Wrapped) ? T("timed on this computer") : T("an estimate until it has made one"));
        }
    }

    private void RaiseWrapped()
    {
        OnPropertyChanged(nameof(ShowWrapped));
        OnPropertyChanged(nameof(WrappedTitle));
        OnPropertyChanged(nameof(HasWrappedVideo));
        OnPropertyChanged(nameof(MakeWrappedLabel));
        OnPropertyChanged(nameof(WrappedPlanText));
        OnPropertyChanged(nameof(WrappedCostText));
    }

    private WrappedStats WrappedStats(IReadOnlyList<VideoEntry> clips, int year) =>
        WrappedService.Stats(clips, year, Features.People, e => e.Tags.FirstOrDefault(GameTest(e)));

    // ---------- the songs ----------

    private void LoadWrappedSongs()
    {
        WrappedSongs.Clear();
        foreach (var path in _settings.Current.Wrapped.Songs) WrappedSongs.Add(new WrappedSongRow(path));
        _ = ListenToSongsAsync();
    }

    private bool _listeningToSongs;

    /// <summary>Works out each new song's beats in the background (about a second a song), then updates the advice.</summary>
    private async Task ListenToSongsAsync()
    {
        if (_listeningToSongs) return;
        _listeningToSongs = true;
        try
        {
            while (WrappedSongs.FirstOrDefault(s => s.Info is null && s.Detail.Length == 0) is { } row)
            {
                if (!File.Exists(row.Path)) { row.Detail = T("Missing: the file was moved or deleted"); continue; }
                row.Detail = T("Listening for the beat…");
                try
                {
                    var info = await MusicAnalyzer.GetAsync(row.Path, CancellationToken.None);
                    row.Info = info;
                    row.Detail = info.Usable
                        ? string.Join(" · ", new[] { TrimTime(info.Seconds), F("{0:0} BPM", info.Bpm), info.KeyName }.OfType<string>())
                        : TrimTime(info.Seconds) + " · " + T("no clear beat: it's used as it is");
                }
                catch (Exception ex)
                {
                    Log.Warn($"Song {row.Name}: {ex.Message}");
                    row.Detail = ex.Message;
                }
                OnPropertyChanged(nameof(WrappedPlanText));
            }
        }
        finally { _listeningToSongs = false; }
    }

    private static string TrimTime(double seconds) => Controls.TrimTimeline.FormatTime((long)(seconds * 1000));

    [RelayCommand]
    private async Task AddWrappedSongsAsync()
    {
        if (PickAudios is null) return;
        var picked = await PickAudios();
        var songs = _settings.Current.Wrapped.Songs;
        foreach (var path in picked.Where(p => !songs.Contains(p, StringComparer.Ordinal)))
        {
            songs.Add(path);
            WrappedSongs.Add(new WrappedSongRow(path));
        }
        _settings.Save();
        OnPropertyChanged(nameof(WrappedPlanText));
        _ = ListenToSongsAsync();
    }

    [RelayCommand]
    private void RemoveWrappedSong(WrappedSongRow? row)
    {
        if (row is null) return;
        WrappedSongs.Remove(row);
        _settings.Current.Wrapped.Songs.Remove(row.Path);
        _settings.Save();
        OnPropertyChanged(nameof(WrappedPlanText));
    }

    // ---------- making it ----------

    [RelayCommand] private void MakeWrapped() => MakeWrappedFor(WrappedYear, byItself: false);

    /// <summary>Queues the year's video in the export list. Returns false when there's too little to make one.</summary>
    private bool MakeWrappedFor(int year, bool byItself)
    {
        if (IsMakingWrapped) return true;
        var clips = WrappedService.YearClips(_items.Values.Select(v => v.Entry), year);
        if (clips.Count < 3)
        {
            if (!byItself) ShowToast(F("Save a few more clips in {0} first.", year));
            return false;
        }
        var moments = WrappedService.Moments(clips);
        var cards = WrappedService.Cards(WrappedStats(clips, year));
        var songs = WrappedSongs.Select(s => s.Path).Where(File.Exists).ToList();
        var height = clips.Select(c => c.Height).OrderBy(h => h).ElementAt(clips.Count / 2) >= 1000 ? 1080 : 720;
        var width = height * 16 / 9;

        var clipsDir = _settings.Current.Clip.ResolvedFolder;
        var root = _settings.Current.Folders.Any(f => Under(Path.Combine(clipsDir, "x"), f)) ? clipsDir
            : _settings.Current.Folders.FirstOrDefault() ?? clipsDir;
        var dir = Path.Combine(root, "Wrapped");
        var name = F("Savefile Wrapped {0}", year);
        var output = Path.Combine(dir, name + ".mp4");
        for (var n = 2; File.Exists(output) || Exports.IsTarget(output); n++) output = Path.Combine(dir, $"{name} ({n}).mp4");

        WrappedStatus = T("Getting your Wrapped ready…");
        Exports.Add(output, F("Your {0} Wrapped", year), async (progress, ct) =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var finished = false; // a late progress report must not bring the status back
            var cardDir = Path.Combine(Path.GetTempPath(), "savefile-cards-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                var analysed = new List<SongInfo>();
                foreach (var song in songs)
                {
                    try { analysed.Add(await MusicAnalyzer.GetAsync(song, ct)); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Song {Path.GetFileName(song)} left out: {ex.Message}"); }
                }
                var plan = await Task.Run(() => WrappedDirector.Plan(moments, cards.Select(c => c.MinSeconds).ToList(), analysed), ct);
                Log.Info($"Wrapped {year}: {plan.Seconds:0.0} s, {plan.Shots.Count} shots, {plan.Moments} moments, {plan.Songs.Count} songs");
                var images = await Dispatcher.UIThread.InvokeAsync(() => Views.WrappedCards.Render(cards, cardDir, width, height));
                progress.Report(0.02);
                var inner = new Progress<double>(p =>
                {
                    progress.Report(0.02 + p * 0.98);
                    Dispatcher.UIThread.Post(() => { if (!finished) WrappedStatus = F("Making your Wrapped: {0:0}%", (0.02 + p * 0.98) * 100); });
                });
                await WrappedService.RenderAsync(plan, images, width, height, output, inner, ct);
                CostMeter.Record(CostMeter.Wrapped, watch.Elapsed.TotalSeconds, (long)(plan.Seconds * 1000));
                Dispatcher.UIThread.Post(() =>
                {
                    _settings.Current.Wrapped.Made[year] = output;
                    _settings.Save();
                    RaiseWrapped();
                    RefreshMascot();
                });
            }
            finally
            {
                Dispatcher.UIThread.Post(() => { finished = true; WrappedStatus = ""; });
                try { Directory.Delete(cardDir, true); } catch { /* temp */ }
            }
        }, () => byItself ? F("Your {0} Wrapped is ready", year) : T("Your Wrapped is ready"));
        if (!byItself) ShowToast(T("Making your Wrapped. It shows up in your library when it's done."));
        return true;
    }

    [RelayCommand]
    private async Task WatchWrappedAsync()
    {
        var year = WrappedYear;
        if (WrappedVideo(year) is not { } path) return;
        _settings.Current.Wrapped.Watched = Math.Max(_settings.Current.Wrapped.Watched, year);
        _settings.Save();
        RefreshMascot();
        if (_items.TryGetValue(path, out var item))
        {
            Recap = null;
            await OpenCommand.ExecuteAsync(item);
        }
        else PlatformShell.Reveal(path);
    }

    // ---------- in December, by itself ----------

    private DispatcherTimer? _wrappedTimer;

    /// <summary>Checks now and then whether it's time to make this year's video (from 1 December, once).</summary>
    private void StartWrappedSeason()
    {
        _wrappedTimer ??= new DispatcherTimer(TimeSpan.FromMinutes(20), DispatcherPriority.Background, (_, _) => CheckWrappedSeason());
        _wrappedTimer.Start();
        // A first look a few minutes after start, once the library and the smart passes have settled.
        DispatcherTimer.RunOnce(CheckWrappedSeason, TimeSpan.FromMinutes(3));
    }

    private void CheckWrappedSeason()
    {
        if (!WrappedReady) return;
        var now = DateTime.Now;
        if (!ShowWrapped || now.Month != 12 || WrappedVideo(now.Year) is not null || IsMakingWrapped) return;
        if (SmartShouldWait || Player.Current is not null) return; // not while playing or watching
        if (WrappedService.YearClips(_items.Values.Select(v => v.Entry), now.Year).Count < 8) return;
        Log.Info($"Making the {now.Year} Wrapped by itself");
        MakeWrappedFor(now.Year, byItself: true);
    }

    /// <summary>The blob's news, when this season's video is made and hasn't been watched yet.</summary>
    private string? WrappedNews(DateTime now)
    {
        if (!WrappedReady) return null;
        var year = now.Month == 1 ? now.Year - 1 : now.Year;
        if (!ShowWrapped || now.Month is not (12 or 1) || _settings.Current.Wrapped.Watched >= year || WrappedVideo(year) is null) return null;
        return F("Your {0} Wrapped is ready! Click me to watch it.", year);
    }

    /// <summary>A click on the blob while it announces the year's video plays it.</summary>
    private bool PlayWrappedFromBlob()
    {
        if (WrappedNews(DateTime.Now) is not { } news || MascotLine != news) return false;
        var year = DateTime.Now.Month == 1 ? DateTime.Now.Year - 1 : DateTime.Now.Year;
        if (WrappedVideo(year) is not { } path) return false;
        _settings.Current.Wrapped.Watched = year;
        _settings.Save();
        RefreshMascot();
        if (_items.TryGetValue(path, out var item)) _ = OpenCommand.ExecuteAsync(item);
        return true;
    }
}
