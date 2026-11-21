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
        int Even(int v) => Math.Max(2, v / 2 * 2);
        // Two halos: a tight one at half size and a wide one at an eighth, so lights bloom softly instead of fogging.
        yield return $"{input}format=gbrp,split[{tag}a][{tag}b]";
        yield return $"[{tag}b]scale={Even(width / 2)}:{Even(height / 2)},curves=all='0/0 0.55/0 0.8/0.45 1/1',split[{tag}c][{tag}d]";
        yield return $"[{tag}c]gblur=sigma=5[{tag}e]";
        yield return $"[{tag}d]scale={Even(width / 8)}:{Even(height / 8)},gblur=sigma=5,scale={Even(width / 2)}:{Even(height / 2)}:flags=bilinear[{tag}f]";
        yield return $"[{tag}e][{tag}f]blend=all_mode=screen,scale={width}:{height}:flags=bilinear[{tag}h]";
        yield return $"[{tag}a][{tag}h]blend=all_mode=screen:all_opacity={F(amount)},vignette=angle=PI/6:eval=init,format=yuv420p{output}";
    }

    private static string F(double v) => v.ToString("0.#####", Inv);

    // ---------- the edge glow ----------

    /// <summary>The glow's colours: violet, magenta, electric blue, peach and a little teal, flowing into each other.</summary>
    private static readonly (byte R, byte G, byte B)[] GlowColors =
    {
        (0x8B, 0x5C, 0xF6), (0xFF, 0x3F, 0xC5), (0x3B, 0x6B, 0xFF), (0xFF, 0xB2, 0x7A), (0x3F, 0xE6, 0xD6),
    };

    /// <summary>
    /// Writes the light that flows along the bottom edge at the start and the end, like light seen through frosted
    /// glass: a strip twice the frame's width (its pattern repeats every frame width, so it can scroll forever) of the
    /// colours blending into each other, with a bright core hugging the edge and a tall soft haze above it, brighter
    /// and dimmer stretches along it. Raw RGB on black (it's added as light), at a quarter size: ffmpeg scales it up,
    /// which only makes it softer. Returns its size.
    /// </summary>
    public static (int Width, int Height) WriteEdgeGlow(string path, int width, int height)
    {
        var pw = Math.Max(16, width / 4); // one period
        var w = pw * 2;
        var h = Math.Max(8, height * 34 / 100 / 4);
        var data = new byte[w * h * 3];
        var n = GlowColors.Length;
        var sigma = pw / (double)n * 0.45;
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
            var phase = 2 * Math.PI * (x % pw) / pw;
            var swell = 0.6 + 0.25 * Math.Sin(phase * 2 + 0.7) + 0.15 * Math.Sin(phase * 3 - 1.1);
            // The haze rises higher where the light is stronger, so the top edge is a soft, uneven line.
            var reach = 0.42 + 0.3 * swell;
            for (var y = 0; y < h; y++)
            {
                var up = (h - 1 - y) / (double)(h - 1); // 0 at the bottom edge, 1 at the top of the strip
                var core = Math.Exp(-Math.Pow(up / 0.07, 2));
                var fall = (1 - up) * (1 - up); // reaches black at the strip's top, so no edge shows
                var haze = Math.Exp(-Math.Pow(up / reach, 2)) * fall * 1.05;
                var a = Math.Min(1.0, (core * 0.4 + haze) * (0.5 + 0.6 * swell));
                // Toward white in the core, like a light source; the haze keeps its colour.
                var white = core * 0.12;
                var i = (y * w + x) * 3;
                data[i] = (byte)Math.Clamp((r + (255 - r) * white) * a, 0, 255);
                data[i + 1] = (byte)Math.Clamp((g + (255 - g) * white) * a, 0, 255);
                data[i + 2] = (byte)Math.Clamp((b + (255 - b) * white) * a, 0, 255);
            }
        }
        File.WriteAllBytes(path, data);
        return (w, h);
    }

    /// <summary>
    /// Filtergraph lines adding the flowing light over the bottom of <paramref name="input"/>: two copies of the strip
    /// drifting at different speeds (one mirrored and a little shorter), added as light, breathing slowly, fading in
    /// at <paramref name="fadeIn"/> and out by <paramref name="end"/> (seconds in the shot). Only the bottom of the
    /// picture is touched. <paramref name="glowInput"/> is the strip's ffmpeg input index (raw rgb24).
    /// </summary>
    public static IEnumerable<string> EdgeGlow(string input, string output, int glowInput, (int W, int H) strip, int width, int height,
        double length, double fadeIn, double end, string tag)
    {
        var sh = Math.Max(2, height * 34 / 100 / 2 * 2);
        var shortH = Math.Max(2, sh * 7 / 10 / 2 * 2);
        var d = F(length);
        var outAt = F(Math.Max(fadeIn + 0.5, end - 1.1));
        // The strip is a single picture repeated at 30 fps. (No T in blend expressions: ffmpeg 6 works T out in the
        // wrong time base when the two inputs' time bases differ, so the fades happen on the glow itself.)
        yield return $"[{glowInput}:v]loop=loop=-1:size=1,settb=expr=1/30,setpts=N,trim=duration={d},format=gbrp,split[{tag}1][{tag}2]";
        yield return $"[{tag}1]scale={width * 2}:{sh}:flags=bicubic,crop={width}:{sh}:x='mod(t*{F(width * 0.06)},{width})':y=0[{tag}a]";
        yield return $"[{tag}2]hflip,scale={width * 2}:{shortH}:flags=bicubic,crop={width}:{shortH}:x='mod(t*{F(width * 0.037)}+{width / 3},{width})':y=0," +
                     $"pad={width}:{sh}:0:{sh - shortH}:black[{tag}b]";
        // Up over 0.9 s, down over 1.1 s (fading light on black is the same as dimming it).
        yield return $"[{tag}a][{tag}b]blend=all_mode=screen,fade=t=in:st={F(fadeIn)}:d=0.9,fade=t=out:st={outAt}:d=1.1[{tag}g]";
        yield return $"{input}format=gbrp,split[{tag}v][{tag}w]";
        yield return $"[{tag}w]crop={width}:{sh}:0:{height - sh}[{tag}bot]";
        // Added as light.
        yield return $"[{tag}bot][{tag}g]blend=all_mode=screen[{tag}lit]";
        yield return $"[{tag}v][{tag}lit]overlay=x=0:y={height - sh}:eof_action=pass,format=yuv420p{output}";
    }

    // ---------- organic transitions ----------

    /// <summary>
    /// The xfade options for one of Savefile's own transitions (names start with "sf:"), drawn per pixel with
    /// ffmpeg's custom transition: a liquid blob that swells open with a wobbling, glowing rim, a wave that rolls up
    /// the frame, ink that blooms through like clouds, and a bright burst for a drop. All ease in and out. Null for
    /// "sf:leak", which is a straight cut with a light leak laid over it (see <see cref="LightLeak"/>).
    /// </summary>
    public static string? Transition(string name, int seed)
    {
        // Written out in full with no st()/ld(): ffmpeg runs the expression on several threads that share those
        // registers, which turned the picture into speckled noise. p = progress 0→1 (xfade's P runs 1→0),
        // e = eased progress, u/v = position 0..1.
        const string p = "(1-P)";
        const string e = "((1-P)*(1-P)*(1+2*P))";
        const string u = "(X/W)";
        const string v = "(Y/H)";
        var rng = new Random(seed);
        // Mix A to B by a front f (B where f > 0), softened over the band, plus a glowing rim of light on the front.
        static string Mix(string f, double band, double rim, double rimWidth) =>
            $"min(255,A+(B-A)*clip(({f})/{F(band)}+0.5,0,1)" +
            (rim > 0 ? $"+if(eq(PLANE,0),{F(rim)}*exp(-pow(({f})/{F(rimWidth)},2))*(1-0.6*{p}),0))" : ")");
        string Blob(double cx, double cy, double speed) =>
            $"{e}*{F(speed)}-hypot(({u}-{F(cx)})*1.78,{v}-{F(cy)})" +
            $"-0.05*sin(atan2({v}-{F(cy)},{u}-{F(cx)})*5+{p}*8)-0.022*sin({u}*23+{p}*11)*cos({v}*19-{p}*9)";
        var expr = name switch
        {
            "sf:blob" => Mix(Blob(0.25 + rng.NextDouble() * 0.5, 0.3 + rng.NextDouble() * 0.4, 1.25), 0.05, 45, 0.03),
            "sf:burst" => Mix(Blob(0.5, 0.5, 1.3), 0.05, 110, 0.04),
            "sf:wave" => Mix($"{e}*1.3-0.15-(1-{v})-0.05*sin({u}*9+{p}*6)-0.03*sin({u}*21-{p}*10)", 0.06, 40, 0.035),
            "sf:ink" => Mix($"{e}*1.6-0.3-(0.5+0.24*sin({u}*11+{v}*6+1.3)+0.14*sin({u}*23-{v}*17+{p}*2)+0.08*sin({v}*31+{u}*5-{p}*3))", 0.22, 0, 0),
            _ => null,
        };
        return expr is null ? null : $"transition=custom:expr='{expr}'";
    }

    /// <summary>
    /// A light leak over the first half second of a shot: the edge glow's colours spread over the whole frame,
    /// drifting, flaring up and fading away. Filtergraph lines; <paramref name="glowInput"/> is the strip's input.
    /// </summary>
    public static IEnumerable<string> LightLeak(string input, string output, int glowInput, int width, int height, double length, string tag)
    {
        var d = F(length);
        // Up fast on the cut, then away over half a second; added as light, so nothing goes grey.
        yield return $"[{glowInput}:v]loop=loop=-1:size=1,settb=expr=1/30,setpts=N,trim=duration={d},vflip,scale={width * 2}:{height}:flags=bicubic," +
                     $"crop={width}:{height}:x='mod(t*{F(width * 0.9)},{width})':y=0,format=gbrp,fade=t=in:st=0:d=0.06,fade=t=out:st=0.15:d=0.45,colorlevels=romax=0.85:gomax=0.85:bomax=0.85[{tag}l]";
        yield return $"{input}format=gbrp[{tag}v]";
        yield return $"[{tag}v][{tag}l]blend=all_mode=screen,format=yuv420p{output}";
    }
}
