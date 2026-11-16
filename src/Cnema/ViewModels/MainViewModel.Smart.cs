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
        if (!Features.Highlights && !Features.AutoTitles) return;
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
    private bool SmartShouldWait => Clips.ActiveGame is not null || Clips.IsSession || Exports.IsBusy || Player.IsExporting || IsScanning;

    private async Task RunSmartPassAsync(CancellationToken ct)
    {
        try
        {
            var since = DateTime.UtcNow;
            var done = 0;
            while (!ct.IsCancellationRequested)
            {
                if (!Features.Highlights) break;
                var todo = _items.Values.Where(v => v.Entry.Smart is not { Version: SmartAnalyzer.Version } && File.Exists(v.Entry.Path))
                    .OrderByDescending(v => v.Entry.ModifiedUtc).ToList();
                if (todo.Count == 0) break;
                var item = todo[0];
                SmartActivity = F("Finding best moments: {0} to go", todo.Count.ToString("N0"));
                while (SmartShouldWait && !ct.IsCancellationRequested) await Task.Delay(5000, ct);
                SmartInfo? info;
                try { info = await SmartAnalyzer.AnalyzeAsync(item.Entry.Path, item.Entry.DurationMs, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warn($"Best moments for {item.Entry.FileName}: {ex.Message}");
                    info = new SmartInfo { Version = SmartAnalyzer.Version };
                }
                // The item may have been renamed, moved or deleted meanwhile.
                if (_items.TryGetValue(item.Entry.Path, out var still) && ReferenceEquals(still, item))
                {
                    item.Entry.Smart = info ?? new SmartInfo { Version = SmartAnalyzer.Version };
                    AutoTitle(item);
                }
                if (++done % 25 == 0) { _library.SaveCache(); UpdateHighlightMarks(); }
                if (DateTime.UtcNow - since > TimeSpan.FromSeconds(30) && SortIndex == 5) { since = DateTime.UtcNow; RefreshView(); }
                // Breathe between videos: this is background work.
                await Task.Delay(150, ct);
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (Exception ex) { Log.Warn($"Best moments pass stopped: {ex.Message}"); }
        finally
        {
            SmartActivity = "";
            _library.SaveCache();
            UpdateHighlightMarks();
            if (SortIndex == 5) RefreshView();
        }
    }

    /// <summary>The top tenth of the library (and clearly eventful) get a Highlight mark on their card.</summary>
    private void UpdateHighlightMarks()
    {
        var scored = _items.Values.Where(v => v.Entry.Smart is { Version: > 0 }).Select(v => v.Entry.Smart!.Excitement).OrderByDescending(x => x).ToList();
        var threshold = scored.Count < 10 ? double.MaxValue : Math.Max(0.45, scored[Math.Max(0, scored.Count / 10 - 1)]);
        VideoItemViewModel.HighlightThreshold = Features.Highlights ? threshold : double.MaxValue;
        foreach (var v in _items.Values) v.RefreshHighlight();
        RefreshMemories();
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
