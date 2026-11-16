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
    internal static bool WrappedReady = Environment.GetEnvironmentVariable("SAVEFILE_WRAPPED") != "0";
    public bool WrappedReadyUi => WrappedReady;
    public bool ShowWrapped => WrappedReady && Features.Wrapped && Features.Recap;
    private int WrappedYear => Recap?.SelectedYear ?? DateTime.Now.Year;
    public string WrappedTitle => F("Your {0} Wrapped", WrappedYear);
    public bool HasWrappedVideo => WrappedVideo(WrappedYear) is not null;
    public bool IsMakingWrapped => WrappedStatus.Length > 0;
    public string MakeWrappedLabel => HasWrappedVideo ? T("Make it again") : T("Make my Wrapped");

    private string? WrappedVideo(int year) =>
        _settings.Current.Wrapped.Made.TryGetValue(year, out var p) && File.Exists(p) ? p : null;

    partial void OnWrappedStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsMakingWrapped));
        OnPropertyChanged(nameof(GiftTip));
        if (value.Length == 0) WrappedProgress = -1;
    }

    /// <summary>What the video will be and how much music it wants, for Year in clips.</summary>
    public string WrappedPlanText
    {
        get
        {
            var year = WrappedYear;
            var clips = WrappedService.YearClips(_items.Values.Select(v => v.Entry), year);
            if (clips.Count < 3) return F("Save a few more clips in {0} and Savefile can make a video of your year.", year);
            var length = WrappedScriptFor(clips, year).TargetSeconds;
            var music = WrappedDirector.RecommendedMusicSeconds(length);
            var have = WrappedSongs.Sum(s => s.Info?.Seconds ?? 0);
            var intro = F("About {0} long: your year in chapters, cut to the beat.", WrappedService.Span((long)(length * 1000)));
            return WrappedSongs.Count == 0
                ? intro + " " + F("Add about {0} of music (MP3 or similar), two or three songs you love; without songs, the clips keep their own sound.", WrappedService.Span((long)(music * 1000)))
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
            var length = clips.Count < 3 ? WrappedDirector.MinSeconds : WrappedScriptFor(clips, DateTime.Now.Year).TargetSeconds;
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
        OnPropertyChanged(nameof(ShowFriendWrapped));
        OnPropertyChanged(nameof(WrappedTitle));
        OnPropertyChanged(nameof(HasWrappedVideo));
        OnPropertyChanged(nameof(MakeWrappedLabel));
        OnPropertyChanged(nameof(WrappedPlanText));
        OnPropertyChanged(nameof(WrappedCostText));
        RaiseGift();
    }


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

    private WrappedScript WrappedScriptFor(IReadOnlyList<VideoEntry> clips, int year, string? friend = null) =>
        WrappedService.Script(clips, year, Features.People, e => e.Tags.FirstOrDefault(GameTest(e)), friend);

    /// <summary>On a friend's page: a shorter Wrapped of only your clips together this year.</summary>
    public bool ShowFriendWrapped => ShowWrapped && Features.People && ActiveNav is { Kind: NavKind.Person } friend && !IsSelecting &&
        FriendYearClips(friend.Name, DateTime.Now.Year).Count >= 3;

    public string FriendWrappedLabel => ActiveNav is { Kind: NavKind.Person } f ? F("Wrapped with {0}", f.Name) : "";

    private List<VideoEntry> FriendYearClips(string friend, int year) =>
        WrappedService.YearClips(_items.Values.Select(v => v.Entry), year)
            .Where(e => e.People.Any(p => string.Equals(p.Trim(), friend, StringComparison.OrdinalIgnoreCase))).ToList();

    [RelayCommand]
    private void MakeFriendWrapped()
    {
        if (ActiveNav is not { Kind: NavKind.Person } friend) return;
        MakeWrappedFor(DateTime.Now.Year, byItself: false, friend.Name);
    }

    /// <summary>Queues the year's video in the export list. Returns false when there's too little to make one.</summary>
    private bool MakeWrappedFor(int year, bool byItself, string? friend = null)
    {
        if (IsMakingWrapped)
        {
            if (!byItself) ShowToast(T("A Wrapped is already being made. This one can start when it's done."));
            return IsMakingWrapped && friend is null;
        }
        var clips = friend is null ? WrappedService.YearClips(_items.Values.Select(v => v.Entry), year) : FriendYearClips(friend, year);
        if (clips.Count < 3)
        {
            if (!byItself) ShowToast(F("Save a few more clips in {0} first.", year));
            return false;
        }
        var height = clips.Select(c => c.Height).OrderBy(h => h).ElementAt(clips.Count / 2) >= 1000 ? 1080 : 720;
        var clipsDir = _settings.Current.Clip.ResolvedFolder;
        var root = _settings.Current.Folders.Any(f => Under(Path.Combine(clipsDir, "x"), f)) ? clipsDir
            : _settings.Current.Folders.FirstOrDefault() ?? clipsDir;
        var dir = Path.Combine(root, "Wrapped");
        var name = friend is null ? F("Savefile Wrapped {0}", year) : F("Savefile Wrapped {0} with {1}", year, Sanitize(friend));
        var output = Path.Combine(dir, name + ".mp4");
        for (var n = 2; File.Exists(output) || Exports.IsTarget(output); n++) output = Path.Combine(dir, $"{name} ({n}).mp4");
        var work = Path.Combine(AppPaths.DataDir, "wrapped", $"{year}-{DateTime.UtcNow:yyyyMMddHHmmss}");
        var job = new WrappedJob { Year = year, Output = output, Work = work, Width = height * 16 / 9, Height = height, Friend = friend };
        _settings.Current.Wrapped.Job = job;
        _settings.Save();
        QueueWrapped(job, WrappedScriptFor(clips, year, friend), WrappedSongs.Select(s => s.Path).Where(File.Exists).ToList(), byItself);
        if (!byItself) ShowToast(T("Making your Wrapped. It shows up in your library when it's done."));
        return true;
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch is '/' or '\\' or ':' ? '_' : ch)).Trim();

    /// <summary>A video that was being made when Savefile last quit carries on from the parts already done.</summary>
    private void ResumeWrapped()
    {
        if (!WrappedReady || _settings.Current.Wrapped.Job is not { } job || IsMakingWrapped) return;
        if (!File.Exists(Path.Combine(job.Work, "plan.json")))
        {
            _settings.Current.Wrapped.Job = null;
            _settings.Save();
            return;
        }
        Log.Info($"Wrapped {job.Year}: picking up where it stopped");
        QueueWrapped(job, null, null, byItself: true);
    }

    private sealed record WrappedSaved(WrappedPlan Plan, List<WrappedCard> Cards);

    private void QueueWrapped(WrappedJob job, WrappedScript? script, List<string>? songs, bool byItself)
    {
        WrappedStatus = T("Getting your Wrapped ready…");
        WrappedProgress = 0;
        var year = job.Year;
        Exports.Add(job.Output, job.Friend is null ? F("Your {0} Wrapped", year) : F("Your {0} Wrapped with {1}", year, job.Friend), async (progress, ct) =>
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var finished = false; // a late progress report must not bring the status back
            var planFile = Path.Combine(job.Work, "plan.json");
            try
            {
                Directory.CreateDirectory(job.Work);
                WrappedSaved saved;
                if (script is null)
                {
                    saved = System.Text.Json.JsonSerializer.Deserialize<WrappedSaved>(await File.ReadAllTextAsync(planFile, ct))
                            ?? throw new InvalidOperationException("The saved plan is unreadable.");
                }
                else
                {
                    var analysed = new List<SongInfo>();
                    foreach (var song in songs ?? new())
                    {
                        try { analysed.Add(await MusicAnalyzer.GetAsync(song, ct)); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Warn($"Song {Path.GetFileName(song)} left out: {ex.Message}"); }
                    }
                    var plan = await Task.Run(() => WrappedDirector.Plan(script.Acts, script.Cards.Select(c => c.MinSeconds).ToList(), analysed), ct);
                    saved = new WrappedSaved(plan, script.Cards.ToList());
                    await File.WriteAllTextAsync(planFile, System.Text.Json.JsonSerializer.Serialize(saved), ct);
                }
                Log.Info($"Wrapped {year}: {saved.Plan.Seconds:0.0} s, {saved.Plan.Shots.Count} shots, {saved.Plan.Moments} moments, {saved.Plan.Songs.Count} songs");
                var inner = new Progress<double>(p =>
                {
                    progress.Report(p);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (finished) return;
                        WrappedStatus = F("Making your Wrapped: {0:0}%", p * 100);
                        WrappedProgress = p;
                    });
                });
                await WrappedService.RenderAsync(saved.Plan, job.Work, job.Width, job.Height, job.Output,
                    (card, seconds, beats, downs, path, c) => Views.WrappedMotion.RenderAsync(saved.Cards[card], seconds, beats, downs, job.Width, job.Height, WrappedService.Fps, path, c),
                    (caption, path, c) => Dispatcher.UIThread.InvokeAsync(() => Views.WrappedMotion.RenderCaption(caption, job.Width, job.Height, path)).GetTask(),
                    inner, ct);
                CostMeter.Record(CostMeter.Wrapped, watch.Elapsed.TotalSeconds, (long)(saved.Plan.Seconds * 1000));
                try { Directory.Delete(job.Work, true); } catch { /* left for the next clean-up */ }
                Dispatcher.UIThread.Post(() =>
                {
                    if (job.Friend is null) _settings.Current.Wrapped.Made[year] = job.Output;
                    if (_settings.Current.Wrapped.Job?.Work == job.Work) _settings.Current.Wrapped.Job = null;
                    _settings.Save();
                    RaiseWrapped();
                    RefreshMascot();
                });
            }
            catch (OperationCanceledException) when (!_quitting)
            {
                // Cancelled by hand: start over next time.
                Dispatcher.UIThread.Post(() =>
                {
                    if (_settings.Current.Wrapped.Job?.Work == job.Work) { _settings.Current.Wrapped.Job = null; _settings.Save(); }
                });
                try { Directory.Delete(job.Work, true); } catch { /* left for the next clean-up */ }
                throw;
            }
            finally
            {
                Dispatcher.UIThread.Post(() => { finished = true; WrappedStatus = ""; });
            }
        }, () => byItself ? F("Your {0} Wrapped is ready", year) : T("Your Wrapped is ready"));
    }

    [RelayCommand]
    private async Task WatchWrappedAsync()
    {
        var year = WrappedYear;
        if (WrappedVideo(year) is not { } path) return;
        await OpenTheatreAsync(path, first: _settings.Current.Wrapped.Watched < year);
    }

    // ---------- the present (1 November to 31 December) ----------

    private DispatcherTimer? _wrappedTimer;
    private string? _giftPictureFor;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _wrappedPicture;
    [ObservableProperty] private double _wrappedProgress = -1;
    [ObservableProperty] private bool _isWrappedGiftOpen;

    /// <summary>The present shows from 1 November to the end of the year (SAVEFILE_SEASON=1 shows it any time, for testing).</summary>
    private static bool InWrappedSeason(DateTime now) =>
        now.Month is 11 or 12 || Environment.GetEnvironmentVariable("SAVEFILE_SEASON") == "1";

    /// <summary>The present is offered once a season; after that it stays, opened.</summary>
    public bool GiftOpened => _settings.Current.Wrapped.GiftOffered >= DateTime.Now.Year;

    public bool ShowWrappedGift
    {
        get
        {
            var now = DateTime.Now;
            if (!ShowWrapped || !ShowMascot || !InWrappedSeason(now)) return false;
            return GiftOpened || WrappedService.YearClips(_items.Values.Select(v => v.Entry), now.Year).Count >= 3;
        }
    }

    public string GiftTip => !GiftOpened ? T("A present from Mochi. Click to open it.")
        : IsMakingWrapped ? WrappedStatus
        : WrappedVideo(DateTime.Now.Year) is not null ? T("Watch your Wrapped again, or save a copy")
        : T("Make your Wrapped");

    public string GiftTitle => F("Your {0} Wrapped", DateTime.Now.Year);

    private void RaiseGift()
    {
        OnPropertyChanged(nameof(ShowWrappedGift));
        OnPropertyChanged(nameof(GiftOpened));
        OnPropertyChanged(nameof(GiftTip));
        OnPropertyChanged(nameof(GiftTitle));
        LoadGiftPicture();
    }

    /// <summary>A frame of the finished video for the card in the open box.</summary>
    private void LoadGiftPicture()
    {
        var path = WrappedVideo(DateTime.Now.Year);
        var thumb = path is not null && _items.TryGetValue(path, out var item) ? item.Entry.ThumbPath : null;
        if (thumb == _giftPictureFor) return;
        _giftPictureFor = thumb;
        var old = WrappedPicture;
        WrappedPicture = null;
        old?.Dispose();
        if (thumb is null || !File.Exists(thumb)) return;
        _ = Task.Run(() =>
        {
            try
            {
                using var fs = File.OpenRead(thumb);
                return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(fs, 240);
            }
            catch { return null; }
        }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
        {
            if (_giftPictureFor == thumb) WrappedPicture = t.Result;
            else t.Result?.Dispose();
        }));
    }

    private void StartWrappedSeason()
    {
        // Looks again now and then, so the present turns up on 1 November while Savefile is open.
        _wrappedTimer ??= new DispatcherTimer(TimeSpan.FromMinutes(20), DispatcherPriority.Background, (_, _) => CheckWrappedSeason());
        _wrappedTimer.Start();
        CheckWrappedSeason();
    }

    private void CheckWrappedSeason()
    {
        if (!WrappedReady) return;
        RaiseGift();
        RefreshMascot();
    }

    /// <summary>A click on the present: the first time it unwraps and explains; then it plays the video.</summary>
    [RelayCommand]
    private async Task OpenGiftAsync()
    {
        var year = DateTime.Now.Year;
        if (!GiftOpened)
        {
            _settings.Current.Wrapped.GiftOffered = year;
            _settings.Save();
            RaiseGift();
            RefreshMascot();
            await Task.Delay(ReducedMotion ? 0 : 650); // let the lid come off first
            IsWrappedGiftOpen = true;
            return;
        }
        if (WrappedVideo(year) is { } path) { await OpenTheatreAsync(path, first: _settings.Current.Wrapped.Watched < year); return; }
        if (IsMakingWrapped) { ShowToast(WrappedStatus); return; }
        IsWrappedGiftOpen = true;
    }

    [RelayCommand] private void CloseWrappedGift() => IsWrappedGiftOpen = false;

    [RelayCommand]
    private void MakeWrappedFromGift()
    {
        if (MakeWrappedFor(DateTime.Now.Year, byItself: false)) IsWrappedGiftOpen = false;
    }

    /// <summary>The blob's line about the present, or about the finished video nobody has watched yet.</summary>
    private string? WrappedNews(DateTime now)
    {
        if (!WrappedReady || !ShowWrapped) return null;
        if (ShowWrappedGift && !GiftOpened) return T("I wrapped something for you. Open the present!");
        var year = now.Month == 1 ? now.Year - 1 : now.Year;
        if (now.Month is not (11 or 12 or 1) || _settings.Current.Wrapped.Watched >= year || WrappedVideo(year) is null) return null;
        return F("Your {0} Wrapped is ready! Click me to watch it.", year);
    }

    /// <summary>A click on the blob while it talks about the present opens it, or plays the finished video.</summary>
    private bool PlayWrappedFromBlob()
    {
        if (WrappedNews(DateTime.Now) is not { } news || MascotLine != news) return false;
        if (!GiftOpened && ShowWrappedGift) { _ = OpenGiftAsync(); return true; }
        var year = DateTime.Now.Month == 1 ? DateTime.Now.Year - 1 : DateTime.Now.Year;
        if (WrappedVideo(year) is not { } path) return false;
        _ = OpenTheatreAsync(path, first: true);
        return true;
    }


    // ---------- watching it: a plain screen with pause and scrubbing ----------

    [ObservableProperty] private bool _isTheatreOpen;
    /// <summary>The first viewing fills the screen; later ones play in the window with Save a copy.</summary>
    [ObservableProperty] private bool _theatreFirst;
    private string? _theatrePath;

    private async Task OpenTheatreAsync(string path, bool first)
    {
        if (!_items.TryGetValue(path, out var item)) { PlatformShell.Reveal(path); return; }
        var year = _settings.Current.Wrapped.Made.FirstOrDefault(kv => kv.Value == path).Key;
        if (year > 0 && _settings.Current.Wrapped.Watched < year)
        {
            _settings.Current.Wrapped.Watched = year;
            _settings.Save();
        }
        IsWrappedGiftOpen = false;
        Recap = null;
        if (IsMiniPlayerOpen) CloseMiniPlayer();
        if (IsPlayerOpen) ClosePlayer();
        PlayAll = false;
        _theatrePath = path;
        TheatreFirst = first;
        IsTheatreOpen = true;
        RefreshMascot();
        if (first) Player.StartAt = 0; // from the very start, not where it was left
        await Player.OpenAsync(item);
    }

    [RelayCommand]
    private void CloseTheatre()
    {
        if (!IsTheatreOpen) return;
        Player.Close();
        IsTheatreOpen = false;
        TheatreFirst = false;
        _library.SaveCache();
    }

    [RelayCommand]
    private async Task SaveTheatreCopyAsync()
    {
        if (_theatrePath is not { } path || !File.Exists(path) || Player.PickSavePath is null) return;
        var start = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(start)) start = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var target = await Player.PickSavePath(Path.GetFileName(path), start);
        if (target is null || string.Equals(Path.GetFullPath(target), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            Player.ShowToast(T("Saving a copy…"));
            await Task.Run(() => File.Copy(path, target, overwrite: true));
            Player.ShowToast(F("Saved a copy to {0}", Path.GetDirectoryName(target) ?? target));
        }
        catch (Exception ex)
        {
            Log.Warn($"Saving a copy of the Wrapped: {ex.Message}");
            Player.ShowToast(F("Couldn't save the copy: {0}", ex.Message));
        }
    }
}
