using System.Globalization;
using Cnema.Models;

namespace Cnema.Services;

/// <summary>The numbers on the year's stat cards.</summary>
public sealed record WrappedStats(
    int Year, int Moments, long TotalMs, int Days,
    IReadOnlyList<(string Name, int Count)> Games,
    IReadOnlyList<(string Name, int Count)> Friends,
    DateTime? BiggestNight, int BiggestNightCount, IReadOnlyList<string> BiggestNightWho,
    double LaughSeconds, string? LaughFriend);

/// <summary>One stat card: what it says, and how long it stays at least.</summary>
public sealed record WrappedCard(string Kind, string Kicker, string Big, string Small, double MinSeconds);

/// <summary>
/// "Wrapped": the year in one video, made on this computer. Picks the year's best moments, lets the director
/// cut them to the person's own songs, draws the stat cards, and renders with the bundled ffmpeg at low
/// priority: every shot to a small intermediate first (so only one video is open at a time), then joined
/// without re-encoding the picture while the soundtrack is mixed.
/// </summary>
public static class WrappedService
{
    public const int Fps = 30;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Videos Savefile made itself (montages, earlier Wrapped videos) don't go in.</summary>
    public static bool IsOwnOutput(string path)
    {
        var dir = Path.GetFileName(Path.GetDirectoryName(path) ?? "");
        return dir is "Montages" or "Wrapped" || Path.GetFileName(path).StartsWith("Savefile Wrapped", StringComparison.OrdinalIgnoreCase);
    }

    public static List<VideoEntry> YearClips(IEnumerable<VideoEntry> all, int year) => all
        .Where(e => e.ModifiedUtc.ToLocalTime().Year == year && e.DurationMs >= 2000 && !IsOwnOutput(e.Path))
        .ToList();

    /// <summary>How good a clip is for the reel: best moments, favourites, laughter, hype, friends, a note.</summary>
    public static double Score(VideoEntry e)
    {
        var laugh = e.Sound is { Laugh.Count: > 1 } s ? s.LaughSeconds : 0;
        var hype = e.Sound is { Hype.Count: > 1 } h ? h.HypeSeconds : 0;
        return (e.Smart?.Excitement ?? 0.3) * 3 + (e.Favorite ? 2 : 0) + Math.Min(2.5, laugh / 2) + Math.Min(1, hype / 3)
               + (e.People.Count > 0 ? 0.6 : 0) + (string.IsNullOrWhiteSpace(e.Note) ? 0 : 0.3);
    }

    /// <summary>The year's moments for the reel, best first: at most <paramref name="max"/>.</summary>
    public static List<WrappedMoment> Moments(IEnumerable<VideoEntry> clips, int max = 44) => clips
        .Select(e => (e, Score: Score(e)))
        .OrderByDescending(x => x.Score).Take(max)
        .Select(x => new WrappedMoment(x.e.Path, x.e.DurationMs, Focus(x.e), x.Score, Voice(x.e)))
        .ToList();

    /// <summary>The peak of a clip: the densest laughter when there is some, else the best moment, else a third in.</summary>
    private static long Focus(VideoEntry e)
    {
        if (e.Sound is { Laugh.Count: >= 3 } s)
        {
            var best = s.Laugh.MaxBy(m => s.Laugh.Count(o => Math.Abs(o.At - m.At) <= 3000))!;
            return best.At;
        }
        if (e.Smart is { BestMs: >= 0 } smart) return smart.BestMs;
        return e.DurationMs / 3;
    }

    /// <summary>Where people talk or laugh in a clip (ms), so the music can make room.</summary>
    private static List<(long, long)> Voice(VideoEntry e)
    {
        var list = new List<(long, long)>();
        if (e.Speech is { } sp) list.AddRange(sp.Lines.Select(l => (l.Start, l.End)));
        if (e.Sound is { } so) list.AddRange(so.Laugh.Concat(so.Hype).Select(m => (m.At - 400, m.At + 600)));
        return list;
    }

