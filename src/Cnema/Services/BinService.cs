using Cnema.Models;

namespace Cnema.Services;

/// <summary>One video in Savefile's bin: where it came from, where it waits, and when it was deleted.</summary>
public sealed class BinItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginalPath { get; set; } = "";
    public string BinPath { get; set; } = "";
    public DateTime DeletedUtc { get; set; }
    public long Bytes { get; set; }
    /// <summary>Favourites, tags, friends, notes and the rest, so a restore brings them back too.</summary>
    public VideoEntry? Details { get; set; }
}

public sealed class BinFile
{
    public List<BinItem> Items { get; set; } = new();
}

/// <summary>
/// Savefile's own bin. A deleted video waits here for 24 hours, each one on its own clock, and can be put
/// back with everything it had; after that it is deleted for good. The bin lives next to the video (a hidden
/// folder in the same folder), so putting a video in it is a quick rename even on another drive.
/// </summary>
public sealed class BinService
{
    public static readonly TimeSpan Keep = TimeSpan.FromHours(24);
    public const string FolderName = ".savefile-bin";

    private readonly string _file = Path.Combine(AppPaths.DataDir, "bin.json");
    private readonly object _lock = new();
    private BinFile _bin;

    public event Action? Changed;

    public BinService()
    {
        _bin = JsonStore.Load<BinFile>(_file);
    }

    public IReadOnlyList<BinItem> Items
    {
        get { lock (_lock) return _bin.Items.OrderByDescending(i => i.DeletedUtc).ToList(); }
    }

    /// <summary>Is this a path inside a bin folder (the library scan leaves those alone)?</summary>
    public static bool IsBinPath(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(FolderName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Moves the file into the bin beside it and remembers its details. Throws if the move fails.</summary>
    public BinItem Add(VideoEntry entry)
    {
        var dir = Path.Combine(Path.GetDirectoryName(entry.Path) ?? AppPaths.DataDir, FolderName);
        var info = Directory.CreateDirectory(dir);
        try { info.Attributes |= FileAttributes.Hidden; } catch { /* only a hint, on Windows */ }
        var item = new BinItem { OriginalPath = entry.Path, DeletedUtc = DateTime.UtcNow };
        item.BinPath = Path.Combine(dir, item.Id + "-" + Path.GetFileName(entry.Path));
        item.Bytes = new FileInfo(entry.Path).Length;
        File.Move(entry.Path, item.BinPath);
        var details = new VideoEntry { Path = entry.Path };
        details.CopyUserDataFrom(entry);
        item.Details = details;
        lock (_lock) _bin.Items.Add(item);
        Save();
        return item;
    }

    /// <summary>Puts a video back where it was (with a number if that name is taken). Returns the path it went to.</summary>
    public string Restore(BinItem item)
    {
        var target = item.OriginalPath;
        var dir = Path.GetDirectoryName(target) ?? "";
        Directory.CreateDirectory(dir);
        var name = Path.GetFileNameWithoutExtension(target);
        var ext = Path.GetExtension(target);
        for (var n = 2; File.Exists(target); n++) target = Path.Combine(dir, $"{name} ({n}){ext}");
        File.Move(item.BinPath, target);
        lock (_lock) _bin.Items.RemoveAll(i => i.Id == item.Id);
        Save();
        TryRemoveEmptyFolder(item.BinPath);
        return target;
    }

    /// <summary>Deletes one video for good right now.</summary>
    public void DeleteNow(BinItem item)
    {
        try { if (File.Exists(item.BinPath)) File.Delete(item.BinPath); }
        catch (Exception ex) { Log.Warn($"Bin: couldn't delete {item.BinPath}: {ex.Message}"); return; }
        lock (_lock) _bin.Items.RemoveAll(i => i.Id == item.Id);
        Save();
        TryRemoveEmptyFolder(item.BinPath);
    }

    /// <summary>Deletes every video whose own 24 hours are up. Files on a drive that isn't plugged in wait.</summary>
    public int Expire()
    {
        var due = Items.Where(i => DateTime.UtcNow - i.DeletedUtc >= Keep).ToList();
        var gone = 0;
        foreach (var item in due)
        {
            if (!File.Exists(item.BinPath))
            {
                // Removed by hand: forget it. On a drive that isn't plugged in: wait for it.
                if (DriveMissing(item.BinPath)) continue;
                lock (_lock) _bin.Items.RemoveAll(i => i.Id == item.Id);
                gone++;
                continue;
            }
            DeleteNow(item);
            gone++;
        }
        if (gone > 0) { Save(); Log.Info($"Bin: {gone} video(s) deleted for good after 24 hours"); }
        return gone;
    }

    /// <summary>
    /// The drive the file is on isn't plugged in. A drive letter on Windows; on a Mac or Linux an external drive
    /// is a folder under /Volumes, /media, /run/media or /mnt, whose root ("/") is always there.
    /// </summary>
    internal static bool DriveMissing(string path)
    {
        if (OperatingSystem.IsWindows())
            return Path.GetPathRoot(path) is { Length: > 0 } root && !Directory.Exists(root);
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var depth = parts.Length switch
        {
            > 1 when parts[0] is "Volumes" or "mnt" => 2,
            > 2 when parts[0] == "media" => 3,
            > 3 when parts[0] == "run" && parts[1] == "media" => 4,
            _ => 0,
        };
        return depth > 0 && !Directory.Exists("/" + string.Join('/', parts.Take(depth)));
    }

    private void Save()
    {
        lock (_lock)
        {
            try { JsonStore.Save(_file, _bin); }
            catch (Exception ex) { Log.Warn($"Bin: couldn't save the list: {ex.Message}"); }
        }
        Changed?.Invoke();
    }

    private static void TryRemoveEmptyFolder(string binPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(binPath);
            if (dir is not null && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { /* left for next time */ }
    }
}
