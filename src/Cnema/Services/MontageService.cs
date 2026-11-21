using System.Globalization;

namespace Cnema.Services;

public enum MontageTransition { Cut, Crossfade, FadeBlack, Slide }

/// <summary>One piece of a montage: a stretch of a video, in milliseconds.</summary>
public sealed record MontagePart(string Path, long Start, long End);

public sealed record MontageJob(IReadOnlyList<MontagePart> Parts, MontageTransition Transition, MusicSpec? Music, string Output)
{
    /// <summary>The song's beats, when it was analysed: the picture pulses with it.</summary>
    public SongInfo? Song { get; init; }
}

/// <summary>
/// Stitches the best moments of several videos into one, with transitions and an optional music track.
/// Every piece is fitted to one size and frame rate first (the biggest picture among them, at most 1080p).
/// </summary>
public static class MontageService
{
    public const long TransitionMs = 600;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A video's best moment in ms when the smart features found it already (null: not known).</summary>
    public static Func<string, double?>? KnownBestMoment { get; set; }

    /// <summary>
    /// Picks the part of each video to use: about <paramref name="seconds"/> of it (0 = the whole video). With smart
    /// cuts, every pass that has looked at the clip has a say (<see cref="CutGuide"/>): the stretch can grow by up to
    /// half or shift so it starts and ends in a pause, never mid-sentence, and a laugh comes with the joke before it.
    /// Otherwise it's the stretch around the loudest moment.
    /// </summary>
    public static async Task<List<MontagePart>> PlanAsync(IReadOnlyList<(string Path, long DurationMs)> videos, int seconds, CancellationToken ct,
        Func<string, CutGuide?>? guideOf = null, SongInfo? song = null, MontageTransition transition = MontageTransition.Cut)
    {
        var parts = await PiecesAsync(videos, seconds, ct, guideOf);
        if (song is { Usable: true } && PulseFx.Enabled && parts.Count > 1)
            parts = OnTheBeat(parts, song, transition == MontageTransition.Cut ? 0 : TransitionMs, videos.ToDictionary(v => v.Path, v => v.DurationMs, StringComparer.Ordinal), guideOf);
        return parts;
    }

    /// <summary>
    /// Moves each cut onto a beat of the song (the first beat of a bar when one is close), by making the piece before
    /// it a little longer or shorter, where that doesn't cut into a sentence. With a transition, its middle is on the beat.
    /// </summary>
    private static List<MontagePart> OnTheBeat(List<MontagePart> parts, SongInfo song, long transitionMs, Dictionary<string, long> durations, Func<string, CutGuide?>? guideOf)
    {
        var result = new List<MontagePart>();
        var downs = song.Downbeats().Where(i => i < song.Beats.Count).Select(i => song.Beats[i]).ToHashSet();
        var t = transitionMs / 1000.0;
        var at = 0.0; // where this piece starts in the montage, seconds
        for (var i = 0; i < parts.Count; i++)
        {
            var p = parts[i];
            if (i == parts.Count - 1) { result.Add(p); break; }
            var len = (p.End - p.Start) / 1000.0;
            var room = (durations.GetValueOrDefault(p.Path, p.End) - p.Start) / 1000.0;
            var guide = guideOf?.Invoke(p.Path);
            var best = len;
            var bestScore = double.MaxValue;
            foreach (var b in song.Beats)
            {
                var newLen = b + t / 2 - at;
                if (newLen < Math.Max(1.2, len * 0.75) || newLen > Math.Min(room, len * 1.3)) continue;
                var score = (downs.Contains(b) ? 0 : 0.6) + Math.Abs(newLen - len) / len * 1.5
                            + (guide?.Cost(p.Start + (long)(newLen * 1000)) ?? 0) * 0.5;
                if (score < bestScore) { bestScore = score; best = newLen; }
            }
            var end = p.Start + (long)Math.Round(best * 1000);
            result.Add(p with { End = end });
            at += best - t;
        }
        return result;
    }

