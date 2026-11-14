using System.Globalization;
using System.Text;

namespace Cnema.Services;

/// <summary>One chapter of a video, in milliseconds.</summary>
public sealed record Chapter(int Number, long Start, long End, string Title);

/// <summary>
/// Chapters made from session bookmarks: each bookmark starts a chapter, and whatever comes before the
/// first one is "Start". They show in the player and are written into session recordings and exports
/// (as an ffmpeg metadata file), so other players see them too.
/// </summary>
public static class Chapters
{
    /// <summary>The "Chapters from bookmarks" feature; off, nothing is shown or written.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Bookmarks closer than this to the start, the end or each other don't make a chapter.</summary>
    private const long MinLength = 1000;

    public static IReadOnlyList<Chapter> FromBookmarks(IEnumerable<long> bookmarks, long durationMs)
    {
        if (durationMs <= 0) return Array.Empty<Chapter>();
        var starts = new List<long>();
        foreach (var b in bookmarks.Order())
        {
            if (b < MinLength || b > durationMs - MinLength) continue;
            if (starts.Count > 0 && b - starts[^1] < MinLength) continue;
            starts.Add(b);
        }
        if (starts.Count == 0) return Array.Empty<Chapter>();

        var list = new List<Chapter> { new(1, 0, starts[0], "Start") };
        for (var i = 0; i < starts.Count; i++)
            list.Add(new Chapter(i + 2, starts[i], i + 1 < starts.Count ? starts[i + 1] : durationMs, $"Bookmark {i + 1}"));
        return list;
    }

    /// <summary>The chapters that fall inside [inMs, outMs), moved so the cut starts at 0.</summary>
    public static IReadOnlyList<Chapter> Within(IReadOnlyList<Chapter> chapters, long inMs, long outMs)
    {
        var list = new List<Chapter>();
        foreach (var c in chapters)
        {
            var s = Math.Max(c.Start, inMs);
            var e = Math.Min(c.End, outMs);
            if (e - s < MinLength) continue;
            list.Add(new Chapter(list.Count + 1, s - inMs, e - inMs, c.Title));
        }
        return list.Count > 1 ? list : Array.Empty<Chapter>();
    }

    /// <summary>Writes an FFMETADATA1 file that ffmpeg reads with <c>-i file -map_chapters N</c>.</summary>
    public static string WriteMetadataFile(IReadOnlyList<Chapter> chapters)
    {
        var sb = new StringBuilder(";FFMETADATA1\n");
        foreach (var c in chapters)
        {
            sb.Append("[CHAPTER]\nTIMEBASE=1/1000\n");
            sb.Append("START=").Append(c.Start.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("END=").Append(c.End.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("title=").Append(Escape(c.Title)).Append('\n');
        }
        var path = Path.Combine(Path.GetTempPath(), $"cnema-chapters-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    // '=', ';', '#', '\' and newlines are special in ffmetadata.
    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (ch is '=' or ';' or '#' or '\\' or '\n') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
