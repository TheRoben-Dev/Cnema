using System.Globalization;
using System.Text;

namespace Cnema.Services;

public enum CaptionPosition { Top, Middle, Bottom }
public enum CaptionSize { Small, Medium, Large }
public enum CaptionStyle { Outline, Box }
public enum Corner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>Text over the video from <see cref="Start"/> to <see cref="End"/>, in source milliseconds.</summary>
public sealed record CaptionSpec(long Start, long End, string Text, CaptionPosition Position, CaptionSize Size, CaptionStyle Style);

/// <summary>A part of the video played at another speed (source milliseconds). Ramp eases into and out of it.</summary>
public sealed record SpeedSpec(long Start, long End, double Rate, bool Ramp);

/// <summary>An image in a corner; Width is a fraction of the video's width.</summary>
public sealed record WatermarkSpec(string Path, Corner Corner, double Width, double Opacity);

/// <summary>
/// Loudness and voice tools. VoiceTrack is the index of the voice among the source's audio streams,
/// or -1 when the file has no separate voice track.
/// </summary>
public sealed record AudioSpec(bool Normalize, int VoiceTrack, double VoiceBoostDb, bool Duck)
{
    public static readonly AudioSpec None = new(false, -1, 0, false);
    public bool HasVoiceTools => VoiceTrack >= 0 && (VoiceBoostDb != 0 || Duck);
    public bool IsEmpty => !Normalize && !HasVoiceTools;
}

/// <summary>Everything the Effects panel adds to an export. Effects always mean re-encoding.</summary>
public sealed record EffectsSpec(
    IReadOnlyList<CaptionSpec> Captions,
    IReadOnlyList<SpeedSpec> Speed,
    AudioSpec Audio,
    WatermarkSpec? Watermark,
    string? Intro,
    string? Outro)
{
    public static readonly EffectsSpec None =
        new(Array.Empty<CaptionSpec>(), Array.Empty<SpeedSpec>(), AudioSpec.None, null, null, null);

    public bool IsEmpty => Captions.Count == 0 && Speed.Count == 0 && Audio.IsEmpty &&
                           Watermark is null && Intro is null && Outro is null;
}

/// <summary>A stretch of the source played at one speed. Times in milliseconds.</summary>
public readonly record struct Piece(long Start, long End, double Rate)
{
    public long Length => End - Start;
    public double OutputLength => Length / Rate;
}

/// <summary>
/// How source time becomes output time: the kept parts, in order, each split where the speed changes.
/// </summary>
public sealed class TimeMap
{
    public const long RampMs = 600;
    private const int RampSteps = 6;
    private const long MinPieceMs = 40;

    public IReadOnlyList<Piece> Pieces { get; }
    public double OutputMs { get; }

    private TimeMap(List<Piece> pieces)
    {
        Pieces = pieces;
        OutputMs = pieces.Sum(p => p.OutputLength);
    }

    public static TimeMap Build(IReadOnlyList<(long In, long Out)> parts, IReadOnlyList<SpeedSpec> speed)
    {
        // Speed sections become plain (start, end, rate) steps; a ramp is a staircase at each edge.
        var steps = new List<(long Start, long End, double Rate)>();
        long lastEnd = long.MinValue;
        foreach (var s in speed.Where(s => s.End - s.Start >= 100 && s.Rate > 0).OrderBy(s => s.Start))
        {
            var start = Math.Max(s.Start, lastEnd);
            if (s.End - start < 100) continue;
            lastEnd = s.End;
            var len = s.End - start;
            var ramp = s.Ramp ? Math.Min(RampMs, len / 4) : 0;
            if (ramp < MinPieceMs * 2)
            {
                steps.Add((start, s.End, s.Rate));
                continue;
            }
            var n = (int)Math.Clamp(ramp / MinPieceMs, 2, RampSteps);
            for (var k = 0; k < n; k++)
            {
                var r = Lerp(1, s.Rate, (k + 1.0) / (n + 1));
                steps.Add((start + ramp * k / n, start + ramp * (k + 1) / n, r));
            }
            steps.Add((start + ramp, s.End - ramp, s.Rate));
            for (var k = 0; k < n; k++)
            {
                var r = Lerp(s.Rate, 1, (k + 1.0) / (n + 1));
                steps.Add((s.End - ramp + ramp * k / n, s.End - ramp + ramp * (k + 1) / n, r));
            }
        }

        var pieces = new List<Piece>();
        foreach (var (pIn, pOut) in parts)
        {
            if (pOut - pIn < MinPieceMs) continue;
            var cuts = new SortedSet<long> { pIn, pOut };
            foreach (var st in steps)
            {
                if (st.Start > pIn && st.Start < pOut) cuts.Add(st.Start);
                if (st.End > pIn && st.End < pOut) cuts.Add(st.End);
            }
            var list = cuts.ToList();
            for (var i = 0; i + 1 < list.Count; i++)
            {
                var (a, b) = (list[i], list[i + 1]);
                var mid = a + (b - a) / 2;
                var rate = steps.Where(st => mid >= st.Start && mid < st.End).Select(st => st.Rate).DefaultIfEmpty(1).First();
                var prev = pieces.Count > 0 ? pieces[^1] : (Piece?)null;
                if (prev is { } p && p.End == a && (Math.Abs(p.Rate - rate) < 0.001 || b - a < MinPieceMs))
                    pieces[^1] = p with { End = b };
                else
                    pieces.Add(new Piece(a, b, rate));
            }
        }
        // A sliver too short to hold a frame is joined to the piece before it.
        for (var i = pieces.Count - 1; i > 0; i--)
            if (pieces[i].Length < MinPieceMs && pieces[i - 1].End == pieces[i].Start)
            {
                pieces[i - 1] = pieces[i - 1] with { End = pieces[i].End };
                pieces.RemoveAt(i);
            }
        return new TimeMap(pieces);
    }

