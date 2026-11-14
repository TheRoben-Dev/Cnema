namespace Cnema.Services;

/// <summary>What the editor had for one video: never applied to the file, only a recipe for the next export.</summary>
public sealed class SavedEdit
{
    public double In { get; set; } = -1;
    public double Out { get; set; } = -1;
    public List<double[]> Parts { get; set; } = new();
    public List<CaptionSpec> Captions { get; set; } = new();
    public List<SpeedSpec> Speed { get; set; } = new();
    public List<BlurSpec> Blurs { get; set; } = new();
    public List<ZoomKey> Zoom { get; set; } = new();
    public string? Music { get; set; }
    public DateTime SavedUtc { get; set; }
}

/// <summary>
/// Remembered editor state per video path (Settings, Features, Remember edits). Kept in Cnema's own data
/// folder, not next to the videos, so nothing is written into the user's folders.
/// </summary>
public static class EditStore
{
    private const int MaxEntries = 500;
    private static string FilePath => Path.Combine(AppPaths.DataDir, "edits.json");
    private static Dictionary<string, SavedEdit>? _edits;

    private static Dictionary<string, SavedEdit> Edits => _edits ??= JsonStore.Load<Dictionary<string, SavedEdit>>(FilePath);

    public static SavedEdit? Get(string path) => Edits.TryGetValue(path, out var e) ? e : null;

    public static void Set(string path, SavedEdit edit)
    {
        edit.SavedUtc = DateTime.UtcNow;
        Edits[path] = edit;
        // Old entries for videos nobody opens any more go first.
        foreach (var old in Edits.OrderBy(kv => kv.Value.SavedUtc).Take(Math.Max(0, Edits.Count - MaxEntries)).Select(kv => kv.Key).ToList())
            Edits.Remove(old);
        Save();
    }

    public static void Remove(string path)
    {
        if (Edits.Remove(path)) Save();
    }

    /// <summary>A renamed or moved video keeps its edits.</summary>
    public static void Move(string from, string to)
    {
        if (!Edits.Remove(from, out var e)) return;
        Edits[to] = e;
        Save();
    }

    private static void Save()
    {
        try { JsonStore.Save(FilePath, Edits); }
        catch (Exception ex) { Log.Warn($"Could not save edits: {ex.Message}"); }
    }
}
