namespace Cinema.Models;

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

    [System.Text.Json.Serialization.JsonIgnore]
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Things the user set (as opposed to what a scan can rediscover).</summary>
    public void CopyUserDataFrom(VideoEntry other)
    {
        LastPositionMs = other.LastPositionMs;
        LastPlayedUtc = other.LastPlayedUtc;
        Tags = other.Tags.ToList();
        Favorite = other.Favorite;
    }
}

public sealed class LibraryCacheFile
{
    public Dictionary<string, VideoEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