    private static double Lerp(double a, double b, double t) => Math.Round(a + (b - a) * t, 3);

    /// <summary>Output time of a source moment; one in a cut-out gap maps to where the next piece starts.</summary>
    public double? MapForward(long sourceMs)
    {
        double offset = 0;
        foreach (var p in Pieces)
        {
            if (sourceMs < p.Start) return offset;
            if (sourceMs <= p.End) return offset + (sourceMs - p.Start) / p.Rate;
            offset += p.OutputLength;
        }
        return null;
    }

    /// <summary>The output stretches a source range ends up in (more than one when the range spans a cut).</summary>
    public IReadOnlyList<(double Start, double End)> MapRange(long start, long end)
    {
        var result = new List<(double Start, double End)>();
        double offset = 0;
        foreach (var p in Pieces)
        {
            var a = Math.Max(start, p.Start);
            var b = Math.Min(end, p.End);
            if (b > a)
            {
                var s = offset + (a - p.Start) / p.Rate;
                var e = offset + (b - p.Start) / p.Rate;
                if (result.Count > 0 && Math.Abs(result[^1].End - s) < 1) result[^1] = (result[^1].Start, e);
                else result.Add((s, e));
            }
            offset += p.OutputLength;
        }
        return result;
    }

    /// <summary>The speed at a source moment (1 outside every speed section).</summary>
    public static double RateAt(IReadOnlyList<SpeedSpec> speed, double ms)
    {
        foreach (var s in speed)
        {
            if (ms < s.Start || ms >= s.End) continue;
            var ramp = s.Ramp ? Math.Min(RampMs, (s.End - s.Start) / 4) : 0;
            if (ramp > 0 && ms < s.Start + ramp) return Lerp(1, s.Rate, (ms - s.Start) / ramp);
            if (ramp > 0 && ms > s.End - ramp) return Lerp(s.Rate, 1, (ms - (s.End - ramp)) / ramp);
            return s.Rate;
        }
        return 1;
    }
}

/// <summary>Writes captions as an ASS subtitle file, which ffmpeg burns in with libass.</summary>
public static class CaptionWriter
{
    public const string FontFamily = "Inter";

    /// <summary>Font size as a share of the picture height.</summary>
    public static double SizeFactor(CaptionSize s) => s switch
    {
        CaptionSize.Small => 0.045,
        CaptionSize.Large => 0.09,
        _ => 0.065,
    };

    public static string Write(IEnumerable<CaptionSpec> captions, TimeMap map, int width, int height)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine($"PlayResX: {width}");
        sb.AppendLine($"PlayResY: {height}");
        sb.AppendLine("WrapStyle: 0");
        sb.AppendLine("ScaledBorderAndShadow: yes");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        var list = captions.ToList();
        var marginH = (int)Math.Round(width * 0.05);
        var marginV = (int)Math.Round(height * 0.06);
        for (var i = 0; i < list.Count; i++)
        {
            var c = list[i];
            var size = Math.Max(8, height * SizeFactor(c.Size));
            var align = c.Position switch { CaptionPosition.Top => 8, CaptionPosition.Middle => 5, _ => 2 };
            // Outline: white text, black edge and a soft shadow. Box: white text on a translucent dark box.
            var (borderStyle, outline, shadow, outlineColour) = c.Style == CaptionStyle.Box
                ? (3, size * 0.22, 0.0, "&H4C000000")
                : (1, size * 0.07, size * 0.035, "&H00000000");
            sb.Append(inv, $"Style: C{i},{FontFamily},{size:0.#},&H00FFFFFF,&H00FFFFFF,{outlineColour},&H80000000,-1,0,0,0,100,100,0,0,");
            sb.AppendLine(inv, $"{borderStyle},{outline:0.##},{shadow:0.##},{align},{marginH},{marginH},{marginV},1");
        }
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        for (var i = 0; i < list.Count; i++)
        {
            var c = list[i];
            var text = Escape(c.Text);
            if (text.Length == 0) continue;
            foreach (var (s, e) in map.MapRange(c.Start, c.End))
                sb.AppendLine(inv, $"Dialogue: 0,{Time(s)},{Time(e)},C{i},,0,0,0,,{text}");
        }
        return sb.ToString();
    }

    /// <summary>Keeps typed text literal: braces would start override tags, a backslash an escape.</summary>
    internal static string Escape(string text)
    {
        var t = text.Replace("\r\n", "\n").Trim();
        t = t.Replace("\\", "\\⁠").Replace("{", "\\{").Replace("}", "\\}");
        return t.Replace("\n", "\\N");
    }

    private static string Time(double ms)
    {
        var cs = (long)Math.Round(Math.Max(0, ms) / 10);
        return string.Create(CultureInfo.InvariantCulture,
            $"{cs / 360000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}");
    }
}
