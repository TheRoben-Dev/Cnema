using Avalonia.Threading;
using Cnema.Models;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// One library folder by default, and "Merge into one folder" for libraries spread over many.
public sealed partial class MainViewModel
{
    /// <summary>Several watched folders (Settings, Features). Off: the library is one folder, where clips are saved too.</summary>
    public bool SeveralFolders => Features.SeveralFolders == true;
    public bool HasSeveralFolders => _settings.Current.Folders.Count > 1;
    public string AddFolderLabel => SeveralFolders || HasNoFolders ? T("Add folder") : T("Change folder");

    private static bool SameFolder(string a, string b) =>
        string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Folders that OBS / NVIDIA / Steam record into: they stay watched whatever happens.</summary>
    private List<string> RecorderFolders() => _settings.Current.Recorders
        .Select(i => i.Kind == RecorderKind.Steam ? SteamOutput : i.Path).Where(p => p.Length > 0).ToList();

    /// <summary>The watched folders that aren't a recorder's.</summary>
    private List<string> LibraryFolders()
    {
        var recorders = RecorderFolders();
        return _settings.Current.Folders.Where(f => !recorders.Any(r => SameFolder(r, f))).ToList();
    }

    /// <summary>
    /// Settings from before "Several folders" existed: keep every folder people already added (more than
    /// one means they wanted several), and with a single one, save clips there so the library stays one folder.
    /// </summary>
    private void MigrateFolderMode()
    {
        if (Features.SeveralFolders is not null) return;
        Features.SeveralFolders = _settings.Current.Folders.Count > 1;
        var library = LibraryFolders();
        if (Features.SeveralFolders == false && library.Count == 1 && !Under(Path.Combine(_settings.Current.Clip.ResolvedFolder, "x"), library[0]))
            _settings.Current.Clip.Folder = library[0];
        _settings.Save();
    }

    /// <summary>One-folder mode: this folder is the library now (and where clips go). The old one is left as it is on disk.</summary>
    private async Task UseLibraryFolderAsync(string path, bool clipsToo)
    {
        var old = LibraryFolders();
        if (old.Any(f => SameFolder(f, path))) return;
        _settings.Current.Folders.RemoveAll(f => old.Any(o => SameFolder(o, f)));
        _settings.Current.Folders.Insert(0, path);
        if (clipsToo) _settings.Current.Clip.Folder = path;
        _settings.Save();
        foreach (var key in _items.Keys.Where(k => !_settings.Current.Folders.Any(f => Under(k, f))).ToList())
            _items.Remove(key);
        RebuildFolders(path);
        RefreshView();
        if (old.Count > 0)
            ShowToast(F("Your library is now {0}. Videos in the old folder stayed where they were.", Path.GetFileName(path.TrimEnd('/', '\\'))));
        await RescanAsync();
    }

    // ---------- "Merge into one folder?" ----------

    public bool ShowMergeSuggestion => !_settings.Current.MergeSuggestionClosed && IsPlainLibraryView && MergeReason is not null;

    private string? MergeReason
    {
        get
        {
            var all = _settings.Current.Folders.Count;
            if (all > 3) return F("Your videos come from {0} folders. Savefile can move them into one, so they're easier to find, back up and keep.", all);
            var library = LibraryFolders();
            if (!SeveralFolders && library.Count > 1)
                return F("Your videos are in {0} folders. Savefile can move them into one, so new clips and old ones live together.", library.Count);
            return null;
        }
    }

    public string MergeSuggestionText => MergeReason ?? "";

    private void RaiseMergeSuggestion()
    {
        OnPropertyChanged(nameof(ShowMergeSuggestion));
        OnPropertyChanged(nameof(MergeSuggestionText));
        OnPropertyChanged(nameof(AddFolderLabel));
        OnPropertyChanged(nameof(HasSeveralFolders));
        OnPropertyChanged(nameof(SeveralFolders));
    }

