using Cinema.Models;

namespace Cinema.Services;

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

    private readonly LibraryCacheFile _cache = JsonStore.Load<LibraryCacheFile>(AppPaths.LibraryFile);
    private readonly object _cacheLock = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private Timer? _debounce;

    /// <summary>Raised (on a background thread) when watched folders change on disk.</summary>
    public event Action? FoldersChanged;

    public IReadOnlyCollection<VideoEntry> CachedEntries
    {
        get { lock (_cacheLock) return _cache.Entries.Values.ToList(); }
    }

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
                if (Path.GetFileName(path).StartsWith("._")) continue; // macOS resource forks on exFAT
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
                if (hit is not null) e.CopyUserDataFrom(hit);
                result.Add(e);
                work.Add(e);
            }
        }
        // A file renamed or moved outside the app shows up as "new" while its old path vanished.
        // Match those by size + modified time so tags, favourites and resume position follow the file.
        lock (_cacheLock)
        {
            var live = new HashSet<string>(result.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
            var orphans = _cache.Entries.Values.Where(v => !live.Contains(v.Path)).ToList();
            foreach (var e in work.Where(w => !_cache.Entries.ContainsKey(w.Path)))
            {
                var matches = orphans.Where(o => o.Size == e.Size && o.ModifiedUtc == e.ModifiedUtc).ToList();
                if (matches.Count != 1) continue; // ambiguous or none: don't guess
                e.CopyUserDataFrom(matches[0]);
                orphans.Remove(matches[0]);
            }
        }
        foreach (var e in result.Except(work)) onEntryReady(e);

        int done = 0;
        progress?.Report((0, work.Count));
        await Parallel.ForEachAsync(work, new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6),
            CancellationToken = ct,
        }, async (e, token) =>
        {
            await MediaProbe.ProbeAsync(e, token);
            e.ThumbPath = await ThumbnailService.EnsureAsync(e, token);
            lock (_cacheLock) _cache.Entries[e.Path] = e;
            onEntryReady(e);
            progress?.Report((Interlocked.Increment(ref done), work.Count));
        });

        // Drop cache rows for files that no longer exist under any folder.
        lock (_cacheLock)
        {
            var live = new HashSet<string>(result.Select(r => r.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _cache.Entries.Keys.Where(k => !live.Contains(k)).ToList())
                _cache.Entries.Remove(stale);
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
        if (name.Length == 0) throw new InvalidOperationException("The name can't be empty.");
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new InvalidOperationException("The name contains characters that aren't allowed in file names.");
        if (name is "." or ".." || name.EndsWith('.') )
            throw new InvalidOperationException("That name isn't allowed.");

        var oldPath = entry.Path;
        var dir = string.IsNullOrEmpty(newDirectory) ? Path.GetDirectoryName(oldPath)! : newDirectory;
        var newPath = Path.Combine(dir, name + Path.GetExtension(oldPath));
        if (string.Equals(newPath, oldPath, StringComparison.Ordinal)) return oldPath;

        if (!File.Exists(oldPath)) throw new InvalidOperationException("The file no longer exists on disk.");
        // Allow case-only renames on case-insensitive filesystems; otherwise never overwrite.
        var caseOnly = string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(newPath) || Directory.Exists(newPath)))
            throw new InvalidOperationException("A file with that name already exists there.");

        try
        {
            File.Move(oldPath, newPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Couldn't rename the file: " + ex.Message);
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

    public void SaveCache()
    {
        lock (_cacheLock) JsonStore.Save(AppPaths.LibraryFile, _cache);
    }

    public void Watch(IEnumerable<string> folders)
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        foreach (var folder in folders.Where(Directory.Exists))
        {
            try
            {
                var w = new FileSystemWatcher(folder) { IncludeSubdirectories = true, EnableRaisingEvents = true };
                w.Created += OnFsEvent;
                w.Deleted += OnFsEvent;
                w.Renamed += OnFsEvent;
                _watchers.Add(w);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Cinema] Cannot watch {folder}: {ex.Message}");
            }
        }
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        if (!VideoExtensions.Contains(Path.GetExtension(e.FullPath)) &&
            !(e is RenamedEventArgs r && VideoExtensions.Contains(Path.GetExtension(r.OldFullPath))))
            return;
        // Debounce: copies/exports fire many events; wait for things to settle.
        _debounce?.Dispose();
        _debounce = new Timer(_ => FoldersChanged?.Invoke(), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _debounce?.Dispose();
    }
}