    public static WrappedStats Stats(IReadOnlyList<VideoEntry> year, int y, bool people, Func<VideoEntry, string?> gameOf)
    {
        var days = year.Select(e => e.ModifiedUtc.ToLocalTime().Date).Distinct().Count();
        var games = year.Select(gameOf).OfType<string>().GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.First(), g.Count())).OrderByDescending(g => g.Item2).Take(3).ToList();
        var friends = people
            ? year.SelectMany(e => e.People).GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.First(), g.Count())).OrderByDescending(g => g.Item2).Take(3).ToList()
            : new List<(string, int)>();
        var night = year.GroupBy(e => NightOf(e.ModifiedUtc.ToLocalTime())).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).FirstOrDefault();
        var who = night is null || !people ? new List<string>()
            : night.SelectMany(e => e.People).GroupBy(p => p, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.First()).Take(3).ToList();
        double Laugh(VideoEntry e) => e.Sound is { Laugh.Count: > 1 } s ? s.LaughSeconds : 0;
        var laugh = year.Sum(Laugh);
        var laughFriend = people
            ? year.Where(e => Laugh(e) > 0).SelectMany(e => e.People.Select(p => (p, Laugh(e))))
                .GroupBy(x => x.p, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Sum(x => x.Item2)).Select(g => g.First().p).FirstOrDefault()
            : null;
        return new WrappedStats(y, year.Count, year.Sum(e => e.DurationMs), days, games, friends,
            night is { } n && n.Count() > 1 ? n.Key : null, night?.Count() ?? 0, who, laugh, laughFriend);
    }

    /// <summary>A night runs until 6 in the morning: a 2 AM clip belongs to the evening before.</summary>
    private static DateTime NightOf(DateTime local) => (local.Hour < 6 ? local.AddDays(-1) : local).Date;

    public static List<WrappedCard> Cards(WrappedStats s)
    {
        var c = CultureInfo.CurrentCulture;
        var cards = new List<WrappedCard>
        {
            new("intro", "savefile", F("Your {0}", s.Year), T("wrapped"), WrappedDirector.IntroSeconds),
            new("moments", T("This year you kept"), P(s.Moments, "{0} moment", "{0:N0} moments"),
                F("{0} of memories, across {1}.", Span(s.TotalMs), P(s.Days, "{0} day", "{0:N0} days")), WrappedDirector.CardSeconds),
        };
        if (s.Games.Count > 0)
        {
            var (game, count) = s.Games[0];
            var share = s.Moments > 0 ? (int)Math.Round(100.0 * count / s.Moments) : 0;
            cards.Add(new("game", T("Your game of the year"), SmartTitles.Pretty(game),
                F("{0} · {1}% of your year", P(count, "{0} clip", "{0:N0} clips"), share), WrappedDirector.CardSeconds));
        }
        if (s.Friends.Count > 0)
            cards.Add(new("squad", T("Your squad"), string.Join("  ·  ", s.Friends.Select(f => f.Name)),
                string.Join("   ", s.Friends.Select(f => F("{0}: {1}", f.Name, P(f.Count, "{0} clip together", "{0:N0} clips together")))), WrappedDirector.CardSeconds));
        if (s.BiggestNight is { } night)
            cards.Add(new("night", T("Your biggest night"), night.ToString("dddd d MMMM", c),
                s.BiggestNightWho.Count > 0 ? F("{0} with {1}", P(s.BiggestNightCount, "{0} save", "{0} saves"), Join(s.BiggestNightWho))
                    : P(s.BiggestNightCount, "{0} save", "{0} saves"), WrappedDirector.CardSeconds));
        if (s.LaughSeconds >= 20)
            cards.Add(new("laughs", T("You laughed for"), Span((long)(s.LaughSeconds * 1000)),
                s.LaughFriend is { } f ? F("Most of it with {0}.", f) : T("And that's just what was recorded."), WrappedDirector.CardSeconds));
        cards.Add(new("outro", F("That was your {0}.", s.Year), T("Some saves you never overwrite."), F("See you in {0}.", s.Year + 1), WrappedDirector.OutroSeconds));
        return cards;
    }

    private static string Join(IReadOnlyList<string> names) =>
        names.Count switch { 1 => names[0], 2 => F("{0} and {1}", names[0], names[1]), _ => F("{0}, {1} and {2}", names[0], names[1], names[2]) };

    public static string Span(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? F("{0} h {1} min", (int)t.TotalHours, t.Minutes)
            : t.TotalMinutes >= 1 ? F("{0} min {1} s", (int)t.TotalMinutes, t.Seconds)
            : F("{0} s", (int)Math.Round(t.TotalSeconds));
    }

    // ---------- rendering ----------

    /// <summary>
    /// Renders the plan to <paramref name="output"/>. <paramref name="cardImages"/> are the stat cards as PNGs at
    /// the video's size (index = card number). Progress 0..1.
    /// </summary>
    public static async Task RenderAsync(WrappedPlan plan, IReadOnlyList<string> cardImages, int width, int height, string output,
        IProgress<double>? progress, CancellationToken ct)
    {
        var ffmpeg = ToolLocator.Ffmpeg ?? throw new InvalidOperationException(T("ffmpeg not found."));
        var work = Path.Combine(Path.GetTempPath(), "savefile-wrapped-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        var music = plan.Songs.Count > 0;
        var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4).ToString(Inv);
        try
        {
            var parts = new List<string>();
            var infos = new Dictionary<string, StreamInfo?>(StringComparer.Ordinal);
            for (var i = 0; i < plan.Shots.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var shot = plan.Shots[i];
                // Whole frames from the timeline's own clock, so nothing drifts against the music.
                var frames = (int)Math.Round(shot.End * Fps) - (int)Math.Round(shot.At * Fps);
                if (frames <= 0) continue;
                var d = S(frames / (double)Fps);
                var part = Path.Combine(work, $"{i:000}.mkv");
                var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-y" };
                string video, audio;
                var fadeIn = i == 0 ? $",fade=t=in:st=0:d=0.5" : "";
                var fadeOut = i == plan.Shots.Count - 1 ? $",fade=t=out:st={S(Math.Max(0, frames / (double)Fps - 1.2))}:d=1.2" : "";
                if (shot.Kind == WrappedShotKind.Card)
                {
                    args.AddRange(new[] { "-loop", "1", "-framerate", Fps.ToString(Inv), "-t", d, "-i", cardImages[shot.Card] });
                    args.AddRange(new[] { "-f", "lavfi", "-t", d, "-i", "anullsrc=r=48000:cl=stereo" });
                    video = $"[0:v]scale={width}:{height},setsar=1,format=yuv420p{fadeIn}{fadeOut}[v]";
                    audio = "[1:a]asplit=2[a][k]";
                }
                else
                {
                    if (!infos.TryGetValue(shot.Path!, out var info)) infos[shot.Path!] = info = await MediaProbe.InfoAsync(shot.Path!, ct);
                    args.AddRange(new[] { "-ss", S(shot.StartMs / 1000.0), "-t", d, "-i", shot.Path! });
                    var src = info is { AudioStreams: > 0 } ? "[0:a:0]" : null;
                    if (src is null) args.AddRange(new[] { "-f", "lavfi", "-t", d, "-i", "anullsrc=r=48000:cl=stereo" });
                    // Fill the frame when the clip is about 16:9; letterbox anything else (a phone clip).
                    var aspect = info is { Width: > 0, Height: > 0 } ? (double)info.Width / info.Height : 16 / 9.0;
                    var fit = aspect is > 1.6 and < 1.95
                        ? $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height}"
                        : $"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:color=black";
                    video = $"[0:v:0]{fit},fps={Fps},setsar=1,format=yuv420p,trim=duration={d},setpts=PTS-STARTPTS,tpad=stop_mode=clone:stop_duration={d},trim=duration={d}{fadeIn}{fadeOut}[v]";
                    // Talking and laughing come through; the rest of the game sound stays under the music.
                    var mix = !music || shot.Voice ? "0dB" : "-16dB";
                    var key = shot.Voice ? "1" : "0";
                    audio = $"{src ?? "[1:a]"}aformat=sample_rates=48000:channel_layouts=stereo,atrim=duration={d},asetpts=PTS-STARTPTS,apad=whole_dur={d},atrim=duration={d}," +
                            $"afade=t=in:d=0.02,afade=t=out:st={S(Math.Max(0, frames / (double)Fps - 0.03))}:d=0.03,asplit=2[a0][k0];[a0]volume={mix}[a];[k0]volume={key}[k]";
                }
                args.AddRange(new[]
                {
                    "-filter_complex", video + ";" + audio, "-map", "[v]", "-map", "[a]", "-map", "[k]",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "19", "-r", Fps.ToString(Inv), "-g", (Fps * 2).ToString(Inv), "-threads", threads,
                    "-c:a", "pcm_s16le", "-ar", "48000", "-frames:v", frames.ToString(Inv), part,
                });
                var r = await ProcessRunner.RunAsync(ffmpeg, args, ct: ct, background: true);
                if (r.ExitCode != 0 || !File.Exists(part))
                    throw new InvalidOperationException(Loc.F("Couldn't prepare part {0} of the video: {1}", i + 1, r.StdErr.Trim()));
                parts.Add(part);
                progress?.Report(0.85 * (i + 1) / plan.Shots.Count);
            }

            // Join the parts as they are and lay the soundtrack under them.
            var list = Path.Combine(work, "parts.txt");
            await File.WriteAllLinesAsync(list, parts.Select(p => $"file '{p.Replace("'", "'\\''")}'"), ct);
            var total = plan.Shots.Count == 0 ? 0 : Math.Round(plan.Shots[^1].End * Fps) / Fps;
            var final = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-nostats", "-progress", "pipe:1", "-y", "-f", "concat", "-safe", "0", "-i", list };
            var g = new List<string>();
            string aOut;
            if (music)
            {
                for (var i = 0; i < plan.Songs.Count; i++)
                {
                    var s = plan.Songs[i];
                    final.AddRange(new[] { "-ss", S(s.In), "-t", S(s.Length), "-i", s.Song.Path });
                    g.Add($"[{i + 1}:a:0]aformat=sample_rates=48000:channel_layouts=stereo,asetpts=PTS-STARTPTS,volume={S(s.GainDb)}dB" +
                          (i == 0 && s.In > 0.05 ? ",afade=t=in:d=0.3" : "") + $"[m{i}]");
                }
                var m = "[m0]";
                for (var i = 1; i < plan.Songs.Count; i++)
                {
                    // Equal-power: the sum stays as loud through the change.
                    g.Add($"{m}[m{i}]acrossfade=d={S(plan.Songs[i - 1].CrossfadeOut)}:c1=qsin:c2=qsin[x{i}]");
                    m = $"[x{i}]";
                }
                var fade = plan.Songs[^1].FadeOut;
                g.Add($"{m}afade=t=out:st={S(Math.Max(0, total - fade))}:d={S(fade)},apad,atrim=duration={S(total)}[mus]");
                // Voices and laughs push the music down; the rest of the game sound sits under it.
                g.Add("[mus][0:a:1]sidechaincompress=threshold=0.015:ratio=6:attack=15:release=400[duck]");
                g.Add("[duck][0:a:0]amix=inputs=2:normalize=0:duration=first,alimiter=limit=0.9[aout]");
                aOut = "[aout]";
            }
            else
            {
                g.Add("[0:a:0]alimiter=limit=0.9[aout]");
                aOut = "[aout]";
            }
            var temp = TrimService.TempFor(output);
            final.AddRange(new[]
            {
                "-filter_complex", string.Join(";", g), "-map", "0:v:0", "-map", aOut,
                "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-t", S(total), "-movflags", "+faststart", temp,
            });
            var done = await ProcessRunner.RunAsync(ffmpeg, final, line =>
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line.AsSpan(12), out var us) && total > 0)
                    progress?.Report(0.85 + 0.15 * Math.Clamp(us / 1e6 / total, 0, 1));
            }, ct, background: true);
            if (done.ExitCode != 0 || !File.Exists(temp))
            {
                try { File.Delete(temp); } catch { /* nothing written */ }
                throw new InvalidOperationException(done.StdErr.Trim() is { Length: > 0 } e ? e : $"ffmpeg exited with {done.ExitCode}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Move(temp, output, overwrite: true);
            progress?.Report(1);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { /* temp */ }
        }
    }

    private static string S(double v) => v.ToString("0.###", Inv);
}
