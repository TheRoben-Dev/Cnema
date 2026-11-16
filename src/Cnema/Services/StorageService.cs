namespace Cnema.Services;

/// <summary>
/// Keeps the clips folder inside the limits from Settings: clips older than a number of days, and the
/// oldest clips once the folder passes a size cap, go to the trash or to an archive folder.
/// Favourites, montages and exports are never touched. Only the clips folder is ever cleaned, never other library folders.
/// </summary>
public static class StorageService
{
    private static readonly string[] Extensions = { ".mp4", ".mkv", ".mov", ".webm" };

    public sealed record Usage(int Count, long Bytes);
    public sealed record Result(int Removed, long Bytes, bool Archived);

    private static IEnumerable<FileInfo> Clips(string folder)
    {
        if (!Directory.Exists(folder)) yield break;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList(); }
        catch { yield break; }
        var montages = Path.Combine(folder, "Montages") + Path.DirectorySeparatorChar;
        foreach (var f in files)
        {
            if (!Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
            if (BinService.IsBinPath(f)) continue; // the bin deletes its own after 24 hours
            // Things you made from your clips are kept: montages, and exports ("Clip [Discord].mp4").
            if (f.StartsWith(montages, StringComparison.OrdinalIgnoreCase) || IsExport(f)) continue;
            FileInfo? info = null;
            try { info = new FileInfo(f); } catch { /* vanished */ }
            if (info is { Exists: true }) yield return info;
        }
    }

    private static bool IsExport(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith(']') && name.LastIndexOf(" [", StringComparison.Ordinal) > 0;
    }

    public static Usage Measure(string folder)
    {
        var count = 0;
        long bytes = 0;
        foreach (var f in Clips(folder)) { count++; bytes += f.Length; }
        return new Usage(count, bytes);
    }

    /// <summary>Which files the current limits would remove, oldest first.</summary>
    public static List<FileInfo> Plan(ClipSettings c, Func<string, bool> isFavourite, DateTime nowUtc)
    {
        var picked = new List<FileInfo>();
        if (c.KeepDays <= 0 && c.MaxFolderGb <= 0) return picked;
        var all = Clips(c.ResolvedFolder).OrderBy(f => f.LastWriteTimeUtc).ToList();
        var total = all.Sum(f => f.Length);
        var cap = c.MaxFolderGb > 0 ? c.MaxFolderGb * (1L << 30) : long.MaxValue;
        var cutoff = c.KeepDays > 0 ? nowUtc.AddDays(-c.KeepDays) : DateTime.MinValue;
        foreach (var f in all)
        {
            var tooOld = f.LastWriteTimeUtc < cutoff;
            if (!tooOld && total <= cap) continue;
            // Never a favourite, and never a clip from the last few minutes (it may still be open).
            if (isFavourite(f.FullName) || nowUtc - f.LastWriteTimeUtc < TimeSpan.FromMinutes(5)) continue;
            picked.Add(f);
            total -= f.Length;
        }
        return picked;
    }

    public static Result Apply(ClipSettings c, IEnumerable<FileInfo> files)
    {
        var removed = 0;
        long bytes = 0;
        var archive = c.CleanupArchive ? c.ResolvedArchiveFolder : null;
        foreach (var f in files)
        {
            try
            {
                var size = f.Length;
                if (archive is null) PlatformShell.MoveToTrash(f.FullName);
                else
                {
                    Directory.CreateDirectory(archive);
                    var target = Path.Combine(archive, f.Name);
                    for (var i = 2; File.Exists(target); i++)
                        target = Path.Combine(archive, $"{Path.GetFileNameWithoutExtension(f.Name)} {i}{f.Extension}");
                    File.Move(f.FullName, target);
                }
                removed++;
                bytes += size;
            }
            catch (Exception ex)
            {
                Log.Warn($"Storage cleanup could not remove {f.Name}: {ex.Message}");
            }
        }
        if (removed > 0)
            Log.Info($"Storage cleanup: {removed} clip(s), {bytes / (1 << 20)} MB {(archive is null ? "to the trash" : "archived")}");
        return new Result(removed, bytes, archive is not null);
    }
}
