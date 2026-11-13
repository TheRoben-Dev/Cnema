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
                if (hit is not null) { e.LastPositionMs = hit.LastPositionMs; e.LastPlayedUtc = hit.LastPlayedUtc; }
                result.Add(e);
                work.Add(e);
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
