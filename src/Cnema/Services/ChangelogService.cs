using System.Reflection;

namespace Cnema.Services;

public sealed record ChangelogEntry(Version Version, IReadOnlyList<string> Items)
{
    public string Title => "Version " + Version.ToString(3);
}

/// <summary>Reads CHANGELOG.md, which is built into the app so it can be shown offline.</summary>
public static class ChangelogService
{
    private static IReadOnlyList<ChangelogEntry>? _all;

    /// <summary>Every release, newest first.</summary>
    public static IReadOnlyList<ChangelogEntry> All => _all ??= Parse(ReadEmbedded());

    /// <summary>
    /// Entries newer than <paramref name="lastSeen"/>, up to the running version. With no last-seen
    /// version (an update from a build that did not track it) only the running version's entry.
    /// </summary>
    public static IReadOnlyList<ChangelogEntry> Since(Version? lastSeen)
    {
        var upTo = All.Where(e => e.Version <= UpdateService.Current).ToList();
        return lastSeen is null ? upTo.Take(1).ToList() : upTo.Where(e => e.Version > lastSeen).ToList();
    }

    private static string ReadEmbedded()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Cnema.CHANGELOG.md");
            if (stream is null) return "";
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return "";
        }
    }

    internal static IReadOnlyList<ChangelogEntry> Parse(string text)
    {
        var entries = new List<ChangelogEntry>();
        Version? version = null;
        var items = new List<string>();
        void Flush()
        {
            if (version is not null && items.Count > 0) entries.Add(new ChangelogEntry(version, items.ToList()));
            items.Clear();
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                version = UpdateService.Parse(line[3..].Trim().Split(' ')[0]);
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) && version is not null)
            {
                items.Add(line[2..].Trim());
            }
        }
        Flush();
        return entries.OrderByDescending(e => e.Version).ToList();
    }
}
