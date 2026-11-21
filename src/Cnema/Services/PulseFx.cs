using System.Globalization;
using System.Text;

namespace Cnema.Services;

/// <summary>
/// The "nightclub pulse" for Wrapped and Auto montage, as plain ffmpeg filters: the picture punches in on the
/// song's beats (harder on the first beat of a bar, hardest on a drop) and eases back, a light flash on bars and
/// drops, slow moves that ease in and out instead of crawling at one speed, and a soft glow on the bright parts.
/// Everything is timed from the beats Savefile found in the person's own music, so it moves with the track.
/// </summary>
public static class PulseFx
{
    /// <summary>The Settings toggle (Features.BeatFx): off, the videos look as they did before.</summary>
    public static bool Enabled { get; set; } = true;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A hit of the music inside a shot: seconds from the shot's start, and how hard (0.35 a beat, 1 a bar, 2+ a drop).</summary>
    public readonly record struct Hit(double At, double Power, bool Bar);

    /// <summary>The beats between <paramref name="from"/> and <paramref name="to"/> (timeline seconds), relative to <paramref name="from"/>.</summary>
    public static List<Hit> Hits(IReadOnlyList<double> beats, IReadOnlyList<double> downbeats, IReadOnlyList<double> drops,
        IReadOnlyList<float>? energy, double from, double to)
    {
        var hits = new List<Hit>();
        for (var i = 0; i < beats.Count; i++)
        {
            var b = beats[i];
            if (b < from - 0.25 || b >= to) continue;
            var e = energy is not null && i < energy.Count ? energy[i] : 0.6f;
            var bar = downbeats.Any(d => Math.Abs(d - b) < 0.02);
            var drop = drops.Any(d => Math.Abs(d - b) < 0.02);
            // Quiet stretches of the song barely move the picture; the loud ones hit.
            var power = (drop ? 2.4 : bar ? 1 : 0.35) * (0.45 + 0.75 * e);
            hits.Add(new Hit(b - from, power, bar || drop));
        }
        // An expression per frame stays cheap: at most the 24 strongest.
        return hits.Count <= 24 ? hits : hits.OrderByDescending(h => h.Power).Take(24).OrderBy(h => h.At).ToList();
    }

    /// <summary>Σ power · e^(−rate·(t−at)) for the hits already reached, in ffmpeg expression syntax over <paramref name="t"/>.</summary>
    private static string Kicks(IEnumerable<Hit> hits, string t, double scale, double rate)
    {
        var sb = new StringBuilder();
        foreach (var h in hits)
        {
            var a = h.Power * scale;
            if (a < 0.0005) continue;
            if (sb.Length > 0) sb.Append('+');
            var at = h.At.ToString("0.###", Inv);
            sb.Append(Inv, $"{a:0.#####}*gte({t},{at})*exp(-{rate:0.##}*({t}-{at}))");
        }
        return sb.Length == 0 ? "0" : sb.ToString();
    }

    /// <summary>
    /// The move and the punches, drawn from a frame twice the size so they stay smooth: <paramref name="z0"/> to
    /// <paramref name="z1"/> over the shot (eased), plus a punch on each hit. <paramref name="punch"/> scales the hits
    /// (smaller on moments where people talk). Empty when nothing moves.
    /// </summary>
    public static string Move(int width, int height, int fps, int frames, double z0, double z1, bool quickSettle, IReadOnlyList<Hit> hits, double punch)
    {
        var moving = Math.Abs(z1 - z0) > 0.0005;
        if (!moving && (hits.Count == 0 || punch <= 0)) return "";
        var n = Math.Max(1, quickSettle ? Math.Min(frames, 18) : frames);
        var x = $"min(1,on/{n})";
        // Ease in and out for a slow push (no start/stop jolt); a quick settle decelerates into place.
        var ease = quickSettle ? $"(1-pow(1-{x},3))" : $"((1-cos(PI*{x}))/2)";
        var t = $"(on/{fps})";
        var z = $"{F(z0)}+({F(z1 - z0)})*{ease}+{Kicks(hits, t, 0.03 * punch, 9)}";
        return $",scale={width * 2}:{height * 2}:flags=bicubic,zoompan=z='{z}':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d=1:s={width}x{height}:fps={fps}";
    }

    /// <summary>A light flash on the first beat of each bar and a bright one on a drop, with the colour lifting a little with it.</summary>
    public static string Flash(IReadOnlyList<Hit> hits, double strength = 1)
    {
        var bars = hits.Where(h => h.Bar).ToList();
        if (bars.Count == 0 || strength <= 0) return "";
        var light = Kicks(bars, "t", 0.035 * strength, 13);
        var color = Kicks(bars, "t", 0.10 * strength, 7);
        return $",eq=eval=frame:brightness='{light}':saturation='1.06+{color}':contrast='1.02'";
    }

