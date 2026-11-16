using System.Security.Cryptography;
using System.Text;
using Cnema.Services;

namespace Cnema.ViewModels;

// Drag-out sizes: a video too big for Discord is dragged as a copy that fits.
public sealed partial class MainViewModel
{
    private static string ShareCacheDir => Path.Combine(AppPaths.DataDir, "drag-copies");

    /// <summary>The size a dragged-out file may have, or 0 when the original is always dragged.</summary>
    public long DragLimitBytes => Features.DragOut && Features.DragDiscord ? (Features.DiscordNitro ? 50L : 10L) * 1000 * 1000 : 0;

    private SharePreset DragPreset => Features.DiscordNitro ? SharePreset.Discord50 : SharePreset.Discord10;

    /// <summary>The copy made for dragging <paramref name="path"/> out, if it's ready (made earlier, same file).</summary>
    public string? ReadyDragCopy(string path) => File.Exists(DragCopyPath(path)) ? DragCopyPath(path) : null;

    /// <summary>True when this file is too big to drag into Discord as it is.</summary>
    public bool NeedsDragCopy(string path) =>
        DragLimitBytes > 0 && File.Exists(path) && new FileInfo(path).Length > DragLimitBytes;

    private string DragCopyPath(string path)
    {
        var fi = new FileInfo(path);
        var key = $"{path}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}|{DragPreset}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
        return Path.Combine(ShareCacheDir, hash, Path.GetFileNameWithoutExtension(path) + ".mp4");
    }

    /// <summary>Makes the Discord-sized copy of <paramref name="path"/> (kept for next time) and returns its path.</summary>
    public async Task<string> MakeDragCopyAsync(string path, IProgress<double>? progress, CancellationToken ct)
    {
        var target = DragCopyPath(path);
        if (File.Exists(target)) return target;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".part.mp4";
        var duration = _items.TryGetValue(path, out var item) ? item.Entry.DurationMs : 0;
        if (duration <= 0) duration = (await MediaProbe.InfoAsync(path, ct))?.DurationMs ?? 0;
        await TrimService.ShareAsync(path, temp, 0, duration, DragPreset, progress, ct);
        File.Move(temp, target, overwrite: true);
        return target;
    }

    /// <summary>Copies older than a week go, and the folder stays under 1 GB.</summary>
    private static void TrimDragCopies()
    {
        try
        {
            if (!Directory.Exists(ShareCacheDir)) return;
            var files = new DirectoryInfo(ShareCacheDir).GetFiles("*", SearchOption.AllDirectories).OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long kept = 0;
            foreach (var f in files)
            {
                kept += f.Length;
                if (f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-7) || kept > 1_000_000_000 || f.Name.EndsWith(".part.mp4", StringComparison.Ordinal))
                {
                    try { f.Delete(); f.Directory?.Delete(false); } catch { /* in use or not empty */ }
                }
            }
        }
        catch (Exception ex) { Log.Warn("Couldn't tidy the drag copies: " + ex.Message); }
    }
}
