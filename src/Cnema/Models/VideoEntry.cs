namespace Cnema.Models;

/// <summary>One video file in the library. Persisted in library.json as the scan cache.</summary>
public sealed class VideoEntry
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public long DurationMs { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public double FrameRate { get; set; }
    public string? ThumbPath { get; set; }
    public long LastPositionMs { get; set; }
    public DateTime? LastPlayedUtc { get; set; }
    public List<string> Tags { get; set; } = new();
    public bool Favorite { get; set; }
    /// <summary>Moments marked with the hotkey during a session recording, in ms from the start.</summary>
    public List<long> Bookmarks { get; set; } = new();
    /// <summary>Where the user picked the thumbnail from, in ms; 0 = chosen automatically.</summary>
    public long ThumbAtMs { get; set; }
    /// <summary>A line of text the user wrote about the video; search finds it.</summary>
    public string? Note { get; set; }
    /// <summary>Friends who were there when this was recorded ("Who was there"); search finds them.</summary>
    public List<string> People { get; set; } = new();
    /// <summary>The name shown in Savefile (Smart titles, or one typed in); null shows the file name.</summary>
    public string? Title { get; set; }
    /// <summary>The title was given by Name new clips by themselves, so it may be improved once the clip has been listened to.</summary>
    public bool TitleAuto { get; set; }
    /// <summary>What the smart features worked out about the video; null until it has been looked at.</summary>
    public SmartInfo? Smart { get; set; }
    /// <summary>What's said in the video (voice search and captions); null until it has been listened to.</summary>
    public SpeechInfo? Speech { get; set; }
    /// <summary>What it looks like (visual search, similar videos); null until it has been looked at.</summary>
    public VisualInfo? Visual { get; set; }
    /// <summary>Laughter and hype (cheering, shouting); null until it has been listened to.</summary>
    public SoundInfo? Sound { get; set; }
    /// <summary>The voices heard in it, grouped by who's speaking; null until it has been listened to.</summary>
    public VoiceInfo? Voices { get; set; }
    /// <summary>Text read off the screen (kill feeds, scoreboards, banners); null until it has been read.</summary>
    public ScreenInfo? Screen { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Things the user set (as opposed to what a scan can rediscover).</summary>
    public void CopyUserDataFrom(VideoEntry other)
    {
        LastPositionMs = other.LastPositionMs;
        LastPlayedUtc = other.LastPlayedUtc;
        Tags = other.Tags.ToList();
        Favorite = other.Favorite;
        Bookmarks = other.Bookmarks.ToList();
        ThumbAtMs = other.ThumbAtMs;
        Note = other.Note;
        People = other.People.ToList();
        Title = other.Title;
    }

    /// <summary>
    /// What the smart features found, for the same file under another name (same size and date):
    /// listening again would cost minutes for nothing.
    /// </summary>
    public void CopyAnalysisFrom(VideoEntry other)
    {
        if (other.Size != Size || other.ModifiedUtc != ModifiedUtc) return;
        Smart ??= other.Smart;
        Speech ??= other.Speech;
        Visual ??= other.Visual;
        Sound ??= other.Sound;
        Voices ??= other.Voices;
    }
}

/// <summary>Found by looking at (and listening to) a video, on this computer. Redone when the file changes.</summary>
public sealed class SmartInfo
{
    /// <summary>The analysis version; a higher one in a newer Savefile means "look again".</summary>
    public int Version { get; set; }
    /// <summary>How eventful the video is, 0 (quiet, still) to 1 (loud spikes, lots going on).</summary>
    public double Excitement { get; set; }
    /// <summary>The middle of its best few seconds, in ms; -1 when nothing stands out.</summary>
    public long BestMs { get; set; } = -1;
    /// <summary>Sudden loud moments (shouts, gunfire, a crowd).</summary>
    public int Spikes { get; set; }
    /// <summary>How much the picture changes, 0 to 1.</summary>
    public double Motion { get; set; }
    /// <summary>A good frame for the thumbnail (not black, not a loading screen), in ms; 0 when none was picked.</summary>
    public long ThumbMs { get; set; }
    /// <summary>Dead air at the start to trim away, ending here (ms); 0 when there's none.</summary>
    public long TrimStartMs { get; set; }
    /// <summary>Dead air at the end to trim away, starting here (ms); 0 when there's none.</summary>
    public long TrimEndMs { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool HasTrimSuggestion => TrimStartMs > 0 || TrimEndMs > 0;
}

/// <summary>The different voices in a video: each one's voiceprint and where it talks (from the transcript's lines).</summary>
public sealed class VoiceInfo
{
    public int Version { get; set; }
    public List<VoiceSpeaker> Speakers { get; set; } = new();
    /// <summary>Friends who were added to Who was there because their voice was recognised.</summary>
    public List<string> Added { get; set; } = new();
}

public sealed class VoiceSpeaker
{
    /// <summary>The average voiceprint (256 numbers, length 1), as base64 floats to keep the library file small.</summary>
    public string Print { get; set; } = "";
    /// <summary>How long this voice talks in the video, in milliseconds.</summary>
    public long Ms { get; set; }
    /// <summary>Where it talks: start and end (ms) of its longest lines, at most a dozen.</summary>
    public List<long[]> Lines { get; set; } = new();
    /// <summary>The best line to play back for "Who's that?".</summary>
    public string Quote { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore] private float[]? _vec;
    [System.Text.Json.Serialization.JsonIgnore]
    public float[] Vec => _vec ??= Print.Length == 0 ? Array.Empty<float>() : System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(Convert.FromBase64String(Print)).ToArray();
    public static string Pack(float[] v) => Convert.ToBase64String(System.Runtime.InteropServices.MemoryMarshal.AsBytes(v.AsSpan()));
}

/// <summary>Where people laugh and where it gets loud with excitement, heard on this computer by the sound model.</summary>
public sealed class SoundInfo
{
    public int Version { get; set; }
    /// <summary>Moments (every 0.48 s) that sound like laughter, with how sure the model is.</summary>
    public List<SoundMoment> Laugh { get; set; } = new();
    /// <summary>Moments of cheering, shouting, screaming or a whoop.</summary>
    public List<SoundMoment> Hype { get; set; } = new();

    /// <summary>Roughly how many seconds of laughter (each moment covers about half a second).</summary>
    [System.Text.Json.Serialization.JsonIgnore] public double LaughSeconds => Laugh.Count * 0.48;
    [System.Text.Json.Serialization.JsonIgnore] public double HypeSeconds => Hype.Count * 0.48;
}

public sealed class SoundMoment
{
    public long At { get; set; }
    public float Score { get; set; }
}

/// <summary>The words spoken in a video, written down on this computer by the speech model.</summary>
public sealed class SpeechInfo
{
    public int Version { get; set; }
    /// <summary>Two-letter code of the language heard most ("en"), or null when nobody spoke.</summary>
    public string? Language { get; set; }
    public List<SpeechLine> Lines { get; set; } = new();
}

/// <summary>One line of speech; times in milliseconds from the start of the video.</summary>
public sealed class SpeechLine
{
    public long Start { get; set; }
    public long End { get; set; }
    public string Text { get; set; } = "";
}

/// <summary>
/// What a video looks like to the visual model: a vector for each of a few frames, kept as 16-bit
/// floats in Base64 so the library file stays small (about 1 KB a frame).
/// </summary>
public sealed class VisualInfo
{
    public int Version { get; set; }
    public long[] At { get; set; } = Array.Empty<long>();
    public string Vectors { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore] private float[][]? _frames;
    [System.Text.Json.Serialization.JsonIgnore] private float[]? _mean;

    /// <summary>One unit vector per frame.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float[][] Frames => _frames ??= Decode();

    /// <summary>The whole video in one unit vector (the frames averaged).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public float[] Mean => _mean ??= MeanOf(Frames);

    public static VisualInfo From(List<long> at, List<float[]> frames)
    {
        var bytes = new byte[frames.Sum(f => f.Length) * 2];
        var k = 0;
        foreach (var f in frames)
            foreach (var x in f)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(k), (Half)x);
                k += 2;
            }
        return new VisualInfo { Version = Cnema.Services.VisualService.Version, At = at.ToArray(), Vectors = Convert.ToBase64String(bytes), _frames = frames.ToArray() };
    }

    private float[][] Decode()
    {
        try
        {
            var bytes = Convert.FromBase64String(Vectors);
            var dim = Cnema.Services.VisualService.Dim;
            var n = bytes.Length / 2 / dim;
            var frames = new float[n][];
            for (var i = 0; i < n; i++)
            {
                frames[i] = new float[dim];
                for (var d = 0; d < dim; d++) frames[i][d] = (float)BitConverter.ToHalf(bytes, (i * dim + d) * 2);
            }
            return frames;
        }
        catch { return Array.Empty<float[]>(); }
    }

    private static float[] MeanOf(float[][] frames)
    {
        var dim = Cnema.Services.VisualService.Dim;
        var m = new float[dim];
        foreach (var f in frames) for (var d = 0; d < dim && d < f.Length; d++) m[d] += f[d];
        return Cnema.Services.VisualService.Normalize(m);
    }

    public static float Dot(float[] a, float[] b)
    {
        var s = 0f;
        for (var i = 0; i < a.Length && i < b.Length; i++) s += a[i] * b[i];
        return s;
    }
}

public sealed class LibraryCacheFile
{
    public Dictionary<string, VideoEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A hand-made group of videos from any folders, like a playlist.</summary>
public sealed class VideoCollection
{
    public string Name { get; set; } = "";
    public List<string> Paths { get; set; } = new();
}

public sealed class CollectionsFile
{
    public List<VideoCollection> Collections { get; set; } = new();
}

/// <summary>Text read off the screen in a few frames of the video, on this computer.</summary>
public sealed class ScreenInfo
{
    public int Version { get; set; }
    public List<ScreenLine> Lines { get; set; } = new();
}

public sealed class ScreenLine
{
    /// <summary>The frame it was read in (ms).</summary>
    public long At { get; set; }
    public string Text { get; set; } = "";
}
