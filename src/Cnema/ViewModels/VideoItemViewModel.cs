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

    // ---------- watched ----------
    /// <summary>The "Unwatched" feature; off, no dot is shown.</summary>
    public static bool UnwatchedBadges { get; set; } = true;
    public bool IsUnwatched => Entry.LastPlayedUtc is null;
    public bool ShowUnwatched => UnwatchedBadges && IsUnwatched;
    public string WatchedMenuText => IsUnwatched ? T("Mark as watched") : T("Mark as unwatched");
    public bool HasCustomThumbnail => Entry.ThumbAtMs > 0;

    // ---------- note ----------
    /// <summary>The "Notes" feature; off, notes are kept but not shown.</summary>
    public static bool NotesEnabled { get; set; } = true;
    public bool HasNote => NotesEnabled && !string.IsNullOrWhiteSpace(Entry.Note);
    public string Note => Entry.Note ?? "";
    // ---------- who was there ----------
    /// <summary>The "Who was there" feature; off, names are kept but not shown.</summary>
    public static bool PeopleEnabled { get; set; } = true;
    public bool HasPeople => PeopleEnabled && Entry.People.Count > 0;
    /// <summary>"with Alex, Sam and Mira".</summary>
    public string PeopleText => HasPeople ? F("with {0}", JoinNames(Entry.People)) : "";

    public static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        _ => F("{0} and {1}", string.Join(", ", names.Take(names.Count - 1)), names[^1]),
    };

    /// <summary>When it was recorded, said the way a friend would: "last night", "3 weeks ago", "2 years ago".</summary>
    public string AgoText => Ago(Entry.ModifiedUtc.ToLocalTime(), DateTime.Now);

    public static string Ago(DateTime when, DateTime now)
    {
        var days = (now.Date - when.Date).Days;
        if (days <= 0) return when.Hour >= 18 || when.Hour < 4 ? T("tonight") : T("today");
        if (days == 1) return when.Hour >= 18 || when.Hour < 4 ? T("last night") : T("yesterday");
        if (days < 7) return F("{0} days ago", days);
        if (days < 30) return P(days / 7, "{0} week ago", "{0} weeks ago");
        if (days < 365) return P(Math.Max(1, days / 30), "{0} month ago", "{0} months ago");
        return P(days / 365, "{0} year ago", "{0} years ago");
    }

    /// <summary>Second line on a "Worth a rewatch" card: who was there and when, or just when.</summary>
    /// <summary>Under the title in the Quick player: when it was, and who was there.</summary>
    public string PlayerLine
    {
        get
        {
            var date = Entry.ModifiedUtc.ToLocalTime();
            var when = date.ToString(date.Year == DateTime.Now.Year ? "dddd d MMMM" : "d MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture);
            return HasPeople ? $"{when} · {PeopleText}" : when;
        }
    }

    public string MemoryLine => HasPeople ? $"{PeopleText} · {AgoText}" : AgoText;

    /// <summary>The card's second line: the note, then who was there, otherwise resolution, codec and size.</summary>
    public string Subtitle => HasNote ? Note : HasPeople ? PeopleText : Details;
    public string? SubtitleTip => HasNote || HasPeople ? Details : null;

    // ---------- selection ----------
    /// <summary>True while the library is in select mode: cards show a tick box and a click selects.</summary>
    public static bool Selecting { get; set; }
    public bool ShowSelectBox => Selecting;
    [ObservableProperty] private bool _isSelected;
    public void RefreshSelection() => OnPropertyChanged(nameof(ShowSelectBox));

    /// <summary>The thumbnail file changed in place (a picked frame): decode it again.</summary>
    public void ReloadThumbnail()
    {
        OnPropertyChanged(nameof(HasCustomThumbnail));
        if (_thumbRefs > 0) _ = LoadThumbnailCoreAsync();
    }

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

    // ---------- hover scrub ----------
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasScrub))] private Avalonia.Media.IImage? _scrubFrame;
    [ObservableProperty] private double _scrubPosition;
    public bool HasScrub => ScrubFrame is not null;
    private Bitmap? _strip;
    private int _scrubToken;
    private int _scrubIndex = -1;

    /// <summary>Pointer came onto the card: after a short pause, load (or make) the filmstrip.</summary>
    /// <summary>The "Hover preview" feature; off, cards keep their still thumbnail.</summary>
    public static bool HoverPreview { get; set; } = true;

    public async void BeginScrub()
    {
        if (_strip is not null || !HoverPreview) return;
        var token = ++_scrubToken;
        try
        {
            await Task.Delay(220);
            if (token != _scrubToken) return;
            var path = await Services.ThumbnailService.EnsureStripAsync(Entry, CancellationToken.None);
            if (path is null || token != _scrubToken) return;
            var bmp = await Task.Run(() => new Bitmap(path));
            if (token != _scrubToken) { bmp.Dispose(); return; }
            _strip = bmp;
            ShowScrubFrame();
        }
        catch { /* no filmstrip; the card keeps its still thumbnail */ }
    }

    /// <summary>Pointer moved to this fraction (0..1) of the card's width.</summary>
    public void Scrub(double fraction)
    {
        ScrubPosition = Math.Clamp(fraction, 0, 1);
        if (_strip is not null) ShowScrubFrame();
    }

    private void ShowScrubFrame()
    {
        if (_strip is not { } strip) return;
        var n = Services.ThumbnailService.StripFrames;
        var index = Math.Clamp((int)(ScrubPosition * n), 0, n - 1);
        if (index == _scrubIndex && ScrubFrame is not null) return;
        _scrubIndex = index;
        var w = strip.PixelSize.Width / n;
        ScrubFrame = new CroppedBitmap(strip, new Avalonia.PixelRect(index * w, 0, w, strip.PixelSize.Height));
    }

    public void EndScrub()
    {
        _scrubToken++;
        _scrubIndex = -1;
        ScrubFrame = null;
        _strip?.Dispose();
        _strip = null;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}
