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
        /// <summary>Points, streaks and badges from "Who's that?".</summary>
        public VoiceGameStats Game { get; set; } = new();
        /// <summary>The voices already asked about in the game, whatever the answer (a skip too): short hashes of their voiceprints.</summary>
        public List<string> Answered { get; set; } = new();
    }

    /// <summary>The game's progress: kept with the voices, so it goes wherever they go.</summary>
    public sealed class VoiceGameStats
    {
        public int Points { get; set; }
        /// <summary>Answers that named someone (a friend or you).</summary>
        public int Named { get; set; }
        public int NotPeople { get; set; }
        /// <summary>Times Mochi's first guess was the right one.</summary>
        public int GuessedRight { get; set; }
        public int BestStreak { get; set; }
        public int BestSession { get; set; }
        public bool NamedMe { get; set; }
        public List<string> Friends { get; set; } = new();
        /// <summary>The days played (yyyy-MM-dd), for the "a few different days" badge; only the latest few are kept.</summary>
        public List<string> Days { get; set; } = new();
        public List<string> Badges { get; set; } = new();

        public VoiceGameStats Copy() => new()
        {
            Points = Points, Named = Named, NotPeople = NotPeople, GuessedRight = GuessedRight, BestStreak = BestStreak,
            BestSession = BestSession, NamedMe = NamedMe, Friends = new(Friends), Days = new(Days), Badges = new(Badges),
        };
    }

    /// <summary>How many voiceprints are kept per person; the oldest go first.</summary>
    private const int Keep = 40;
    private static string FilePath => Path.Combine(AppPaths.DataDir, "voices.json");
    private Data? _data;
    private Dictionary<string, List<float[]>>? _vecs;
    private List<float[]>? _ignored;
    // The game puts its next rounds together on another thread while answers change the lists here: the
    // unpacked copies are built under this lock and never changed after, only replaced.
    private readonly object _sync = new();

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
        _vecs ?? Build(() => _vecs ??= Current.Known.ToDictionary(kv => kv.Key, kv => kv.Value.Select(Unpack).ToList(), StringComparer.OrdinalIgnoreCase));

    public IReadOnlyList<float[]> IgnoredVectors => _ignored ?? Build(() => _ignored ??= Current.Ignored.Select(Unpack).ToList());

    private T Build<T>(Func<T> make) { lock (_sync) return make(); }
    private void Change(Action change) { lock (_sync) change(); Save(); }

    public int CountFor(string name) => Current.Known.TryGetValue(name, out var l) ? l.Count : 0;

    public void Add(string name, IEnumerable<float[]> prints) => Change(() =>
    {
        if (!Current.Known.TryGetValue(name, out var list)) Current.Known[name] = list = new List<string>();
        list.AddRange(prints.Select(Models.VoiceSpeaker.Pack));
        if (list.Count > Keep) list.RemoveRange(0, list.Count - Keep);
        _vecs = null;
        _answered = null;
    });

    public void Remove(string name, IEnumerable<float[]> prints) => Change(() =>
    {
        if (!Current.Known.TryGetValue(name, out var list)) return;
        foreach (var p in prints.Select(Models.VoiceSpeaker.Pack)) list.Remove(p);
        if (list.Count == 0) Current.Known.Remove(name);
        _vecs = null;
        _answered = null;
    });

    public void Ignore(IEnumerable<float[]> prints, bool undo = false) => Change(() =>
    {
        foreach (var p in prints.Select(Models.VoiceSpeaker.Pack))
            if (undo) Current.Ignored.Remove(p); else Current.Ignored.Add(p);
        if (Current.Ignored.Count > 200) Current.Ignored.RemoveRange(0, Current.Ignored.Count - 200);
        _ignored = null;
        _answered = null;
    });

    /// <summary>Renames a friend's voice along with the friend.</summary>
    public void Rename(string from, string to) => Change(() =>
    {
        if (!Current.Known.Remove(from, out var list)) return;
        if (Current.Known.TryGetValue(to, out var have)) have.AddRange(list); else Current.Known[to] = list;
        _vecs = null;
        var friends = Current.Game.Friends;
        if (friends.RemoveAll(f => string.Equals(f, from, StringComparison.OrdinalIgnoreCase)) > 0 && !friends.Contains(to, StringComparer.OrdinalIgnoreCase)) friends.Add(to);
    });

    private HashSet<string>? _answered;

    /// <summary>A short, stable name for one voiceprint.</summary>
    private static string KeyOf(float[] print) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(print.AsSpan())))[..16];

    /// <summary>Was this voice (this very bit of a clip) answered in the game before?</summary>
    public bool WasAnswered(float[] print)
    {
        // Answers from before this list existed count too: their voiceprints are the ones kept per friend, and the ignored ones.
        var set = _answered ?? Build(() => _answered ??= Current.Answered
            .Concat(Current.Known.Values.SelectMany(l => l).Concat(Current.Ignored).Select(b => KeyOf(Unpack(b)))).ToHashSet());
        return set.Contains(KeyOf(print));
    }

    /// <summary>Remembers voices answered in the game (or forgets them again, for Undo).</summary>
    public void MarkAnswered(IEnumerable<float[]> prints, bool undo = false) => Change(() =>
    {
        foreach (var k in prints.Select(KeyOf))
            if (undo) Current.Answered.Remove(k); else if (!Current.Answered.Contains(k)) Current.Answered.Add(k);
        if (Current.Answered.Count > 20000) Current.Answered.RemoveRange(0, Current.Answered.Count - 20000);
        _answered = null;
    });

    /// <summary>The game's progress changed (or was taken back with Undo).</summary>
    public void SetGame(VoiceGameStats stats) => Change(() => Current.Game = stats);

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
