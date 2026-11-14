using System.Globalization;
using System.Text.RegularExpressions;

namespace Cnema.Services;

/// <summary>
/// One export with effects: which parts of the source to keep, what to add, and the form to save it in
/// (null Share = a full-quality MP4). Chapters are in source time.
/// </summary>
public sealed record RenderJob(
    string Source,
    string Output,
    IReadOnlyList<(long In, long Out)> Parts,
    IReadOnlyList<int>? AudioTracks,
    EffectsSpec Effects,
    SharePreset? Share,
    IReadOnlyList<Chapter>? Chapters);

/// <summary>
/// Builds one ffmpeg filter graph for an export with effects: the kept parts at their speeds, captions
/// (libass), the watermark, voice tools and loudness, intro and outro, then the share preset's own size.
/// </summary>
public static class RenderService
{
    /// <summary>Loudness target for "Even out loudness", in LUFS: what YouTube and most streaming sites aim for.</summary>
    public const double TargetLufs = -14;

    private static readonly Regex InputIRx = new("\"input_i\"\\s*:\\s*\"(-?[\\d.]+|-inf)\"", RegexOptions.Compiled);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static async Task RenderAsync(RenderJob job, IProgress<double>? progress, CancellationToken ct)
    {
        var ffmpeg = ToolLocator.Ffmpeg ?? throw new InvalidOperationException("ffmpeg not found.");
        var info = await MediaProbe.InfoAsync(job.Source, ct) ?? throw new InvalidOperationException("Cnema couldn't read this video.");
        if (info.Width <= 0 || info.Height <= 0) throw new InvalidOperationException("This file has no picture to add effects to.");

        var fx = job.Effects;
        var share = job.Share;
        var gif = share == SharePreset.Gif;
        var parts = job.Parts.Where(p => p.Out - p.In >= 100).OrderBy(p => p.In).ToList();
        if (parts.Count == 0) throw new ArgumentException("Nothing to export.");
        if (gif) parts = Cap(parts, TrimService.GifMaxMs);

        // The input is read from the first kept moment, so every time below is relative to it.
        var origin = parts[0].In;
        var span = parts[^1].Out - origin;
        var map = TimeMap.Build(
            parts.Select(p => (p.In - origin, p.Out - origin)).ToList(),
            fx.Speed.Select(s => s with { Start = s.Start - origin, End = s.End - origin }).ToList());
        if (map.Pieces.Count == 0) throw new ArgumentException("Nothing to export.");

        var tracks = gif ? new List<int>()
            : (job.AudioTracks ?? Enumerable.Range(0, info.AudioStreams)).Where(i => i >= 0 && i < info.AudioStreams).Distinct().ToList();
        var mixDown = share is not null && tracks.Count > 1;

        // The stage: the picture captions and the watermark are drawn on (the vertical preset crops first).
        var (sw, sh) = (Even(info.Width), Even(info.Height));
        string? crop = null;
        if (share == SharePreset.Vertical)
        {
            var cw = Math.Min(sw, Even((int)(sh * 9.0 / 16)));
            crop = $"crop={cw}:{sh}";
            sw = cw;
        }
        var fps = info.Fps is > 1 and < 241 ? info.Fps : 30;

        var bumpers = new List<(string Path, StreamInfo Info, bool Before)>();
        foreach (var (path, before) in new[] { (fx.Intro, true), (fx.Outro, false) })
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var what = before ? "intro" : "outro";
            if (!File.Exists(path)) throw new FileNotFoundException($"The {what} clip is missing: {path}");
            var bi = await MediaProbe.InfoAsync(path, ct);
            if (bi is null || bi.DurationMs <= 0 || bi.Width <= 0) throw new InvalidOperationException($"Cnema couldn't read the {what} clip.");
            bumpers.Add((path, bi, before));
        }
        var introMs = bumpers.Where(b => b.Before).Sum(b => b.Info.DurationMs);
        var totalMs = map.OutputMs + bumpers.Sum(b => b.Info.DurationMs);