    private static async Task<List<MontagePart>> PiecesAsync(IReadOnlyList<(string Path, long DurationMs)> videos, int seconds, CancellationToken ct,
        Func<string, CutGuide?>? guideOf)
    {
        var parts = new List<MontagePart>();
        foreach (var (path, duration) in videos)
        {
            ct.ThrowIfCancellationRequested();
            if (duration < 1000) continue;
            var length = seconds <= 0 ? duration : Math.Min(duration, seconds * 1000L);
            if (length >= duration) { parts.Add(new MontagePart(path, 0, duration)); continue; }
            var guide = guideOf?.Invoke(path);
            if (guide is { HasSignals: true, Peak: >= 0 })
            {
                var (from, to) = guide.Window(length);
                parts.Add(new MontagePart(path, from, to));
                continue;
            }
            // Best moments already found in the background: no need to listen again.
            var best = KnownBestMoment?.Invoke(path) is { } known && known >= 0 ? known
                : await HighlightService.BestMomentAsync(path, Math.Min(seconds, 4), ct) ?? duration / 2.0;
            if (guide is { HasSignals: true })
            {
                // Only speech is known: aim at the loudest moment, but still end where a sentence does.
                var (from, to) = guide.Window(length, fallbackPeak: (long)best);
                parts.Add(new MontagePart(path, from, to));
                continue;
            }
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
            infos.Add(await MediaProbe.InfoAsync(p.Path, ct) ?? throw new InvalidOperationException(Loc.F("Savefile couldn't read {0}.", Path.GetFileName(p.Path))));

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
        // With a song that was analysed: punches and flashes on its beats, a glow, and the flowing edge glow.
        var pulse = PulseFx.Enabled && job.Song is { Usable: true } && job.Music is not null;
        var beats = pulse ? job.Song!.Beats : new List<double>();
        var downs = pulse ? job.Song!.Downbeats().Where(k => k < beats.Count).Select(k => beats[k]).ToList() : new List<double>();
        var drops = pulse ? WrappedDirector.DropsOf(job.Song!) : new List<double>();
        var energy = pulse ? job.Song!.Energy().ToList() : null;
        var ifps = (int)Math.Round(fps);
        double outAt = 0;
        for (var i = 0; i < parts.Count; i++)
        {
            var d = S(lengths[i]);
            var fx = "";
            if (pulse)
            {
                var hits = PulseFx.Hits(beats, downs, drops, energy, outAt, outAt + lengths[i] / 1000.0);
                var frames = (int)Math.Round(lengths[i] / 1000.0 * ifps);
                fx = PulseFx.Move(width, height, ifps, frames, 1, 1.04, false, hits, 0.8) + PulseFx.Flash(hits);
            }
            outAt += (lengths[i] - t) / 1000.0;
            g.Add($"[{i}:v:0]scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black," +
                  $"setsar=1,format=yuv420p,trim=duration={d},setpts=PTS-STARTPTS,fps={F(fps)}{fx},setsar=1,format=yuv420p,settb=1/{(int)Math.Round(fps * 1000)}[v{i}]");
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
        if (pulse)
        {
            var glowPath = Path.Combine(Path.GetTempPath(), $"savefile-edge-glow-{width}x{height}.rgba");
            var strip = PulseFx.WriteEdgeGlow(glowPath, width, height);
            var gi = parts.Count + (job.Music is not null ? 1 : 0);
            args.AddRange(new[] { "-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{strip.Width}x{strip.Height}", "-i", glowPath });
            args.AddRange(new[] { "-f", "rawvideo", "-pix_fmt", "rgba", "-s", $"{strip.Width}x{strip.Height}", "-i", glowPath });
            var secs = total / 1000.0;
            g.AddRange(PulseFx.Glow(vOut, "[vg]", width, height, "gm", 0.45));
            g.AddRange(PulseFx.EdgeGlow("[vg]", "[ve1]", gi, strip, width, height, secs, 0.2, Math.Min(secs, 3.6), "ea"));
            g.AddRange(PulseFx.EdgeGlow("[ve1]", "[ve2]", gi + 1, strip, width, height, secs, Math.Max(0, secs - 4), secs, "eb"));
            vOut = "[ve2]";
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
