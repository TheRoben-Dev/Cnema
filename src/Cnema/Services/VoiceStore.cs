namespace Cnema.Services;

/// <summary>
/// The voices Savefile knows, kept on this computer only (voices.json): for each friend, and for you, the
/// voiceprints from the answers in "Who's that?", and the voices you said aren't people (a game's announcer,
/// music, a bot), so they're never asked about again.
/// </summary>
public sealed class VoiceStore
{
    public const string MeKey = "\u0001me";

    public sealed class Data
    {
        /// <summary>Voiceprints (base64, 256 floats) per friend name; <see cref="MeKey"/> for you.</summary>
        public Dictionary<string, List<string>> Known { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Ignored { get; set; } = new();
    }

    /// <summary>How many voiceprints are kept per person; the oldest go first.</summary>
    private const int Keep = 40;
    private static string FilePath => Path.Combine(AppPaths.DataDir, "voices.json");
    private Data? _data;
    private Dictionary<string, List<float[]>>? _vecs;
    private List<float[]>? _ignored;

    public Data Current => _data ??= Load();

    private static Data Load()
    {
        var d = JsonStore.Load<Data>(FilePath);
        d.Known = new Dictionary<string, List<string>>(d.Known, StringComparer.OrdinalIgnoreCase);
        return d;
    }

    public void Save() => JsonStore.Save(FilePath, Current);

    /// <summary>Everyone's voiceprints, unpacked.</summary>
    public IReadOnlyDictionary<string, List<float[]>> Vectors =>
        _vecs ??= Current.Known.ToDictionary(kv => kv.Key, kv => kv.Value.Select(Unpack).ToList(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<float[]> IgnoredVectors => _ignored ??= Current.Ignored.Select(Unpack).ToList();

    public int CountFor(string name) => Current.Known.TryGetValue(name, out var l) ? l.Count : 0;

    public void Add(string name, IEnumerable<float[]> prints)
    {
        if (!Current.Known.TryGetValue(name, out var list)) Current.Known[name] = list = new List<string>();
        list.AddRange(prints.Select(Models.VoiceSpeaker.Pack));
        if (list.Count > Keep) list.RemoveRange(0, list.Count - Keep);
        _vecs = null;
        Save();
    }

    public void Remove(string name, IEnumerable<float[]> prints)
    {
        if (!Current.Known.TryGetValue(name, out var list)) return;
        foreach (var p in prints.Select(Models.VoiceSpeaker.Pack)) list.Remove(p);
        if (list.Count == 0) Current.Known.Remove(name);
        _vecs = null;
        Save();
    }

    public void Ignore(IEnumerable<float[]> prints, bool undo = false)
    {
        foreach (var p in prints.Select(Models.VoiceSpeaker.Pack))
            if (undo) Current.Ignored.Remove(p); else Current.Ignored.Add(p);
        if (Current.Ignored.Count > 200) Current.Ignored.RemoveRange(0, Current.Ignored.Count - 200);
        _ignored = null;
        Save();
    }

    /// <summary>Renames a friend's voice along with the friend.</summary>
    public void Rename(string from, string to)
    {
        if (!Current.Known.Remove(from, out var list)) return;
        if (Current.Known.TryGetValue(to, out var have)) have.AddRange(list); else Current.Known[to] = list;
        _vecs = null;
        Save();
    }

    /// <summary>Who a voiceprint sounds like: the best match (cosine to that person's closest prints), best first.</summary>
    public List<(string Name, float Score)> Match(float[] print)
    {
        var list = new List<(string, float)>();
        foreach (var (name, vecs) in Vectors)
        {
            if (vecs.Count == 0) continue;
            // The mean of the three closest: one odd answer doesn't decide it, and a voice heard in many moods still matches.
            var top = vecs.Select(v => VoiceService.Cosine(v, print)).OrderByDescending(c => c).Take(3).ToList();
            list.Add((name, top.Average()));
        }
        return list.OrderByDescending(x => x.Item2).ToList();
    }

    public bool IsIgnored(float[] print) => IgnoredVectors.Any(v => VoiceService.Cosine(v, print) >= VoiceService.SameVoice + 0.1f);

    private static float[] Unpack(string b64) => System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(b64)).ToArray();
}