    /// <summary>
    /// A soft glow on what's bright (white text, muzzle flashes, the sky), and a slight vignette for depth: the bright
    /// parts are blurred at a quarter size and added back. Filtergraph lines from <paramref name="input"/> to <paramref name="output"/>.
    /// </summary>
    public static IEnumerable<string> Glow(string input, string output, int width, int height, string tag, double amount = 0.55)
    {
        var w = Math.Max(2, width / 4 / 2 * 2);
        var h = Math.Max(2, height / 4 / 2 * 2);
        yield return $"{input}format=gbrp,split[{tag}a][{tag}b]";
        yield return $"[{tag}b]scale={w}:{h},colorlevels=rimin=0.7:gimin=0.7:bimin=0.7,gblur=sigma=7,scale={width}:{height}:flags=bilinear[{tag}c]";
        yield return $"[{tag}a][{tag}c]blend=all_mode=screen:all_opacity={F(amount)},vignette=angle=PI/6:eval=init,format=yuv420p{output}";
    }

    private static string F(double v) => v.ToString("0.#####", Inv);

    // ---------- the edge glow ----------

    /// <summary>The glow's colours: violet, magenta, electric blue, peach and a little teal, flowing into each other.</summary>
    private static readonly (byte R, byte G, byte B)[] GlowColors =
    {
        (0x8B, 0x5C, 0xF6), (0xFF, 0x3F, 0xC5), (0x3B, 0x6B, 0xFF), (0xFF, 0xB2, 0x7A), (0x3F, 0xE6, 0xD6),
    };

    /// <summary>
    /// Writes the glow that flows along the bottom edge at the start and the end: a strip twice the frame's width
    /// (its pattern repeats every frame width, so it can scroll forever), soft blends of the colours fading upwards
    /// to nothing, as raw RGBA at a quarter size (ffmpeg scales it up, which only makes it softer). Returns its size.
    /// </summary>
    public static (int Width, int Height) WriteEdgeGlow(string path, int width, int height)
    {
        var pw = Math.Max(16, width / 4); // one period
        var w = pw * 2;
        var h = Math.Max(8, height / 4 / 4);
        var data = new byte[w * h * 4];
        var n = GlowColors.Length;
        var sigma = pw / (double)n * 0.55;
        for (var x = 0; x < w; x++)
        {
            double r = 0, g = 0, b = 0, sum = 0;
            for (var k = 0; k < n; k++)
            {
                var cx = (k + 0.5) * pw / n;
                var d = Math.Abs((x % pw) - cx);
                d = Math.Min(d, pw - d); // wraps round, so the strip tiles
                var wgt = Math.Exp(-d * d / (2 * sigma * sigma));
                r += GlowColors[k].R * wgt; g += GlowColors[k].G * wgt; b += GlowColors[k].B * wgt; sum += wgt;
            }
            r /= sum; g /= sum; b /= sum;
            // Brighter and dimmer stretches along the edge, so it reads as moving light rather than a band.
            var swell = 0.65 + 0.35 * Math.Sin(2 * Math.PI * (x % pw) / pw * 2 + 0.7);
            for (var y = 0; y < h; y++)
            {
                var up = (h - 1 - y) / (double)(h - 1); // 0 at the bottom edge, 1 at the top of the strip
                var a = Math.Min(1, Math.Exp(-Math.Pow(up / 0.45, 2)) * swell * 1.15);
                var i = (y * w + x) * 4;
                data[i] = (byte)r; data[i + 1] = (byte)g; data[i + 2] = (byte)b; data[i + 3] = (byte)Math.Clamp(a * 255, 0, 255);
            }
        }
        File.WriteAllBytes(path, data);
        return (w, h);
    }

    /// <summary>
    /// Filtergraph lines laying the flowing glow over <paramref name="input"/>: two copies of the strip drifting at
    /// different speeds (one mirrored), lighter of the two wins, fading in at <paramref name="fadeIn"/> and out by
    /// <paramref name="end"/> (seconds in the shot). <paramref name="glowInput"/> is the strip's ffmpeg input index.
    /// </summary>
    public static IEnumerable<string> EdgeGlow(string input, string output, int glowInput, (int W, int H) strip, int width, int height,
        double length, double fadeIn, double end, string tag)
    {
        var sh = Math.Max(2, height * 26 / 100 / 2 * 2);
        var d = F(length);
        var outAt = F(Math.Max(fadeIn + 0.5, end - 1.0));
        yield return $"[{glowInput}:v]loop=loop=-1:size=1,setpts=N/(30*TB),scale={width * 2}:{sh}:flags=bicubic,format=gbrap,split[{tag}1][{tag}2]";
        yield return $"[{tag}1]crop={width}:{sh}:x='mod(t*{F(width * 0.07)},{width})':y=0[{tag}a]";
        yield return $"[{tag}2]hflip,crop={width}:{sh}:x='mod(t*{F(width * 0.045)}+{width / 3},{width})':y=0[{tag}b]";
        yield return $"[{tag}a][{tag}b]blend=all_mode=lighten,trim=duration={d},fade=t=in:st={F(fadeIn)}:d=0.8:alpha=1,fade=t=out:st={outAt}:d=1.0:alpha=1[{tag}g]";
        yield return $"{input}[{tag}g]overlay=x=0:y={height - sh}:format=auto:eof_action=pass,format=yuv420p{output}";
    }
}
