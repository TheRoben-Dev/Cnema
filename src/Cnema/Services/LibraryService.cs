using Cnema.Models;

namespace Cnema.Services;

/// <summary>
/// Scans the user's folders for video files, keeps a metadata/thumbnail cache keyed by path,
/// and watches the folders for changes.
/// </summary>
public sealed class LibraryService : IDisposable
{
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".ts", ".m2ts", ".mts",
        ".mpg", ".mpeg", ".3gp", ".ogv", ".vob", ".divx", ".f4v",
    };

    private readonly LibraryCacheFile _cache = LoadCache();

    // System.Text.Json fills a plain (case-sensitive) dictionary; paths are compared ignoring case.
    private static LibraryCacheFile LoadCache()
    {
        var file = JsonStore.Load<LibraryCacheFile>(AppPaths.LibraryFile);
        var entries = new Dictionary<string, VideoEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in file.Entries) entries.TryAdd(k, v);
        file.Entries = entries;
        return file;
    }

    /// <summary>Raised when a scan matched a file renamed or moved outside the app (old path, new path).</summary>
    public event Action<string, string>? EntryMoved;
    private readonly object _cacheLock = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private Timer? _debounce;

    /// <summary>Raised (on a background thread) when watched folders change on disk.</summary>
    public event Action? FoldersChanged;

    /// <summary>Names the game a newly found file belongs to (another recorder's folder), or null.</summary>
    public Func<string, string?>? GameTagger { get; set; }

    public IReadOnlyCollection<VideoEntry> CachedEntries
    {
        get { lock (_cacheLock) return _cache.Entries.Values.ToList(); }
    }

    private static bool IsUnder(string file, string folder) =>
        file.StartsWith(folder.TrimEnd('/', '\\') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lists all video files under the folders (fast, filesystem only).</summary>
    public static List<FileInfo> Enumerate(IEnumerable<string> folders)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        var files = new List<FileInfo>();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*", opts))
            {
                if (!VideoExtensions.Contains(Path.GetExtension(path))) continue;
                var name = Path.GetFileName(path);
                if (name.StartsWith("._", StringComparison.Ordinal)) continue; // macOS resource forks on exFAT
                // Savefile's own work in progress (Make room's smaller copy). A leading dot hides it on a Mac
                // and Linux, but not on Windows, where a scan would list it half-written.
                if (name.StartsWith(".savefile-", StringComparison.OrdinalIgnoreCase)) continue;
                if (BinService.IsBinPath(path)) continue; // deleted, waiting out its 24 hours
                files.Add(new FileInfo(path));
            }
        }
        return files;
    }

    /// <summary>
    /// Returns an entry for every file; unchanged files come straight from cache, new/changed ones
    /// are probed + thumbnailed. <paramref name="onEntryReady"/> fires as each finishes.
    /// </summary>
    public async Task<List<VideoEntry>> ScanAsync(
        IReadOnlyList<string> folders, Action<VideoEntry> onEntryReady, IProgress<(int done, int total)>? progress,
        CancellationToken ct)
    {
        var files = await Task.Run(() => Enumerate(folders), ct);
        var result = new List<VideoEntry>(files.Count);
        var work = new List<VideoEntry>();

        lock (_cacheLock)
        {
            foreach (var f in files)
            {
                var mtime = f.LastWriteTimeUtc;
                // Cache hit = same size + mtime, and the thumbnail is present (or can't be made anyway).
                if (_cache.Entries.TryGetValue(f.FullName, out var hit) && hit.Size == f.Length && hit.ModifiedUtc == mtime &&
                    (hit.ThumbPath is { } tp ? File.Exists(tp) : ToolLocator.Ffmpeg is null))
                {
                    result.Add(hit);
                    continue;
                }
                var e = new VideoEntry { Path = f.FullName, Size = f.Length, ModifiedUtc = mtime };
                if (hit is not null) { e.CopyUserDataFrom(hit); e.CopyAnalysisFrom(hit); }
                else if (GameTagger?.Invoke(f.FullName) is { } game) e.Tags.Add(game);
                result.Add(e);
                work.Add(e);
            }
        }
        // A file renamed or moved outside the app shows up as "new" while its old path vanished.
        // Match those by size + modified time so tags, favourites and resume position follow the file.
        lock (_cacheLock)
        {
            var live = new HashSet<string>(result.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
            // Rows kept for an unplugged drive are not "gone": they must not take part in the matching.
            var offlineNow = folders.Where(f => !Directory.Exists(f)).ToList();
            var orphans = _cache.Entries.Values.Where(v => !live.Contains(v.Path) && !offlineNow.Any(f => IsUnder(v.Path, f))).ToList();
            foreach (var e in work.Where(w => !_cache.Entries.ContainsKey(w.Path)))
            {
                var matches = orphans.Where(o => o.Size == e.Size && o.ModifiedUtc == e.ModifiedUtc).ToList();
                if (matches.Count != 1) continue; // ambiguous or none: don't guess
                e.CopyUserDataFrom(matches[0]);
                e.CopyAnalysisFrom(matches[0]);
                orphans.Remove(matches[0]);
                // Remembered edits and collections follow the file too.
                EntryMoved?.Invoke(matches[0].Path, e.Path);
            }
        }
        foreach (var e in result.Except(work)) onEntryReady(e);

        int done = 0;
        progress?.Report((0, work.Count));
        // A first scan of a big library takes minutes: save what's done now and then, so quitting
        // halfway doesn't throw away every video already read.
        var sinceSave = System.Diagnostics.Stopwatch.StartNew();
        await Parallel.ForEachAsync(work, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6),
            CancellationToken = ct,
        }, async (e, token) =>
        {
            // One file that can't be read (locked, half-written, ffmpeg refused to start) must never stop the
            // whole scan: it stays in the library without details and the next scan tries it again.
            try
            {
                await MediaProbe.ProbeAsync(e, token);
                e.ThumbPath = await ThumbnailService.EnsureAsync(e, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { Log.Warn($"Scan: {e.FileName}: {ex.Message}"); }
            var save = false;
            lock (_cacheLock)
            {
                _cache.Entries[e.Path] = e;
                if (sinceSave.Elapsed > TimeSpan.FromSeconds(15)) { sinceSave.Restart(); save = true; }
            }
            if (save) SaveCache();
            onEntryReady(e);
            progress?.Report((Interlocked.Increment(ref done), work.Count));
        });

        // Drop cache rows for files that no longer exist. A folder that is missing right now (an
        // external drive or a share that isn't mounted yet) keeps its rows, so tags, favourites,
        // friends and notes are still there when it comes back.
        lock (_cacheLock)
        {
            var live = new HashSet<string>(result.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
            var offline = folders.Where(f => !Directory.Exists(f)).ToList();
            foreach (var stale in _cache.Entries.Keys.Where(k => !live.Contains(k) && !offline.Any(f => IsUnder(k, f)) && !_adopted.Contains(k)).ToList())
                _cache.Entries.Remove(stale);
            // An adopted row has done its job once a scan has seen its file.
            _adopted.RemoveWhere(live.Contains);
        }
        SaveCache();
        return result;
    }

    /// <summary>
    /// Renames and/or moves a video on disk and keeps its library data (tags, thumbnail, resume point).
    /// Returns the new full path. Throws with a user-readable message when it can't be done.
    /// </summary>
    public string Relocate(VideoEntry entry, string newFileNameWithoutExtension, string? newDirectory)
    {
        var name = newFileNameWithoutExtension.Trim();
        if (name.Length == 0) throw new InvalidOperationException(T("The name can't be empty."));
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new InvalidOperationException(T("The name contains characters that aren't allowed in file names."));
        if (name is "." or ".." || name.EndsWith('.') )
            throw new InvalidOperationException(T("That name isn't allowed."));

        var oldPath = entry.Path;
        var dir = string.IsNullOrEmpty(newDirectory) ? Path.GetDirectoryName(oldPath)! : newDirectory;
        var newPath = Path.Combine(dir, name + Path.GetExtension(oldPath));
        if (string.Equals(newPath, oldPath, StringComparison.Ordinal)) return oldPath;

        if (!File.Exists(oldPath)) throw new InvalidOperationException(T("The file no longer exists on disk."));
        // Allow case-only renames on case-insensitive filesystems; otherwise never overwrite.
        var caseOnly = string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(newPath) || Directory.Exists(newPath)))
            throw new InvalidOperationException(T("A file with that name already exists there."));

        try
        {
            File.Move(oldPath, newPath);
            EditStore.Move(oldPath, newPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(T("Couldn't rename the file: ") + ex.Message);
        }

        lock (_cacheLock)
        {
            _cache.Entries.Remove(oldPath);
            entry.Path = newPath;
            entry.ModifiedUtc = File.GetLastWriteTimeUtc(newPath);
            _cache.Entries[newPath] = entry;
        }
        SaveCache();
        return newPath;
    }

    /// <summary>Moves a video to the trash and drops it from the library. Throws if that fails.</summary>
    public void Delete(VideoEntry entry)
    {
        PlatformShell.MoveToTrash(entry.Path);
        Forget(entry);
    }

    /// <summary>Drops a video that has left (to the bin, say) from the library, with its thumbnails.</summary>
    public void Forget(VideoEntry entry)
    {
        lock (_cacheLock) _cache.Entries.Remove(entry.Path);
        try
        {
            if (entry.ThumbPath is { Length: > 0 } thumb && File.Exists(thumb)) File.Delete(thumb);
            var strip = ThumbnailService.StripPathFor(entry);
            if (File.Exists(strip)) File.Delete(strip);
        }
        catch { /* an orphaned thumbnail is harmless */ }
        SaveCache();
    }

    /// <summary>
    /// A file about to appear (restored from a backup) takes over these favourites, tags, friends and
    /// notes; the next scan reads the file itself and keeps them.
    /// </summary>
    public void Adopt(string path, VideoEntry details)
    {
        var e = new VideoEntry { Path = path };
        e.CopyUserDataFrom(details);
        lock (_cacheLock)
        {
            _cache.Entries[path] = e;
            // A scan that listed the folder before this file arrived must not throw the row away.
            _adopted.Add(path);
        }
    }

    private readonly HashSet<string> _adopted = new(StringComparer.OrdinalIgnoreCase);

    private int _saveQueued;
    private readonly object _writeLock = new();

    /// <summary>
    /// Writes library.json soon, off the calling thread. With smart data the file runs to many megabytes, and it
    /// used to be written in full, on the UI thread, after every edit and every few indexed videos (a visible
    /// hitch each time). Saves asked for close together now make one write.
    /// </summary>
    public void SaveCache()
    {
        if (Interlocked.Exchange(ref _saveQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(750).ConfigureAwait(false);
            try { FlushCache(); }
            catch (Exception ex) { Log.Warn($"Could not save the library: {ex.Message}"); }
        });
    }

    /// <summary>Writes library.json now (at quit, or from the delayed save).</summary>
    public void FlushCache()
    {
        lock (_writeLock)
        {
            Interlocked.Exchange(ref _saveQueued, 0);
            for (var attempt = 1; ; attempt++)
            {
                // The rows are copied under the lock; the videos themselves may be edited on the UI thread while
                // they're written out, which a list being changed at that very moment can trip over: try again.
                LibraryCacheFile snapshot;
                lock (_cacheLock)
                    snapshot = new LibraryCacheFile { Entries = new Dictionary<string, VideoEntry>(_cache.Entries, StringComparer.OrdinalIgnoreCase) };
                try
                {
                    JsonStore.Save(AppPaths.LibraryFile, snapshot, compact: true);
                    return;
                }
                catch (InvalidOperationException) when (attempt < 5)
                {
                    Thread.Sleep(20 * attempt);
                }
            }
        }
    }

    public void Watch(IEnumerable<string> folders)
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            try
            {
                var w = new FileSystemWatcher(folder) { IncludeSubdirectories = true, InternalBufferSize = 64 * 1024 };
                w.Created += OnFsEvent;
                w.Deleted += OnFsEvent;
                w.Renamed += OnFsEvent;
                // A big copy can overflow the watcher (Windows drops events then); look through the folder instead.
                w.Error += (_, err) => { Log.Warn($"Watching {folder}: {err.GetException().Message}"); Settle(); };
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex)
            {
                Log.Warn($"Cannot watch {folder}: {ex.Message}");
            }
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (!Matters(e.FullPath) && !(e is RenamedEventArgs r && Matters(r.OldFullPath))) return;
        Settle();
    }

    /// <summary>A video, or a folder that might hold some (moved, renamed or deleted along with its clips).</summary>
    private static bool Matters(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith('.')) return false; // the Bin, ._ files, hidden folders
        var ext = Path.GetExtension(name);
        return VideoExtensions.Contains(ext) || (ext.Length == 0 && !File.Exists(path));
    }

    private readonly object _debounceLock = new();
    private bool _disposed;

    /// <summary>Copies and exports fire many events: look again once things have been quiet for two seconds.</summary>
    private void Settle()
    {
        lock (_debounceLock)
        {
            if (_disposed) return;
            _debounce ??= new Timer(_ => FoldersChanged?.Invoke());
            _debounce.Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        lock (_debounceLock)
        {
            _disposed = true;
            _debounce?.Dispose();
            _debounce = null;
        }
    }
}
