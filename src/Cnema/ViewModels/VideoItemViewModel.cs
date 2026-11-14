using Avalonia.Media.Imaging;
using Cnema.Controls;
using Cnema.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cnema.ViewModels;

public sealed partial class VideoItemViewModel : ObservableObject
{
    public VideoEntry Entry { get; private set; }

    [ObservableProperty] private Bitmap? _thumbnail;
    private int _thumbRefs;

    public VideoItemViewModel(VideoEntry entry) => Entry = entry;

    public string Title => Path.GetFileNameWithoutExtension(Entry.Path);
    public string Extension => Path.GetExtension(Entry.Path).TrimStart('.').ToUpperInvariant();
    public string DurationText => Entry.DurationMs > 0 ? TrimTimeline.FormatTime(Entry.DurationMs) : "";
    public bool HasDuration => Entry.DurationMs > 0;
    public string Folder => Path.GetDirectoryName(Entry.Path) ?? "";

    public string ResolutionBadge => Entry.Height switch
    {
        >= 2000 => "4K",
        >= 1400 => "1440p",
        >= 1000 => "1080p",
        >= 700 => "720p",
        > 0 => $"{Entry.Height}p",
        _ => "",
    };
    public bool HasResolution => Entry.Height > 0;
    public bool IsHighRes => Entry.Height >= 2000;

    public string Details
    {
        get
        {
            var parts = new List<string>();
            if (Entry.Width > 0) parts.Add($"{Entry.Width}×{Entry.Height}");
            if (Entry.FrameRate > 0) parts.Add($"{Entry.FrameRate:0.##} fps");
            if (!string.IsNullOrEmpty(Entry.VideoCodec)) parts.Add(Entry.VideoCodec!.ToUpperInvariant());
            parts.Add(FormatSize(Entry.Size));
            return string.Join("  ·  ", parts);
        }
    }

    public bool IsFavorite => Entry.Favorite;
    public IReadOnlyList<string> Tags => Entry.Tags;
    public bool HasTags => Entry.Tags.Count > 0;

    /// <summary>Re-read everything from the entry after an edit (rename, tags, favourite).</summary>
    public void RefreshAll() => OnPropertyChanged(string.Empty);

    /// <summary>0..1 watched fraction for the resume bar on the card.</summary>
    public double Progress => Entry.DurationMs > 0 ? Math.Clamp((double)Entry.LastPositionMs / Entry.DurationMs, 0, 1) : 0;
    public bool HasProgress => Progress is > 0.01 and < 0.97;

    public void Update(VideoEntry e)
    {
        var thumbChanged = e.ThumbPath != Entry.ThumbPath;
        Entry = e;
        OnPropertyChanged(string.Empty);
        if (thumbChanged && _thumbRefs > 0) _ = LoadThumbnailCoreAsync();
    }

    public void RefreshProgress()
    {
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(HasProgress));
    }

    /// <summary>Called when the card scrolls into view (virtualized grid).</summary>
    public void AcquireThumbnail()
    {
        if (++_thumbRefs == 1 && Thumbnail is null) _ = LoadThumbnailCoreAsync();
    }

    /// <summary>Called when the card leaves the view; frees decoded pixels.</summary>
    public void ReleaseThumbnail()
    {
        if (--_thumbRefs > 0) return;
        _thumbRefs = 0;
        var old = Thumbnail;
        Thumbnail = null;
        old?.Dispose();
    }

    private async Task LoadThumbnailCoreAsync()
    {
        var path = Entry.ThumbPath;
        if (path is null || !File.Exists(path)) return;
        try
        {
            var bmp = await Task.Run(() =>
            {
                using var fs = File.OpenRead(path);
                return Bitmap.DecodeToWidth(fs, 400);
            });
            if (_thumbRefs > 0 && Entry.ThumbPath == path) { Thumbnail?.Dispose(); Thumbnail = bmp; }
            else bmp.Dispose();
        }
        catch { /* corrupt thumbnail; card shows placeholder */ }
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}