    [RelayCommand]
    private void CloseMergeSuggestion()
    {
        _settings.Current.MergeSuggestionClosed = true;
        _settings.Save();
        RaiseMergeSuggestion();
        ShowToast(T("Merge into one folder is under Clean up whenever you want it."));
    }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsShowingMerge))] private FolderMergeViewModel? _merge;
    public bool IsShowingMerge => Merge is not null;

    private static string MergeUndoFile => Path.Combine(AppPaths.DataDir, "merge-undo.json");

    [RelayCommand]
    private void OpenMerge()
    {
        if (Cleanup is not null) CloseCleanup();
        var target = _settings.Current.Clip.ResolvedFolder;
        Merge = new FolderMergeViewModel(target, MergeRows(target), File.Exists(MergeUndoFile));
    }

    [RelayCommand] private void CloseMerge() { if (Merge is not { IsBusy: true }) Merge = null; }

    [RelayCommand]
    private async Task PickMergeTargetAsync()
    {
        if (Merge is not { IsBusy: false } m || PickFolder is null) return;
        if (await PickFolder() is not { Length: > 0 } picked) return;
        picked = NormalizeFolder(picked);
        Merge = new FolderMergeViewModel(picked, MergeRows(picked), m.CanUndo);
    }

    /// <summary>Each watched folder, what it holds, and what merging would do with it.</summary>
    private List<MergeRow> MergeRows(string target)
    {
        var recorders = RecorderFolders();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetName = Path.GetFileName(target.TrimEnd('/', '\\'));
        var rows = new List<MergeRow>();
        foreach (var f in _settings.Current.Folders.Where(f => !SameFolder(f, target)))
        {
            var entries = _items.Values.Select(v => v.Entry).Where(e => Under(e.Path, f)).ToList();
            var isRecorder = recorders.Any(r => SameFolder(r, f));
            var inside = Under(Path.Combine(f, "x"), target);
            var holdsTarget = Under(Path.Combine(target, "x"), f);
            var online = Directory.Exists(f);
            var name = Path.GetFileName(f.TrimEnd('/', '\\')) is { Length: > 0 } n ? n : "Drive";
            var into = name;
            for (var i = 2; !used.Add(into); i++) into = $"{name} {i}";
            var row = new MergeRow
            {
                Path = f, Name = name, Videos = entries.Count, Bytes = entries.Sum(e => e.Size),
                IsInside = inside, IsRecorder = isRecorder, Into = into,
                CanMerge = online && !holdsTarget,
                IsChecked = online && !holdsTarget && !isRecorder,
            };
            row.Note = !online ? T("Not connected right now. It stays in your library; merge it when it's plugged in.")
                : holdsTarget ? T("Holds the folder everything goes into, so it stays as it is.")
                : inside ? F("Already inside {0}: nothing moves, the extra entry in the sidebar goes.", targetName)
                : isRecorder ? F("Goes to {0}/{1}. Its recorder keeps saving new videos here, so it stays watched.", targetName, into)
                : F("Goes to {0}/{1}", targetName, into);
            rows.Add(row);
        }
        return rows;
    }

    private sealed class MergeUndo
    {
        public List<string[]> Moves { get; set; } = new();
        public List<string> Folders { get; set; } = new();
        public bool? SeveralFolders { get; set; }
        public string? ClipFolder { get; set; }
        public string Target { get; set; } = "";
        public DateTime WhenUtc { get; set; }
    }

    [RelayCommand]
    private async Task RunMergeAsync()
    {
        if (Merge is not { IsBusy: false } m) return;
        var chosen = m.Rows.Where(r => r.IsChecked && r.CanMerge).ToList();
        if (chosen.Count == 0) { m.Status = T("Tick the folders to merge first."); m.StatusIsError = true; return; }
        var target = m.Target;
        try { Directory.CreateDirectory(target); }
        catch (Exception ex) { m.Status = T("Could not create the folder: ") + ex.Message; m.StatusIsError = true; return; }

        // Each video goes with the deepest chosen folder it's in (folders can sit inside each other).
        var moves = new List<(MergeRow Row, VideoEntry Entry)>();
        foreach (var v in _items.Values)
        {
            if (chosen.Where(r => !r.IsInside && Under(v.Entry.Path, r.Path)).OrderByDescending(r => r.Path.Length).FirstOrDefault() is { } row
                && !Under(v.Entry.Path, target))
                moves.Add((row, v.Entry));
        }

        // Another drive: the videos are copied, so there must be room for them.
        var targetRoot = AppPaths.VolumeOf(target);
        var crossing = moves.Where(x => !string.Equals(AppPaths.VolumeOf(x.Entry.Path), targetRoot, StringComparison.OrdinalIgnoreCase)).Sum(x => x.Entry.Size);
        try
        {
            var free = new DriveInfo(targetRoot).AvailableFreeSpace;
            if (crossing > 0 && crossing + (512L << 20) > free)
            {
                m.Status = F("There isn't room: the videos from other drives need {0}, and {1} is free there.", BackupViewModel.SizeText(crossing), BackupViewModel.SizeText(free));
                m.StatusIsError = true;
                return;
            }
        }
        catch { /* can't tell: try anyway, each failed move is reported */ }

        var undo = new MergeUndo
        {
            Folders = _settings.Current.Folders.ToList(), SeveralFolders = Features.SeveralFolders,
            ClipFolder = _settings.Current.Clip.Folder, WhenUtc = DateTime.UtcNow, Target = target,
        };
        m.IsBusy = true;
        m.StatusIsError = false;
        m.Status = T("Moving videos…");
        // No folder watching or scanning while files move about.
        _scanCts?.Cancel();
        _library.Watch(Array.Empty<string>());
        var progress = new Progress<double>(p => m.Progress = p);
        List<(string From, string To)> moved;
        int failed;
        try
        {
            (moved, failed) = await Task.Run(() => MoveAll(moves.Select(x => (x.Entry, Destination(target, x.Row, x.Entry.Path))).ToList(), progress));
        }
        catch (Exception ex)
        {
            Log.Warn($"Merge failed: {ex.Message}");
            m.IsBusy = false;
            m.Status = ex.Message;
            m.StatusIsError = true;
            RebuildFolders();
            return;
        }
        undo.Moves = moved.Select(x => new[] { x.From, x.To }).ToList();
        if (moved.Count > 0) JsonStore.Save(MergeUndoFile, undo);

        // The merged folders leave the sidebar (a recorder's stays: it records there). One left: one-folder mode.
        var gone = chosen.Where(r => !r.IsRecorder).Select(r => r.Path).ToList();
        _settings.Current.Folders.RemoveAll(f => gone.Any(g => SameFolder(g, f)));
        if (!_settings.Current.Folders.Any(f => Under(Path.Combine(target, "x"), f))) _settings.Current.Folders.Insert(0, target);
        if (LibraryFolders().Count == 1)
        {
            Features.SeveralFolders = false;
            _settings.Current.Clip.Folder = LibraryFolders()[0];
        }
        _settings.Save();
        AfterMoves(moved);
        m.IsBusy = false;
        m.IsDone = true;
        m.CanUndo = moved.Count > 0;
        m.Status = failed == 0
            ? F("Done: {0} moved into {1}.", P(moved.Count, "{0} video", "{0:N0} videos"), Path.GetFileName(target.TrimEnd('/', '\\')))
            : F("{0} moved, {1} couldn't be moved (in use or read-only). They stayed where they were.", P(moved.Count, "{0} video", "{0:N0} videos"), failed);
        m.StatusIsError = failed > 0;
        RaiseFeatures();
        await RescanAsync();
    }

    /// <summary>"Clips/OBS/2025/x.mp4" for x.mp4 from the OBS folder's 2025 subfolder.</summary>
    private static string Destination(string target, MergeRow row, string path) =>
        Path.Combine(target, row.Into, Path.GetRelativePath(row.Path, path));

    /// <summary>Moves files one by one (another drive: copy, then delete), keeping their details. Never overwrites.</summary>
    private (List<(string From, string To)> Moved, int Failed) MoveAll(List<(VideoEntry Entry, string To)> moves, IProgress<double> progress)
    {
        var moved = new List<(string, string)>();
        var failed = 0;
        for (var i = 0; i < moves.Count; i++)
        {
            var (entry, to) = moves[i];
            var from = entry.Path;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (File.Exists(to))
                {
                    var stem = Path.Combine(Path.GetDirectoryName(to)!, Path.GetFileNameWithoutExtension(to));
                    var ext = Path.GetExtension(to);
                    for (var n = 2; File.Exists(to); n++) to = $"{stem} ({n}){ext}";
                }
                // Details first, so a scan that finds the file already knows its favourite, tags and friends.
                _library.Adopt(to, entry);
                File.Move(from, to);
                moved.Add((from, to));
            }
            catch (Exception ex)
            {
                Log.Warn($"Merge: moving {from} failed: {ex.Message}");
                // A copy to another drive that broke off halfway: the original is still there, drop the half.
                try { if (File.Exists(from) && File.Exists(to) && new FileInfo(to).Length != new FileInfo(from).Length) File.Delete(to); }
                catch { /* leave it */ }
                failed++;
            }
            progress.Report((i + 1) / (double)moves.Count);
        }
        return (moved, failed);
    }

    /// <summary>Collections, remembered edits and backup marks follow each moved file.</summary>
    private void AfterMoves(List<(string From, string To)> moved)
    {
        foreach (var (from, to) in moved)
        {
            CollectionPathChanged(from, to);
            EditStore.Move(from, to);
            _backup.Moved(from, to);
            _items.Remove(from);
        }
        foreach (var key in _items.Keys.Where(k => !_settings.Current.Folders.Any(f => Under(k, f))).ToList())
            _items.Remove(key);
        _library.SaveCache();
        RebuildFolders();
        RefreshView();
    }

    /// <summary>Puts every moved video back, and the folders as they were.</summary>
    [RelayCommand]
    private async Task UndoMergeAsync()
    {
        if (Merge is not { IsBusy: false } m) return;
        if (JsonStore.Load<MergeUndo>(MergeUndoFile) is not { } undo) { m.CanUndo = false; return; }
        m.IsBusy = true;
        m.StatusIsError = false;
        m.Status = T("Putting videos back…");
        _scanCts?.Cancel();
        _library.Watch(Array.Empty<string>());
        var back = undo.Moves.Where(x => x.Length == 2).Select(x => (Entry: _items.TryGetValue(x[1], out var v) ? v.Entry : new VideoEntry { Path = x[1] }, To: x[0])).ToList();
        var progress = new Progress<double>(p => m.Progress = p);
        var (moved, failed) = await Task.Run(() =>
        {
            // Only back to where it came from, and only if nothing new took that name meanwhile.
            var list = back.Where(b => File.Exists(b.Entry.Path) && !File.Exists(b.To)).ToList();
            var result = MoveAll(list, progress);
            foreach (var b in back) RemoveEmptyFolders(Path.GetDirectoryName(b.Entry.Path), undo.Target);
            return (result.Moved, result.Failed + back.Count - list.Count);
        });
        _settings.Current.Folders = undo.Folders;
        Features.SeveralFolders = undo.SeveralFolders;
        _settings.Current.Clip.Folder = undo.ClipFolder;
        _settings.Save();
        try { File.Delete(MergeUndoFile); } catch { /* gone */ }
        AfterMoves(moved);
        m.IsBusy = false;
        m.CanUndo = false;
        m.IsDone = true;
        m.Status = failed == 0
            ? F("Put back: {0} in their old folders.", P(moved.Count, "{0} video", "{0:N0} videos"))
            : F("{0} put back. {1} stayed in the merged folder (moved, renamed or in use since).", P(moved.Count, "{0} video", "{0:N0} videos"), failed);
        m.StatusIsError = failed > 0;
        RaiseFeatures();
        await RescanAsync();
    }

    /// <summary>Tidies folders the merge emptied (the subfolders it made), never one with anything left in it.</summary>
    private static void RemoveEmptyFolders(string? dir, string stopAt)
    {
        while (dir is { Length: > 0 } && !SameFolder(dir, stopAt) && Under(dir, stopAt) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
        {
            try { Directory.Delete(dir); } catch { return; }
            dir = Path.GetDirectoryName(dir);
        }
    }
}
