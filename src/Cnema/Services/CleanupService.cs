using System.Security.Cryptography;
using Cnema.Models;

namespace Cnema.Services;

public enum CleanupReason { Duplicate, TooShort, Unreadable, Empty }

/// <summary>A file the clean-up found, and why. <see cref="Group"/> ties duplicates of one video together.</summary>
public sealed record CleanupFinding(VideoEntry Entry, CleanupReason Reason, int Group, bool Suggested);

/// <summary>
/// Finds duplicate videos and junk (empty, unreadable or very short files). Nothing is removed here;
/// the user picks what goes to the trash.
/// </summary>
public static class CleanupService
{
    public const long ShortMs = 2000;
    private const int SampleBytes = 256 * 1024;

    public static async Task<List<CleanupFinding>> FindAsync(IReadOnlyCollection<VideoEntry> entries, CancellationToken ct)
    {
        var found = new List<CleanupFinding>();
        var junk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            // A file written in the last few minutes may still be recording (OBS, a session): its
            // length isn't known yet, which is not the same as broken.
            if (DateTime.UtcNow - e.ModifiedUtc < TimeSpan.FromMinutes(10)) continue;
            CleanupReason? why = e.Size == 0 ? CleanupReason.Empty
                : e.DurationMs <= 0 ? CleanupReason.Unreadable
                : e.DurationMs < ShortMs ? CleanupReason.TooShort
                : null;
            if (why is not { } r) continue;
            found.Add(new CleanupFinding(e, r, -1, true));
            junk.Add(e.Path);
        }

        // Same size first (cheap), then the same bytes at the start, middle and end: copies of a video
        // match exactly, while two different recordings of the same length practically never do.
        var group = 0;
        foreach (var bySize in entries.Where(e => e.Size > 0 && !junk.Contains(e.Path)).GroupBy(e => e.Size).Where(g => g.Count() > 1))
        {
            var byHash = new Dictionary<string, List<VideoEntry>>();
            foreach (var e in bySize)
            {
                ct.ThrowIfCancellationRequested();
                var h = await Task.Run(() => Fingerprint(e.Path), ct);
                if (h is null) continue;
                if (!byHash.TryGetValue(h, out var list)) byHash[h] = list = new List<VideoEntry>();
                list.Add(e);
            }
            foreach (var same in byHash.Values.Where(l => l.Count > 1))
            {
                // Keep the one that was there first; the later copies are suggested for removal.
                var ordered = same.OrderBy(e => e.ModifiedUtc).ThenBy(e => e.Path.Length).ToList();
                for (var i = 0; i < ordered.Count; i++)
                    found.Add(new CleanupFinding(ordered[i], CleanupReason.Duplicate, group, i > 0));
                group++;
            }
        }
        return found;
    }

    private static string? Fingerprint(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[SampleBytes];
            foreach (var at in new[] { 0L, Math.Max(0, fs.Length / 2 - SampleBytes / 2), Math.Max(0, fs.Length - SampleBytes) })
            {
                fs.Position = at;
                var n = fs.Read(buffer, 0, buffer.Length);
                sha.AppendData(buffer, 0, n);
            }
            return Convert.ToHexString(sha.GetHashAndReset());
        }
        catch
        {
            return null;
        }
    }
}
