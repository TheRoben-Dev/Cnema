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

    public string FileName => System.IO.Path.GetFileName(Path);
}

public sealed class LibraryCacheFile
{
    public Dictionary<string, VideoEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
