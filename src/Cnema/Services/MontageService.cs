using System.Globalization;

namespace Cnema.Services;

public enum MontageTransition { Cut, Crossfade, FadeBlack, Slide }

/// <summary>One piece of a montage: a stretch of a video, in milliseconds.</summary>
public sealed record MontagePart(string Path, long Start, long End);

public sealed record MontageJob(IReadOnlyList<MontagePart> Parts, MontageTransition Transition, MusicSpec? Music, string Output);

/// <summary>
/// Stitches the best moments of several videos into one, with transitions and an optional music track.
/// Every piece is fitted to one size and frame rate first (the biggest picture among them, at most 1080p).
/// </summary>
public static class MontageService
{
    public const long TransitionMs = 600;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Picks the part of each video to use: the loudest stretch of <paramref name="seconds"/> (0 = the whole video).
    /// </summary>
    public static async Task<List<MontagePart>> PlanAsync(IReadOnlyList<(string Path, long DurationMs)> videos, int seconds, CancellationToken ct)
    {
        var parts = new List<MontagePart>();
        foreach (var (path, duration) in videos)
        {
            ct.ThrowIfCancellationRequested();
            if (duration < 1000) continue;
            var length = seconds <= 0 ? duration : Math.Min(duration, seconds * 1000L);
            if (length >= duration) { parts.Add(new MontagePart(path, 0, duration)); continue; }
            var best = await HighlightService.BestMomentAsync(path, Math.Min(seconds, 4), ct) ?? duration / 2.0;
            // A little more after the peak than before it: the reaction is part of the moment.
            var start = (long)Math.Clamp(best - length * 0.55, 0, duration - length);
            parts.Add(new MontagePart(path, start, start + length));
        }
        return parts;
    }

    public static async Task RenderAsync(MontageJob job, IProgress<double>? progress, CancellationToken ct)
    {
        var ffmpeg = ToolLocator.Ffmpeg ?? throw new InvalidOperationException(T("ffmpeg not found."));
        var parts = job.Parts.Where(p => p.End - p.Start >= 1000).ToList();
        if (parts.Count == 0) throw new InvalidOperationException(T("Nothing to put in the montage."));

        var infos = new List<StreamInfo>();
        foreach (var p in parts)
            infos.Add(await MediaProbe.InfoAsync(p.Path, ct) ?? throw new InvalidOperationException(Loc.F("Cnema couldn't read {0}.", Path.GetFileName(p.Path))));

        var height = Math.Min(1080, infos.Max(i => i.Height));
        var width = Even((int)Math.Round(height * 16 / 9.0));
        height = Even(height);
        var fps = Math.Clamp(infos.Max(i => i.Fps is > 1 and < 241 ? i.Fps : 30), 24, 60);
        var t = job.Transition == MontageTransition.Cut ? 0 : TransitionMs;
        // A transition can't be longer than half of the shortest piece.
        t = Math.Min(t, parts.Min(p => p.End - p.Start) / 2);
        var lengths = parts.Select(p => p.End - p.Start).ToList();
        var total = lengths.Sum() - t * (parts.Count - 1);

        var args = new List<string> { "-hide_banner", "-v", "error", "-nostats", "-progress", "pipe:1" };
        for (var i = 0; i < parts.Count; i++)
            args.AddRange(new[] { "-ss", S(parts[i].Start), "-t", S(lengths[i]), "-i", parts[i].Path });
        int? musicInput = null;
        if (job.Music is { } music)
        {
            if (!File.Exists(music.Path)) throw new FileNotFoundException(Loc.F("The music file is missing: {0}", music.Path));
            args.AddRange(new[] { "-stream_loop", "-1", "-i", music.Path });
            musicInput = parts.Count;
        }

        var g = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            var d = S(lengths[i]);
            g.Add($"[{i}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black," +
                  $"setsar=1,format=yuv420p,trim=duration={d},setpts=PTS-STARTPTS,fps={F(fps)},settb=1/{(int)Math.Round(fps * 1000)}[v{i}]");
            g.Add(infos[i].AudioStreams > 0
                ? $"[{i}:a:0]aformat=sample_rates=48000:channel_layouts=stereo,atrim=duration={d},asetpts=PTS-STARTPTS,apad=whole_dur={d}[a{i}]"
                : $"anullsrc=r=48000:cl=stereo,atrim=duration={d}[a{i}]");
        }

        string vOut, aOut;
        if (t == 0 || parts.Count == 1)
        {
            g.Add(string.Concat(Enumerable.Range(0, parts.Count).Select(i => $"[v{i}][a{i}]")) + $"concat=n={parts.Count}:v=1:a=1[vcat][acat]");
            (vOut, aOut) = ("[vcat]", "[acat]");
        }
        else
        {
            var kind = job.Transition switch
            {
                MontageTransition.FadeBlack => "fadeblack",
                MontageTransition.Slide => "slideleft",
                _ => "fade",
            };
            var v = "[v0]";
            var a = "[a0]";
            long offset = 0;
            for (var i = 1; i < parts.Count; i++)
            {
                offset += lengths[i - 1] - t;
                g.Add($"{v}[v{i}]xfade=transition={kind}:duration={S(t)}:offset={S(offset)}[x{i}]");
                g.Add($"{a}[a{i}]acrossfade=d={S(t)}:c1=tri:c2=tri[ax{i}]");
                v = $"[x{i}]";
                a = $"[ax{i}]";
            }
            (vOut, aOut) = (v, a);
        }
        var audio = new List<string> { aOut };
        if (musicInput is { } mi && job.Music is { } m) audio = RenderService.AddMusic(g, audio, mi, m, total, "m");

        var temp = TrimService.TempFor(job.Output);
        args.AddRange(new[] { "-filter_complex", string.Join(";", g), "-map", vOut, "-map", audio[0] });
        args.AddRange(new[]
        {
            "-c:v", "libx264", "-preset", "medium", "-crf", "19", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-t", S(total), "-y", temp,
        });
        try
        {
            var r = await ProcessRunner.RunAsync(ffmpeg, args, line =>
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), out var us) && total > 0)
                    progress?.Report(Math.Clamp(us / 1000.0 / total, 0, 1));
            }, ct);
            if (r.ExitCode != 0)
            {
                var msg = r.StdErr.Trim();
                throw new InvalidOperationException(string.IsNullOrEmpty(msg) ? $"ffmpeg exited with {r.ExitCode}" : msg);
            }
            File.Move(temp, job.Output, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* moved, or never written */ }
        }
    }

    private static int Even(int v) => Math.Max(2, v / 2 * 2);
    private static string S(double ms) => (ms / 1000.0).ToString("0.###", Inv);
    private static string F(double v) => v.ToString("0.####", Inv);
}
