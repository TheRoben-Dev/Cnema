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
    /// <summary>What the smart features worked out about the video; null until it has been looked at.</summary>
    public SmartInfo? Smart { get; set; }

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