        var work = Path.Combine(Path.GetTempPath(), $"cnema-render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        var temp = TrimService.TempFor(job.Output);
        try
        {
            string? fontsDir = null;
            if (fx.Captions.Any(c => c.Text.Trim().Length > 0))
            {
                var fonts = await CaptionFonts.EnsureAsync();
                // Relative paths keep drive letters and backslashes out of the filter string.
                fontsDir = Path.GetRelativePath(work, fonts).Replace('\\', '/');
                await File.WriteAllTextAsync(Path.Combine(work, "captions.ass"),
                    CaptionWriter.Write(fx.Captions.Select(c => c with { Start = c.Start - origin, End = c.End - origin }), map, sw, sh), ct);
            }

            // ---- inputs ----
            var inputs = new List<string> { "-ss", S(origin), "-t", S(span), "-i", job.Source };
            var next = 1;
            int? wmInput = null;
            if (fx.Watermark is { } wm)
            {
                if (!File.Exists(wm.Path)) throw new FileNotFoundException($"The watermark image is missing: {wm.Path}");
                inputs.AddRange(new[] { "-i", wm.Path });
                wmInput = next++;
            }
            var bumperInputs = new List<int>();
            foreach (var b in bumpers)
            {
                inputs.AddRange(new[] { "-i", b.Path });
                bumperInputs.Add(next++);
            }

            // ---- loudness: one quick measuring pass, then a plain gain (no pumping) ----
            double gain = 0;
            if (fx.Audio.Normalize && tracks.Count > 0)
            {
                progress?.Report(0.02);
                gain = await MeasureGainAsync(ffmpeg, job.Source, origin, span, tracks, fx.Audio, ct);
            }
            var from = fx.Audio.Normalize && tracks.Count > 0 ? 0.1 : 0;

            string Graph(bool withAudio, out List<string> audioOut)
            {
                var g = new List<string>();
                var n = map.Pieces.Count;

                // video: pieces at their speeds, joined
                if (n > 1) g.Add("[0:v:0]split=" + n + string.Concat(Enumerable.Range(0, n).Select(i => $"[vs{i}]")));
                for (var i = 0; i < n; i++)
                {
                    var p = map.Pieces[i];
                    var pts = Math.Abs(p.Rate - 1) < 0.0001 ? "PTS-STARTPTS" : $"(PTS-STARTPTS)/{F(p.Rate)}";
                    g.Add($"{(n > 1 ? $"[vs{i}]" : "[0:v:0]")}trim=start={S(p.Start)}:end={S(p.End)},setpts={pts}[vp{i}]");
                }
                g.Add(n > 1 ? string.Concat(Enumerable.Range(0, n).Select(i => $"[vp{i}]")) + $"concat=n={n}:v=1:a=0[vcat]" : "[vp0]null[vcat]");

                // A steady frame rate: joined pieces have none of their own, and the encoder would fall back to 25.
                var chain = new List<string> { $"fps={F(fps)}" };
                if (crop is not null) chain.Add(crop);
                chain.Add("setsar=1");
                if (fontsDir is not null) chain.Add($"subtitles=filename=captions.ass:fontsdir={fontsDir}");
                g.Add("[vcat]" + string.Join(",", chain) + "[vbase]");
                if (fx.Watermark is { } w && wmInput is { } wi)
                {
                    var m = (int)Math.Round(sw * 0.025);
                    var width = Math.Max(16, (int)Math.Round(sw * Math.Clamp(w.Width, 0.02, 0.6)));
                    var x = w.Corner is Corner.TopLeft or Corner.BottomLeft ? $"{m}" : $"main_w-overlay_w-{m}";
                    var y = w.Corner is Corner.TopLeft or Corner.TopRight ? $"{m}" : $"main_h-overlay_h-{m}";
                    g.Add($"[{wi}:v:0]scale={width}:-1,format=rgba,colorchannelmixer=aa={F(Math.Clamp(w.Opacity, 0.05, 1))}[wm]");
                    g.Add($"[vbase][wm]overlay=x={x}:y={y}:format=auto,format=yuv420p[vmain]");
                }
                else g.Add("[vbase]format=yuv420p[vmain]");

                // audio: the same pieces per kept track, then voice tools, mixing and loudness
                var aMain = new List<string>();
                if (withAudio)
                {
                    var voice = fx.Audio.VoiceTrack;
                    var voiceTools = fx.Audio.HasVoiceTools && tracks.Count > 1 && tracks.Contains(voice);
                    var processed = new Dictionary<int, string>();
                    foreach (var t in tracks)
                    {
                        if (n > 1) g.Add($"[0:a:{t}]asplit={n}" + string.Concat(Enumerable.Range(0, n).Select(i => $"[as{t}_{i}]")));
                        for (var i = 0; i < n; i++)
                        {
                            var p = map.Pieces[i];
                            g.Add($"{(n > 1 ? $"[as{t}_{i}]" : $"[0:a:{t}]")}atrim=start={S(p.Start)}:end={S(p.End)},asetpts=PTS-STARTPTS{Tempo(p.Rate)}[ap{t}_{i}]");
                        }
                        var cat = n > 1 ? string.Concat(Enumerable.Range(0, n).Select(i => $"[ap{t}_{i}]")) + $"concat=n={n}:v=0:a=1," : $"[ap{t}_0]";
                        var boost = voiceTools && t == voice && fx.Audio.VoiceBoostDb != 0 ? $",volume={F(fx.Audio.VoiceBoostDb)}dB" : "";
                        g.Add($"{cat}aformat=sample_rates=48000:channel_layouts=stereo{boost}[at{t}]");
                        processed[t] = $"[at{t}]";
                    }
                    if (voiceTools && fx.Audio.Duck)
                    {
                        var others = tracks.Where(t => t != voice).ToList();
                        g.Add($"[at{voice}]asplit={others.Count + 1}[atv]" + string.Concat(others.Select(o => $"[sc{o}]")));
                        processed[voice] = "[atv]";
                        foreach (var o in others)
                        {
                            g.Add($"[at{o}][sc{o}]sidechaincompress=threshold=0.02:ratio=8:attack=15:release=350[ad{o}]");
                            processed[o] = $"[ad{o}]";
                        }
                    }
                    var outs = tracks.Select(t => processed[t]).ToList();
                    if (mixDown)
                    {
                        g.Add(string.Concat(outs) + $"amix=inputs={outs.Count}:duration=longest:normalize=0[amixed]");
                        outs = new List<string> { "[amixed]" };
                    }
                    for (var k = 0; k < outs.Count; k++)
                    {
                        var level = gain != 0 ? $"volume={F(gain)}dB,alimiter=limit=0.891:level=disabled," : "";
                        // Every track exactly as long as the picture, whatever atempo and the compressor trimmed.
                        g.Add($"{outs[k]}{level}apad=whole_dur={S(map.OutputMs)},atrim=end={S(map.OutputMs)}[amain{k}]");
                        aMain.Add($"[amain{k}]");
                    }
                }

                // intro and outro, fitted to the stage
                var vOut = "[vmain]";
                audioOut = aMain;
                if (bumpers.Count > 0)
                {
                    var segments = new List<string>();
                    var mainSeg = vOut + string.Concat(aMain);
                    var before = new List<string>();
                    var after = new List<string>();
                    for (var j = 0; j < bumpers.Count; j++)
                    {
                        var (_, bi, isIntro) = bumpers[j];
                        var input = bumperInputs[j];
                        g.Add($"[{input}:v:0]scale=w={sw}:h={sh}:force_original_aspect_ratio=decrease,pad={sw}:{sh}:(ow-iw)/2:(oh-ih)/2:color=black,setsar=1,fps={F(fps)},format=yuv420p[bv{j}]");
                        var seg = $"[bv{j}]";
                        for (var k = 0; k < aMain.Count; k++)
                        {
                            g.Add(k == 0 && bi.AudioStreams > 0
                                ? $"[{input}:a:0]aformat=sample_rates=48000:channel_layouts=stereo[ba{j}_{k}]"
                                : $"anullsrc=r=48000:cl=stereo,atrim=duration={S(bi.DurationMs)}[ba{j}_{k}]");
                            seg += $"[ba{j}_{k}]";
                        }
                        (isIntro ? before : after).Add(seg);
                    }
                    segments.AddRange(before);
                    segments.Add(mainSeg);
                    segments.AddRange(after);
                    var joinedAudio = Enumerable.Range(0, aMain.Count).Select(k => $"[aj{k}]").ToList();
                    g.Add(string.Concat(segments) + $"concat=n={segments.Count}:v=1:a={aMain.Count}[vjoin]" + string.Concat(joinedAudio));
                    vOut = "[vjoin]";
                    audioOut = joinedAudio;
                }

                // the share preset's own size and frame rate
                switch (share)
                {
                    case SharePreset.Gif:
                        g.Add($"{vOut}fps=15,scale=480:-2:flags=lanczos,split[ga][gb];[ga]palettegen=stats_mode=diff[gp];[gb][gp]paletteuse=dither=bayer:bayer_scale=4[vout]");
                        break;
                    case SharePreset.Vertical:
                        g.Add($"{vOut}scale=-2:'min(1920,ih)'[vout]");
                        break;
                    case SharePreset.Discord10 or SharePreset.Discord50:
                        var plan = TrimService.PlanForSize(CapBytes(share.Value), (long)totalMs);
                        g.Add($"{vOut}scale=-2:'min({plan.Height},ih)',fps={F(Math.Min(plan.Fps, fps))}[vout]");
                        break;
                    default:
                        g.Add($"{vOut}null[vout]");
                        break;
                }
                return string.Join(";", g);
            }

            // ---- chapters, moved to where they land in the output ----
            string? meta = null;
            if (share is null && job.Chapters is { Count: > 1 } chapters)
            {
                var mapped = MapChapters(chapters, map, origin, introMs, (long)totalMs);
                if (mapped.Count > 1) meta = Chapters.WriteMetadataFile(mapped);
            }

            var head = new List<string> { "-hide_banner", "-v", "error", "-nostats", "-progress", "pipe:1" };
            async Task Run(IEnumerable<string> args, double start, double length)
            {
                var r = await ProcessRunner.RunAsync(ffmpeg, head.Concat(args).ToList(), line =>
                {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                        long.TryParse(line.AsSpan(12), out var us) && totalMs > 0)
                        progress?.Report(start + length * Math.Clamp(us / 1000.0 / totalMs, 0, 1));
                }, ct, work);
                if (r.ExitCode == 0) return;
                var msg = r.StdErr.Trim();
                throw new InvalidOperationException(string.IsNullOrEmpty(msg) ? $"ffmpeg exited with {r.ExitCode}" : msg);
            }

            List<string> Maps(List<string> audio)
            {
                var m = new List<string> { "-map", "[vout]" };
                foreach (var a in audio) m.AddRange(new[] { "-map", a });
                return m;
            }

            switch (share)
            {
                case SharePreset.Gif:
                {
                    var graph = Graph(false, out _);
                    await Run(inputs.Concat(new[] { "-filter_complex", graph, "-map", "[vout]", "-loop", "0", "-y", temp }), from, 1 - from);
                    break;
                }
                case SharePreset.Vertical:
                {
                    var graph = Graph(true, out var audio);
                    await Run(inputs.Concat(new[] { "-filter_complex", graph }).Concat(Maps(audio)).Concat(new[]
                    {
                        "-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p",
                        "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", "-y", temp,
                    }), from, 1 - from);
                    break;
                }
                case SharePreset.Discord10 or SharePreset.Discord50:
                {
                    var plan = TrimService.PlanForSize(CapBytes(share.Value), (long)totalMs);
                    var log = Path.Combine(work, "pass");
                    var common = new[]
                    {
                        "-c:v", "libx264", "-preset", "medium", "-pix_fmt", "yuv420p",
                        "-b:v", $"{plan.VideoKbps}k", "-maxrate", $"{plan.VideoKbps * 3 / 2}k", "-bufsize", $"{plan.VideoKbps * 2}k",
                        "-passlogfile", log,
                    };
                    // Both passes run the same graph, audio included: concat lines segments up by their longest
                    // stream, so leaving the audio out of pass 1 would give it a different number of frames.
                    var graph = Graph(true, out var audio);
                    await Run(inputs.Concat(new[] { "-filter_complex", graph }).Concat(Maps(audio)).Concat(common)
                        .Concat(new[] { "-pass", "1", "-c:a", "pcm_s16le", "-f", "null", "-y", OperatingSystem.IsWindows() ? "NUL" : "/dev/null" }),
                        from, (1 - from) * 0.45);
                    var second = graph;
                    await Run(inputs.Concat(new[] { "-filter_complex", second }).Concat(Maps(audio)).Concat(common).Concat(new[]
                    {
                        "-pass", "2", "-c:a", "aac", "-b:a", $"{plan.AudioKbps}k", "-ac", "2", "-movflags", "+faststart", "-y", temp,
                    }), from + (1 - from) * 0.45, (1 - from) * 0.55);
                    break;
                }
                default:
                {
                    var graph = Graph(true, out var audio);
                    var args = new List<string>(inputs);
                    if (meta is not null) args.AddRange(new[] { "-i", meta });
                    args.AddRange(new[] { "-filter_complex", graph });
                    args.AddRange(Maps(audio));
                    args.AddRange(meta is not null ? new[] { "-map_chapters", next.ToString(Inv) } : new[] { "-map_chapters", "-1" });
                    args.AddRange(new[]
                    {
                        "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
                        "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-y", temp,
                    });
                    try { await Run(args, from, 1 - from); }
                    finally { if (meta is not null) try { File.Delete(meta); } catch { /* temp */ } }
                    break;
                }
            }
            File.Move(temp, job.Output, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* moved, or never written */ }
            try { Directory.Delete(work, recursive: true); } catch { /* temp */ }
        }
    }

    /// <summary>Loudness of the kept audio (mixed, voice boost included), as the gain that brings it to the target.</summary>
    private static async Task<double> MeasureGainAsync(
        string ffmpeg, string source, long origin, long span, IReadOnlyList<int> tracks, AudioSpec audio, CancellationToken ct)
    {
        var g = new List<string>();
        foreach (var t in tracks)
        {
            var boost = audio.HasVoiceTools && t == audio.VoiceTrack && audio.VoiceBoostDb != 0 ? $",volume={F(audio.VoiceBoostDb)}dB" : "";
            g.Add($"[0:a:{t}]aformat=sample_rates=48000:channel_layouts=stereo{boost}[m{t}]");
        }
        var mix = tracks.Count > 1
            ? string.Concat(tracks.Select(t => $"[m{t}]")) + $"amix=inputs={tracks.Count}:duration=longest:normalize=0,"
            : $"[m{tracks[0]}]";
        g.Add(mix + $"loudnorm=I={F(TargetLufs)}:TP=-1.5:LRA=11:print_format=json[out]");
        var r = await ProcessRunner.RunAsync(ffmpeg, new[]
        {
            "-hide_banner", "-nostats", "-v", "info", "-ss", S(origin), "-t", S(span), "-i", source,
            "-filter_complex", string.Join(";", g), "-map", "[out]", "-f", "null", OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
        }, ct: ct);
        var m = InputIRx.Matches(r.StdErr).LastOrDefault();
        if (m is null || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, Inv, out var measured) || measured < -70) return 0;
        return Math.Round(Math.Clamp(TargetLufs - measured, -20, 20), 2);
    }

    internal static List<Chapter> MapChapters(IReadOnlyList<Chapter> chapters, TimeMap map, long origin, long introMs, long totalMs)
    {
        var starts = new List<(long Start, string Title)>();
        foreach (var c in chapters.OrderBy(c => c.Start))
        {
            if (map.MapForward(c.Start - origin) is not { } at) continue;
            var start = introMs + (long)Math.Round(at);
            // Chapters that land on the same moment (their part was cut out): the later one starts there.
            starts.RemoveAll(s => Math.Abs(s.Start - start) < 500);
            starts.Add((start, c.Title));
        }
        if (starts.Count > 0 && introMs > 0) starts.Insert(0, (0, "Intro"));
        var result = new List<Chapter>();
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Start : totalMs;
            if (end - starts[i].Start < 500) continue;
            result.Add(new Chapter(result.Count + 1, starts[i].Start, end, starts[i].Title));
        }
        return result;
    }

    private static List<(long In, long Out)> Cap(List<(long In, long Out)> parts, long maxMs)
    {
        var kept = new List<(long In, long Out)>();
        long total = 0;
        foreach (var (a, b) in parts)
        {
            if (total >= maxMs) break;
            var len = Math.Min(b - a, maxMs - total);
            kept.Add((a, a + len));
            total += len;
        }
        return kept;
    }

    /// <summary>atempo keeps the pitch; one instance only goes from half to double speed, so it is chained.</summary>
    internal static string Tempo(double rate)
    {
        if (Math.Abs(rate - 1) < 0.0001) return "";
        var parts = new List<string>();
        var r = rate;
        while (r < 0.5) { parts.Add("atempo=0.5"); r /= 0.5; }
        while (r > 2) { parts.Add("atempo=2"); r /= 2; }
        if (Math.Abs(r - 1) > 0.0001) parts.Add($"atempo={F(r)}");
        return "," + string.Join(",", parts);
    }

    private static long CapBytes(SharePreset p) => (p == SharePreset.Discord10 ? 10L : 50L) * 1000 * 1000;
    private static int Even(int v) => Math.Max(2, v / 2 * 2);
    private static string S(double ms) => (ms / 1000.0).ToString("0.###", Inv);
    private static string F(double v) => v.ToString("0.####", Inv);
}
