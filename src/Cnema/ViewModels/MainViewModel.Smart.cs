using Avalonia.Threading;
using Cnema.Models;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// Smart features that need no download: best moments (sound and picture) and smart titles.
public sealed partial class MainViewModel
{
    private CancellationTokenSource? _smartCts;
    private Task? _smartRun;

    /// <summary>"Finding best moments, 12 of 300" while the background pass runs (Settings shows it).</summary>
    [ObservableProperty] private string _smartActivity = "";

    public bool ShowSmartTitles => Features.SmartTitles;

    // ---------- games, for titles ----------

    private static readonly HashSet<string> WellKnownGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "valorant", "counter-strike 2", "cs2", "counter-strike", "fortnite", "apex legends", "overwatch", "overwatch 2",
        "minecraft", "rocket league", "league of legends", "dota 2", "destiny 2", "warframe", "honkai: star rail",
        "honkai star rail", "genshin impact", "no man's sky", "ark", "ark: survival evolved", "ark: survival ascended",
        "call of duty", "warzone", "rainbow six siege", "pubg", "gta v", "gta 5", "grand theft auto v", "elden ring",
        "it takes two", "split fiction", "lethal company", "phasmophobia", "among us", "fall guys", "deep rock galactic",
        "helldivers 2", "sea of thieves", "rust", "dead by daylight", "the finals", "marvel rivals", "baldur's gate 3",
        "terraria", "stardew valley", "roblox", "palworld", "valheim", "satisfactory", "factorio", "hades", "hades ii",
        "street fighter 6", "tekken 8", "smash", "super smash bros", "mario kart", "mario kart 8", "zelda", "cyberpunk 2077",
        "battlefield", "battlefield 6", "the witcher 3", "red dead redemption 2", "titanfall 2", "halo infinite",
        "escape from tarkov", "monster hunter", "monster hunter wilds", "path of exile", "path of exile 2", "diablo iv",
        "world of warcraft", "final fantasy xiv", "starfield", "skyrim", "zenless zone zero", "wuthering waves",
    };

    /// <summary>A tag that names a game: one Savefile or a recorder put on, a game profile, or a well-known game.</summary>
    private Func<string, bool> GameTest(VideoEntry e)
    {
        var recorder = GameTagFor(e.Path);
        var known = _settings.Current.GameTags;
        var profiles = _settings.Current.Clip.Profiles;
        return tag => WellKnownGames.Contains(tag)
            || (recorder is not null && string.Equals(recorder, tag, StringComparison.OrdinalIgnoreCase))
            || known.Contains(tag, StringComparer.OrdinalIgnoreCase)
            || profiles.Any(p => string.Equals(p.Game.Trim(), tag, StringComparison.OrdinalIgnoreCase));
    }

    private void RememberGame(string game)
    {
        if (_settings.Current.GameTags.Contains(game, StringComparer.OrdinalIgnoreCase)) return;
        _settings.Current.GameTags.Add(game);
        _settings.Save();
    }

    public string? SuggestTitle(VideoEntry e, bool withTime = false) =>
        Features.SmartTitles ? SmartTitles.Suggest(e, GameTest(e), withTime) : null;

    // ---------- best moments, in the background ----------

    /// <summary>Looks at every video not looked at yet, newest first, one at a time at idle priority.</summary>
    private void StartSmartPass()
    {
        if (!Features.Highlights) return;
        if (_smartRun is { IsCompleted: false }) return;
        if (ToolLocator.Ffmpeg is null) return;
        _smartCts = new CancellationTokenSource();
        var ct = _smartCts.Token;
        _smartRun = RunSmartPassAsync(ct);
    }

    private void StopSmartPass()
    {
        _smartCts?.Cancel();
        SmartActivity = "";
    }

    /// <summary>Gaming (a game profile is on), recording a session or exporting: wait, so the game gets the computer.</summary>
    private bool SmartShouldWait => IsIndexPaused || Clips.ActiveGame is not null || Clips.IsSession || Exports.IsBusy || Player.IsExporting || IsScanning;

    /// <summary>What the smart features are waiting for, said in Settings.</summary>
    private string WaitReason =>
        IsIndexPaused ? T("Paused for now.")
        : Clips.IsSession ? T("Paused while a session records.")
        : Clips.ActiveGame is not null ? T("Paused while you play.")
        : Exports.IsBusy || Player.IsExporting ? T("Paused until the export is done.")
        : IsScanning ? T("Paused while the library is scanned.")
        : "";

    /// <summary>
    /// The smart features take turns, one video at a time, so only one of them works at once and each makes
    /// progress (before, each waited for the one before it to finish the whole library, which on a big library
    /// looked like nothing was happening).
    /// </summary>
    private readonly SemaphoreSlim _workGate = new(1, 1);

    /// <summary>Waits until the computer is free and it's this feature's turn, saying so; release <see cref="_workGate"/> after.</summary>
    private async Task<TimeSpan> TakeTurnAsync(Action<string> show, string working, string feature, CancellationToken ct)
    {
        var since = DateTime.UtcNow;
        var logged = false;
        while (SmartShouldWait && !ct.IsCancellationRequested)
        {
            show(working + " " + WaitReason);
            if (!logged && DateTime.UtcNow - since > TimeSpan.FromMinutes(2))
            {
                logged = true;
                Log.Info($"{feature} paused: game={Clips.ActiveGame is not null} session={Clips.IsSession} export={Exports.IsBusy || Player.IsExporting} scan={IsScanning}");
            }
            await Task.Delay(5000, ct);
        }
        show(working);
        await _workGate.WaitAsync(ct);
        return DateTime.UtcNow - since;
    }

    /// <summary>
    /// How long one video may take before it's skipped (a file ffmpeg chokes on must never hold up the
    /// rest): three minutes plus twice its length.
    /// </summary>
    internal static TimeSpan ItemLimit(long durationMs) => TimeSpan.FromMinutes(3) + TimeSpan.FromMilliseconds(Math.Max(0, durationMs) * 2);

    /// <summary>A pass's pace in the log: slow videos one by one, and a summary every 10, so a stall shows.</summary>
    private sealed class PassLog(string feature)
    {
        private int _n;
        private double _work, _wait;

        public void Add(string file, TimeSpan waited, TimeSpan work)
        {
            _n++;
            _work += work.TotalSeconds;
            _wait += waited.TotalSeconds;
            if (work.TotalSeconds >= 60 || waited.TotalSeconds >= 120)
                Log.Info($"{feature}: {file} took {work.TotalSeconds:0.0} s (waited {waited.TotalSeconds:0} s for its turn)");
            if (_n % 10 == 0)
                Log.Info($"{feature}: {_n} done this run, {_work / _n:0.0} s each on average, {_wait / _n:0.0} s waiting");
        }
    }

    /// <summary>Why a video was skipped, for the log: a time limit or the error.</summary>
    private static string SkipReason(Exception ex, long durationMs) =>
        ex is OperationCanceledException ? $"took longer than {ItemLimit(durationMs).TotalMinutes:0} min, skipped" : ex.Message;

    /// <summary>Loads a model off the UI thread, giving up after two minutes with a message fit to show.</summary>
    private static async Task<T> LoadModelAsync<T>(string feature, Func<T> load, CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var model = await Task.Run(load, ct).WaitAsync(TimeSpan.FromMinutes(2), ct);
            Log.Info($"{feature} model loaded in {watch.ElapsedMilliseconds} ms");
            return model;
        }
        catch (TimeoutException)
        {
            Log.Warn($"{feature} model didn't load within 2 minutes");
            throw new InvalidOperationException(F("The {0} model is taking too long to start. Restart Savefile to try again.", feature.ToLowerInvariant()));
        }
    }

    /// <summary>Videos each feature couldn't read this session (shown in Settings and in diagnostics).</summary>
    private readonly Dictionary<string, int> _workFailed = new();

    private void CountFailure(string feature)
    {
        lock (_workFailed) _workFailed[feature] = _workFailed.GetValueOrDefault(feature) + 1;
    }

    private string FailedText(string feature)
    {
        int n;
        lock (_workFailed) n = _workFailed.GetValueOrDefault(feature);
        return n == 0 ? "" : " " + P(n, "{0} video couldn't be read (see the log).", "{0} videos couldn't be read (see the log).");
    }

    private async Task RunSmartPassAsync(CancellationToken ct)
    {
        var done = 0;
        try
        {
            var since = DateTime.UtcNow;
            var tried = new HashSet<VideoItemViewModel>();
            var pace = new PassLog("Best moments");
            using var awake = PlatformShell.StayAwake("Finding best moments");
            Log.Info($"Best moments pass: {_items.Values.Count(v => v.Entry.Smart is not { Version: SmartAnalyzer.Version })} to do");
            while (!ct.IsCancellationRequested && Features.Highlights)
            {
                // Newest first; videos added meanwhile are picked up by the next round.
                var todo = _items.Values.Where(v => v.Entry.Smart is not { Version: SmartAnalyzer.Version } && !tried.Contains(v))
                    .OrderByDescending(v => v.Entry.ModifiedUtc).ToList();
                if (todo.Count == 0) break;
                for (var i = 0; i < todo.Count && !ct.IsCancellationRequested && Features.Highlights; i++)
                {
                    var item = todo[i];
                    tried.Add(item);
                    if (item.Entry.Smart is { Version: SmartAnalyzer.Version } || !File.Exists(item.Entry.Path)) continue; // unplugged drive
                    var waited = await TakeTurnAsync(s => SmartActivity = s, F("Finding best moments: {0} to go, {1}.", (todo.Count - i).ToString("N0"), CostMeter.Span(LeftSeconds(CostMeter.Moments, todo.Skip(i)))), "Best moments", ct);
                    SmartInfo? info;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    limit.CancelAfter(ItemLimit(item.Entry.DurationMs));
                    try
                    {
                        info = await SmartAnalyzer.AnalyzeAsync(item.Entry.Path, item.Entry.DurationMs, limit.Token);
                        CostMeter.Record(CostMeter.Moments, watch.Elapsed.TotalSeconds, item.Entry.DurationMs);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        Log.Warn($"Best moments for {item.Entry.FileName}: {SkipReason(ex, item.Entry.DurationMs)}");
                        CountFailure("Best moments");
                        info = null;
                    }
                    finally { _workGate.Release(); pace.Add(item.Entry.FileName, waited, watch.Elapsed); }
                    // The item may have been renamed, moved or deleted meanwhile.
                    if (_items.TryGetValue(item.Entry.Path, out var still) && ReferenceEquals(still, item))
                    {
                        item.Entry.Smart = info ?? new SmartInfo { Version = SmartAnalyzer.Version };
                        if (info is { Excitement: >= 0.8 }) IndexCheer++; // a really good one: the blob hops
                        AutoTitle(item);
                        if (ReferenceEquals(Player.Current, item)) Player.RefreshTrimSuggestion();
                        if (Features.SmartThumbs && item.Entry.Smart.ThumbMs > 0 && item.Entry.ThumbAtMs == 0)
                            await RefreshSmartThumbsAsync(new[] { item }, ct);
                    }
                    if (++done % 10 == 0) { _library.SaveCache(); UpdateHighlightMarks(); OnCostsChanged(); }
                    if (DateTime.UtcNow - since > TimeSpan.FromSeconds(30) && SortIndex == 5) { since = DateTime.UtcNow; RefreshView(); }
                    // Breathe between videos: this is background work.
                    await Task.Delay(150, ct);
                }
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (Exception ex) { Log.Warn($"Best moments pass stopped: {ex.Message}"); }
        finally
        {
            Log.Info($"Best moments pass ended: {done} done");
            SmartActivity = "";
            OnCostsChanged();
            _library.SaveCache();
            UpdateHighlightMarks();
            if (done > 0) RefreshMemories();
            if (SortIndex == 5) RefreshView();
        }
    }

    /// <summary>
    /// Makes the thumbnails of these videos follow the smart pick (or Savefile's usual frame when Smart
    /// thumbnails is off). A frame the person picked is never touched. One small ffmpeg grab each, at idle.
    /// </summary>
    private async Task RefreshSmartThumbsAsync(IReadOnlyCollection<VideoItemViewModel> items, CancellationToken ct = default)
    {
        foreach (var item in items)
        {
            if (ct.IsCancellationRequested) return;
            var e = item.Entry;
            if (e.ThumbAtMs > 0 || e.Smart is not { ThumbMs: > 0 }) continue;
            var want = ThumbnailService.PathFor(e);
            if (string.Equals(e.ThumbPath, want, StringComparison.Ordinal) && File.Exists(want)) continue;
            try
            {
                var path = await ThumbnailService.EnsureAsync(e, ct);
                if (path is null) continue;
                e.ThumbPath = path;
                item.ReloadThumbnail();
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log.Warn($"Thumbnail for {e.FileName}: {ex.Message}"); }
        }
        if (items.Count > 1) _library.SaveCache();
    }

    /// <summary>The top tenth of the library (and clearly eventful) get a Highlight mark on their card.</summary>
    private void UpdateHighlightMarks()
    {
        var scored = _items.Values.Where(v => v.Entry.Smart is { Version: > 0 }).Select(v => v.Entry.Smart!.Excitement).OrderByDescending(x => x).ToList();
        var threshold = scored.Count < 10 ? double.MaxValue : Math.Max(0.45, scored[Math.Max(0, scored.Count / 10 - 1)]);
        VideoItemViewModel.HighlightThreshold = Features.Highlights ? threshold : double.MaxValue;
        foreach (var v in _items.Values) v.RefreshHighlight();
    }

    /// <summary>A new clip with a made-up name gets a smart title (Settings, Features, Name new clips by themselves).</summary>
    private void AutoTitle(VideoItemViewModel item)
    {
        var e = item.Entry;
        if (!Features.AutoTitles || !Features.SmartTitles || e.Title is not null) return;
        if (e.ModifiedUtc < _settings.Current.AutoTitlesSinceUtc || !SmartTitles.IsGeneric(e.FileName)) return;
        if (SuggestTitle(e) is not { } title) return;
        e.Title = title;
        item.RefreshAll();
        _library.SaveCache();
    }

    // ---------- smart rename ----------

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsShowingRename))] private SmartRenameViewModel? _rename;
    public bool IsShowingRename => Rename is not null;

    [RelayCommand]
    private void OpenSmartRename()
    {
        if (Cleanup is not null) CloseCleanup();
        var rows = new List<SmartRenameRow>();
        // Several clips of one game with the same people on one day: tell them apart by the time.
        var plain = _items.Values.Select(v => (v, s: SuggestTitle(v.Entry))).Where(x => x.s is not null).ToList();
        var twice = plain.GroupBy(x => x.s!).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        foreach (var (v, s) in plain.OrderByDescending(x => x.v.Entry.ModifiedUtc))
        {
            var title = twice.Contains(s!) ? SuggestTitle(v.Entry, withTime: true)! : s!;
            if (title == v.Title) continue;
            rows.Add(new SmartRenameRow(v, title) { IsChecked = SmartTitles.IsGeneric(v.Entry.FileName) && v.Entry.Title is null });
        }
        Rename = new SmartRenameViewModel(rows, File.Exists(RenameUndoFile));
    }

    [RelayCommand] private void CloseSmartRename() { if (Rename is not { IsBusy: true }) Rename = null; }

    private static string RenameUndoFile => Path.Combine(AppPaths.DataDir, "rename-undo.json");

    private sealed class RenameUndo
    {
        /// <summary>Path now, path before, title before.</summary>
        public List<string?[]> Items { get; set; } = new();
    }

    [RelayCommand]
    private async Task ApplySmartRenameAsync()
    {
        if (Rename is not { IsBusy: false } r) return;
        var chosen = r.Rows.Where(x => x.IsChecked && x.NewTitle.Trim().Length > 0).ToList();
        if (chosen.Count == 0) { r.Status = T("Tick the videos to rename first."); return; }
        r.IsBusy = true;
        var undo = new RenameUndo();
        var failed = 0;
        foreach (var row in chosen)
        {
            var item = row.Item;
            var e = item.Entry;
            var oldPath = e.Path;
            var oldTitle = e.Title;
            var title = row.NewTitle.Trim();
            if (r.RenameFiles)
            {
                try
                {
                    var name = FileSafe(title);
                    var dir = Path.GetDirectoryName(oldPath)!;
                    var ext = Path.GetExtension(oldPath);
                    for (var n = 2; File.Exists(Path.Combine(dir, name + ext)) && !string.Equals(Path.Combine(dir, name + ext), oldPath, StringComparison.OrdinalIgnoreCase); n++)
                        name = $"{FileSafe(title)} ({n})";
                    var newPath = _library.Relocate(e, name, null);
                    if (!string.Equals(newPath, oldPath, StringComparison.Ordinal))
                    {
                        _items.Remove(oldPath);
                        _items[newPath] = item;
                        CollectionPathChanged(oldPath, newPath);
                        _backup.Moved(oldPath, newPath);
                    }
                    e.Title = null;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Smart rename of {oldPath}: {ex.Message}");
                    failed++;
                    continue;
                }
            }
            else e.Title = title;
            undo.Items.Add(new[] { e.Path, oldPath, oldTitle });
            item.RefreshAll();
            await Task.Yield();
        }
        _library.SaveCache();
        if (undo.Items.Count > 0) JsonStore.Save(RenameUndoFile, undo);
        r.IsBusy = false;
        r.IsDone = true;
        r.CanUndo = undo.Items.Count > 0;
        r.Status = failed == 0
            ? P(undo.Items.Count, "{0} video renamed.", "{0:N0} videos renamed.")
            : F("{0} renamed, {1} couldn't be (in use, or a file with that name exists).", P(undo.Items.Count, "{0} video", "{0:N0} videos"), failed);
        RefreshView();
    }

    [RelayCommand]
    private void UndoSmartRename()
    {
        if (Rename is not { IsBusy: false } r || JsonStore.Load<RenameUndo>(RenameUndoFile) is not { } undo) return;
        var back = 0;
        foreach (var x in undo.Items)
        {
            if (x is not [{ } now, { } before, var title] || !_items.TryGetValue(now, out var item)) continue;
            var e = item.Entry;
            if (!string.Equals(now, before, StringComparison.Ordinal))
            {
                try
                {
                    var restored = _library.Relocate(e, Path.GetFileNameWithoutExtension(before), Path.GetDirectoryName(before));
                    _items.Remove(now);
                    _items[restored] = item;
                    CollectionPathChanged(now, restored);
                    _backup.Moved(now, restored);
                }
                catch (Exception ex) { Log.Warn($"Undo rename of {now}: {ex.Message}"); continue; }
            }
            e.Title = title;
            item.RefreshAll();
            back++;
        }
        _library.SaveCache();
        try { File.Delete(RenameUndoFile); } catch { /* gone */ }
        r.CanUndo = false;
        r.Status = P(back, "{0} video has its old name back.", "{0:N0} videos have their old names back.");
        RefreshView();
    }

    /// <summary>A title as a file name: no slashes, colons or the like; the "·" becomes a dash.</summary>
    private static string FileSafe(string title)
    {
        var bad = Path.GetInvalidFileNameChars().Concat(new[] { ':', '*', '?', '"', '<', '>', '|', '/', '\\' }).ToHashSet();
        var clean = new string(title.Replace(" · ", " - ").Select(c => bad.Contains(c) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "Clip" : clean;
    }
}
