using System.Diagnostics;
using System.Globalization;

namespace Cnema.Services;

public enum ClipState { Off, Starting, Recording, Error }

/// <summary>
/// The background clipping service ("save the last 30 seconds").
///
/// One ffmpeg process captures the screen with a hardware encoder where possible and writes a
/// rolling window of 2-second MPEG-TS segments to a temp folder; old segments are deleted as new
/// ones arrive, so disk use stays bounded and nothing is kept unless a clip is saved. Saving a
/// clip is a stream copy of the newest segments into an .mp4: no re-encode, done in a moment.
/// </summary>
public sealed class ClipService : IDisposable
{
    public const int SegmentSeconds = 2;

    private readonly SettingsStore _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<string> _log = new();
    private Process? _process;
    private Timer? _janitor;
    private int _generation;
    private int _restarts;
    private DateTime _startedAt;

    public ClipState State { get; private set; } = ClipState.Off;
    public string? Error { get; private set; }
    public string? Encoder { get; private set; }
    public CodecOption? Codec { get; private set; }

    /// <summary>Raised on any thread when State/Error change.</summary>
    public event Action? Changed;
    /// <summary>Raised on any thread after a clip was written; the argument is its path.</summary>
    public event Action<string>? ClipSaved;

    public ClipService(SettingsStore settings) => _settings = settings;

    /// <summary>The app in front, when a per-game profile exists for it; set by the view model.</summary>
    public string? ActiveGame { get; set; }
    private ClipSettings Cfg => _settings.Current.Clip.For(ActiveGame);

    // ---------- session recording ----------
    // A session is the same rolling buffer with pruning switched off: every segment since the start
    // is kept and joined into one file at the end, so there is still only one encoder running.
    private DateTime? _sessionStart;
    private string? _sessionFirst;
    private string? _sessionLabel;
    private readonly List<long> _bookmarks = new();

    public bool IsSession => _sessionStart is not null;
    public TimeSpan SessionElapsed => _sessionStart is { } s ? DateTime.UtcNow - s : TimeSpan.Zero;
    public int BookmarkCount { get { lock (_bookmarks) return _bookmarks.Count; } }
    /// <summary>The finished session file and its bookmarks (ms from the start of the file).</summary>
    public event Action<string, IReadOnlyList<long>>? SessionSaved;

    public bool StartSession(string? label)
    {
        if (State != ClipState.Recording || IsSession) return false;
        var segments = Segments();
        if (segments.Count == 0) return false;
        _sessionFirst = Path.GetFileName(segments[^1]);
        // The file starts with the segment that is being written now, a moment before this call.
        DateTime begun;
        try { begun = File.GetCreationTimeUtc(segments[^1]); } catch { begun = DateTime.UtcNow; }
        var now = DateTime.UtcNow;
        _sessionStart = begun > now.AddSeconds(-SegmentSeconds * 2) && begun <= now ? begun : now;
        _sessionLabel = label;
        lock (_bookmarks) _bookmarks.Clear();
        Log.Info("Session recording started");
        Changed?.Invoke();
        return true;
    }

    /// <summary>Marks "now" in the running session. Returns the position, or null without a session.</summary>
    public TimeSpan? AddBookmark()
    {
        if (_sessionStart is not { } start) return null;
        var at = DateTime.UtcNow - start;
        lock (_bookmarks) _bookmarks.Add((long)at.TotalMilliseconds);
        Changed?.Invoke();
        return at;
    }

    public async Task<string?> StopSessionAsync()
    {
        await _gate.WaitAsync();
        try { return await FinishSessionCoreAsync(); }
        finally { _gate.Release(); }
    }

    /// <summary>Joins the session's segments into one file. The caller holds the gate.</summary>
    private async Task<string?> FinishSessionCoreAsync()
    {
        if (_sessionStart is not { } started || _sessionFirst is not { } first) return null;
        List<long> marks;
        lock (_bookmarks) { marks = _bookmarks.ToList(); _bookmarks.Clear(); }
        var label = _sessionLabel;
        _sessionStart = null;
        _sessionFirst = null;
        _sessionLabel = null;
        string? output = null;
        try
        {
            var ffmpeg = await CaptureProbe.FindCaptureFfmpegAsync() ?? ToolLocator.Ffmpeg;
            var segments = Segments().Where(s => string.CompareOrdinal(Path.GetFileName(s), first) >= 0).ToList();
            if (ffmpeg is null || segments.Count == 0) return null;

            var folder = Cfg.ResolvedFolder;
            Directory.CreateDirectory(folder);
            var name = SafeName(label is null ? "Session" : label + " session");
            output = Path.Combine(folder, $"{name} {started.ToLocalTime():yyyy-MM-dd HH-mm-ss}.mp4");
            var list = Path.Combine(AppPaths.ClipBufferDir, "session.txt");
            await File.WriteAllLinesAsync(list, segments.Select(s => $"file '{s.Replace("'", "'\\''")}'"));
            // Same disk as the destination (a session can be many GB), under a name the library ignores.
            var partial = output + ".part";
            // Bookmarks become chapters inside the file, so other players can jump between them too.
            var chapters = Chapters.Enabled
                ? Chapters.FromBookmarks(marks, (long)(segments.Count * SegmentSeconds * 1000))
                : Array.Empty<Chapter>();
            var meta = chapters.Count > 1 ? Chapters.WriteMetadataFile(chapters) : null;
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0", "-i", list };
            if (meta is not null) args.AddRange(new[] { "-i", meta });
            args.AddRange(new[] { "-map", "0", "-c", "copy", "-movflags", "+faststart" });
            if (meta is not null) args.AddRange(new[] { "-map_chapters", "1" });
            if (Codec?.Codec == "hevc") args.AddRange(new[] { "-tag:v", "hvc1" });
            args.AddRange(new[] { "-f", "mp4", partial });
            ProcessResult r;
            try { r = await ProcessRunner.RunAsync(ffmpeg, args); }
            finally { if (meta is not null) try { File.Delete(meta); } catch { /* temp file */ } }
            if (r.ExitCode != 0 || !File.Exists(partial) || new FileInfo(partial).Length == 0)
            {
                try { File.Delete(partial); } catch { /* nothing written */ }
                Log.Warn($"Saving the session failed: {r.StdErr}");
                return null;
            }
            File.Move(partial, output, overwrite: false);
            Log.Info($"Session saved: {segments.Count * SegmentSeconds} s, {marks.Count} bookmark(s)");
            SessionSaved?.Invoke(output, marks);
            return output;
        }
        catch (Exception ex)
        {
            Log.Warn($"Saving the session failed: {ex.Message}");
            return null;
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    private static string SafeName(string text)
    {
        var name = string.Join("_", text.Split(Path.GetInvalidFileNameChars().Concat(new[] { '/', '\\', ':' }).ToArray())).Trim();
        return name.Length == 0 ? "Clip" : name;
    }

    /// <summary>Starts, restarts or stops capture so it matches the current settings.</summary>
    public async Task ApplyAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await FinishSessionCoreAsync(); // a running session is saved before capture restarts
            StopCore();
            _restarts = 0;
            if (Cfg.Enabled) await StartCoreAsync();
            else Set(ClipState.Off, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartCoreAsync()
    {
        Set(ClipState.Starting, null);

        if (OperatingSystem.IsLinux())
        {
            var wayland = string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland",
                StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || wayland)
            {
                Set(ClipState.Error, "Screen capture needs an X11 session; Wayland is not supported yet.");
                return;
            }
        }

        var ffmpeg = await CaptureProbe.FindCaptureFfmpegAsync();
        if (ffmpeg is null)
        {
            Set(ClipState.Error, OperatingSystem.IsLinux()
                ? "Screen capture needs ffmpeg from your distribution (sudo apt install ffmpeg)."
                : "No ffmpeg with screen capture support was found.");
            return;
        }

        if (OperatingSystem.IsMacOS() && !PlatformShell.EnsureScreenCaptureAccess())
        {
            Set(ClipState.Error,
                "Allow Cnema under System Settings › Privacy & Security › Screen Recording, then restart Cnema.");
            return;
        }

        var codec = await CaptureProbe.PickAsync(ffmpeg, Cfg.HardwareEncoder, Cfg.Codec);
        var encoder = codec.Encoder;
        Codec = codec;
        var ddagrab = await CaptureProbe.HasDdagrabAsync(ffmpeg);
        var devices = OperatingSystem.IsMacOS() ? await CaptureProbe.ListDevicesAsync() : null;
        Encoder = encoder;

        KillStaleCapture();
        await FinishSessionCoreAsync(); // capture died mid-session: keep what was recorded
        try
        {
            if (Directory.Exists(AppPaths.ClipBufferDir)) Directory.Delete(AppPaths.ClipBufferDir, true);
        }
        catch { /* stale segments are harmless: they sort before the new ones */ }
        Directory.CreateDirectory(AppPaths.ClipBufferDir);

        var args = BuildArguments(Cfg, encoder, ddagrab, devices, AppPaths.ClipBufferDir,
            Environment.GetEnvironmentVariable("DISPLAY"));
        if (!StartSystemAudio(devices)) return;
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Log.Info($"Capture starting: {encoder}, {Cfg.Fps} fps, {(Cfg.Height > 0 ? Cfg.Height + "p" : "native")}, {Cfg.BitrateMbps} Mbps");
        var generation = ++_generation;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        lock (_log) _log.Clear();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_log)
            {
                _log.Enqueue(e.Data);
                while (_log.Count > 12) _log.Dequeue();
            }
        };
        p.Exited += (_, _) => OnExited(generation);
        try
        {
            p.Start();
            p.BeginErrorReadLine();
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* not permitted: fine */ }
        }
        catch (Exception ex)
        {
            Set(ClipState.Error, "Could not start capture: " + ex.Message);
            return;
        }
        _process = p;
        try { File.WriteAllText(PidFile, p.Id.ToString(CultureInfo.InvariantCulture)); } catch { /* best effort */ }
        _startedAt = DateTime.UtcNow;
        _janitor = new Timer(_ => Tidy(generation), null, 1000, 1000);
    }

    /// <summary>Runs every second: flips Starting to Recording once data arrives, drops old segments.</summary>
    private void Tidy(int generation)
    {
        if (generation != _generation) return;
        try
        {
            var segments = Segments();
            // The newest segment can read as empty while ffmpeg holds it open; any finished one counts.
            if (State == ClipState.Starting && segments.Any(f => new FileInfo(f).Length > 0))
                Set(ClipState.Recording, null);
            if (State == ClipState.Recording && segments.Count > 0 &&
                DateTime.UtcNow - _startedAt > TimeSpan.FromSeconds(SegmentSeconds * 6) &&
                new FileInfo(segments[^1]).Length > (long)Math.Clamp(Cfg.BitrateMbps, 1, 100) * 125_000 * SegmentSeconds * 8)
            {
                // The newest segment is many times its expected size: segments are not rotating.
                ThreadPool.QueueUserWorkItem(_ => FailStuck(generation));
                return;
            }
            if (IsSession) return; // a session keeps every segment until it is saved
            var keep = SegmentsFor(Cfg.Seconds) + 2;
            if (!_gate.Wait(0)) return; // a save is reading the buffer right now
            try
            {
                foreach (var old in segments.Take(Math.Max(0, segments.Count - keep)))
                    File.Delete(old);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch { /* next tick */ }
    }

    private async void FailStuck(int generation)
    {
        await _gate.WaitAsync();
        try
        {
            if (generation != _generation) return;
            StopCore();
            await FinishSessionCoreAsync();
            Set(ClipState.Error, "The replay buffer stopped rotating, so capture was stopped to protect your disk.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnExited(int generation)
    {
        if (generation != _generation) return; // we stopped it on purpose
        string tail;
        lock (_log) tail = string.Join(" ", _log.Where(l => l.Trim().Length > 0).TakeLast(3)).Trim();
        var ranFor = DateTime.UtcNow - _startedAt;
        Log.Warn($"Capture ffmpeg exited after {ranFor.TotalSeconds:0} s: {tail}");
        _janitor?.Dispose();
        _janitor = null;
        _process = null;

        // A capture that had been healthy for a while (display sleep, device unplugged) gets retried.
        if (ranFor > TimeSpan.FromSeconds(30) && _restarts < 3 && Cfg.Enabled)
        {
            _restarts++;
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(async _ =>
            {
                await _gate.WaitAsync();
                try
                {
                    if (generation == _generation && Cfg.Enabled) await StartCoreAsync();
                }
                finally
                {
                    _gate.Release();
                }
            });
            Set(ClipState.Starting, null);
            return;
        }
        Set(ClipState.Error, tail.Length > 0 ? "Capture stopped: " + Shorten(tail) : "Capture stopped unexpectedly.");
    }

    private Process? _audioHelper;
    private static string SystemAudioFifo => Path.Combine(AppPaths.ClipBufferDir, "system-audio.pcm");

    private bool UsesSystemAudio(CaptureDevices? devices) =>
        OperatingSystem.IsMacOS() && devices?.Audio.Contains(CaptureProbe.SystemAudio) == true &&
        (Cfg.Audio1 == CaptureProbe.SystemAudio || Cfg.Audio2 == CaptureProbe.SystemAudio);

    /// <summary>
    /// macOS system audio: the helper writes PCM into a FIFO that ffmpeg reads as an extra input.
    /// Started before ffmpeg; the helper waits for ffmpeg to open the FIFO.
    /// </summary>
    private bool StartSystemAudio(CaptureDevices? devices)
    {
        if (!UsesSystemAudio(devices) || CaptureProbe.SystemAudioHelper is not { } helper) return true;
        try
        {
            using (var mk = Process.Start(new ProcessStartInfo("mkfifo", $"\"{SystemAudioFifo}\"") { UseShellExecute = false }))
                mk?.WaitForExit(3000);
            var psi = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            psi.ArgumentList.Add(SystemAudioFifo);
            _audioHelper = Process.Start(psi);
            _audioHelper?.BeginErrorReadLine();
            return true;
        }
        catch (Exception ex)
        {
            Set(ClipState.Error, "System audio could not start: " + ex.Message);
            return false;
        }
    }

    private void StopSystemAudio()
    {
        var helper = _audioHelper;
        _audioHelper = null;
        if (helper is null) return;
        try { if (!helper.HasExited) helper.Kill(); } catch { /* already gone */ }
        helper.Dispose();
    }

    private static string PidFile => Path.Combine(AppPaths.DataDir, "capture.pid");

    /// <summary>
    /// If Cnema was killed or crashed, its ffmpeg would keep recording with nobody to stop it.
    /// The capture's process id is noted on disk so the next start can clean that up.
    /// </summary>
    private static void KillStaleCapture()
    {
        try
        {
            if (!File.Exists(PidFile)) return;
            if (int.TryParse(File.ReadAllText(PidFile).Trim(), out var pid))
            {
                using var old = Process.GetProcessById(pid);
                if (old.ProcessName.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
                    old.Kill(entireProcessTree: true);
            }
        }
        catch { /* no such process any more */ }
        try { File.Delete(PidFile); } catch { /* best effort */ }
    }

    private void StopCore()
    {
        _generation++;
        _janitor?.Dispose();
        _janitor = null;
        var p = _process;
        _process = null;
        if (p is null) { StopSystemAudio(); return; }
        try
        {
            if (!p.HasExited)
            {
                p.StandardInput.Write("q");
                p.StandardInput.Flush();
                if (!p.WaitForExit(2000)) p.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
        p.Dispose();
        StopSystemAudio();
        try { File.Delete(PidFile); } catch { /* best effort */ }
    }

    /// <summary>Writes the last N seconds to the clips folder. Returns the new file, or null.</summary>
    public async Task<string?> SaveClipAsync(string? label = null)
    {
        if (State != ClipState.Recording) return null;
        await _gate.WaitAsync();
        try
        {
            var ffmpeg = await CaptureProbe.FindCaptureFfmpegAsync() ?? ToolLocator.Ffmpeg;
            var segments = Segments().TakeLast(SegmentsFor(Cfg.Seconds) + 1).ToList();
            if (ffmpeg is null || segments.Count == 0) return null;

            var folder = Cfg.ResolvedFolder;
            Directory.CreateDirectory(folder);
            var prefix = SafeName(label ?? "Clip");
            var output = Path.Combine(folder, $"{prefix} {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
            var list = Path.Combine(AppPaths.ClipBufferDir, "save.txt");
            await File.WriteAllLinesAsync(list, segments.Select(s => $"file '{s.Replace("'", "'\\''")}'"));

            // The newest segment is still being written, so the clip runs right up to "now". Its
            // exact length is the requested one plus at most one segment.
            var total = segments.Count * (double)SegmentSeconds;
            var skip = Math.Max(0, total - Cfg.Seconds - SegmentSeconds);
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0" };
            if (skip > 0) args.AddRange(new[] { "-ss", skip.ToString("0.###", CultureInfo.InvariantCulture) });
            // Written next to the buffer and moved into place when complete: the clips folder is a
            // watched library folder, and a half-written file would be scanned as a 0-second video.
            var partial = Path.Combine(AppPaths.ClipBufferDir, "saving.mp4");
            args.AddRange(new[] { "-i", list, "-map", "0", "-c", "copy", "-movflags", "+faststart" });
            if (Codec?.Codec == "hevc") args.AddRange(new[] { "-tag:v", "hvc1" }); // QuickTime / Photos need hvc1
            args.AddRange(new[] { "-f", "mp4", partial });

            var r = await ProcessRunner.RunAsync(ffmpeg, args);
            if (r.ExitCode != 0 || !File.Exists(partial) || new FileInfo(partial).Length == 0)
            {
                try { File.Delete(partial); } catch { /* nothing written */ }
                Log.Warn($"Saving the clip failed: {r.StdErr}");
                return null;
            }
            File.Move(partial, output, overwrite: false);
            ClipSaved?.Invoke(output);
            return output;
        }
        catch (Exception ex)
        {
            Log.Warn($"Saving the clip failed: {ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static int SegmentsFor(int seconds) => (int)Math.Ceiling(Math.Max(5, seconds) / (double)SegmentSeconds);

    private static List<string> Segments()
    {
        if (!Directory.Exists(AppPaths.ClipBufferDir)) return new List<string>();
        return Directory.EnumerateFiles(AppPaths.ClipBufferDir, "seg*.*")
            .Where(f => f.EndsWith(".ts") || f.EndsWith(".mkv"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The ffmpeg command line for the rolling capture. Pure function so it can be checked without
    /// a display: input 0 is the screen, inputs 1..2 are the optional audio devices.
    /// </summary>
    public static List<string> BuildArguments(ClipSettings s, string encoder, bool ddagrab,
        CaptureDevices? macDevices, string bufferDir, string? x11Display)
    {
        var fps = s.Fps is 30 or 60 ? s.Fps : 60;
        var inv = CultureInfo.InvariantCulture;
        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats", "-y" };
        var filters = new List<string>();
        var gpuFrames = false;

        // ---- video input ----
        if (OperatingSystem.IsMacOS())
        {
            var screens = macDevices?.Screens ?? Array.Empty<string>();
            var screen = screens.Count > 0 ? screens[Math.Clamp(s.Screen, 0, screens.Count - 1)] : "Capture screen 0";
            a.AddRange(new[]
            {
                "-f", "avfoundation", "-thread_queue_size", "1024", "-capture_cursor", "1",
                "-framerate", fps.ToString(inv), "-pixel_format", "nv12", "-i", screen + ":none",
            });
        }
        else if (OperatingSystem.IsWindows())
        {
            if (ddagrab)
            {
                a.AddRange(new[]
                {
                    "-f", "lavfi", "-thread_queue_size", "1024",
                    "-i", $"ddagrab=output_idx={Math.Max(0, s.Screen)}:framerate={fps}:draw_mouse=1",
                });
                gpuFrames = true;
            }
            else
            {
                a.AddRange(new[]
                {
                    "-f", "gdigrab", "-thread_queue_size", "1024", "-framerate", fps.ToString(inv),
                    "-draw_mouse", "1", "-i", "desktop",
                });
            }
        }
        else
        {
            a.AddRange(new[]
            {
                "-f", "x11grab", "-thread_queue_size", "1024", "-framerate", fps.ToString(inv),
                "-draw_mouse", "1", "-i", string.IsNullOrEmpty(x11Display) ? ":0" : x11Display,
            });
        }

        // ---- audio inputs ----
        var audio = new[] { s.Audio1, s.Audio2 }
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!).Distinct().ToList();
        if (OperatingSystem.IsMacOS())
        {
            // avfoundation addresses audio devices by index; drop names that are no longer plugged in.
            var known = macDevices?.Audio ?? Array.Empty<string>();
            audio = audio.Where(known.Contains).ToList();
            // The system audio entry is not an avfoundation device, so it does not count in the indexes.
            var avDevices = known.Where(n => n != CaptureProbe.SystemAudio).ToList();
            foreach (var name in audio)
                a.AddRange(name == CaptureProbe.SystemAudio
                    ? new[]
                    {
                        "-f", "f32le", "-ar", "48000", "-ac", "2", "-thread_queue_size", "1024",
                        "-i", Path.Combine(bufferDir, "system-audio.pcm"),
                    }
                    : new[]
                    {
                        "-f", "avfoundation", "-thread_queue_size", "1024",
                        "-i", ":" + avDevices.IndexOf(name).ToString(inv),
                    });
        }
        else if (OperatingSystem.IsWindows())
        {
            foreach (var name in audio)
                a.AddRange(new[] { "-f", "dshow", "-thread_queue_size", "1024", "-i", "audio=" + name });
        }
        else
        {
            foreach (var name in audio)
                a.AddRange(new[] { "-f", "pulse", "-thread_queue_size", "1024", "-i", name });
        }

        // ---- video filters ----
        // Desktop Duplication hands over GPU textures. NVENC and AMF encode those directly (no copy
        // through system memory); anything else, and any resize, needs them downloaded first.
        var direct = gpuFrames && s.Height <= 0 && (encoder.EndsWith("_nvenc") || encoder.EndsWith("_amf"));
        if (gpuFrames && !direct) filters.Add("hwdownload,format=bgra");
        if (s.Height > 0) filters.Add($"scale=-2:'min({s.Height},ih)':flags=fast_bilinear");
        if (!direct)
        {
            var alreadyNv12 = OperatingSystem.IsMacOS() && s.Height <= 0 && encoder != "libx264";
            if (!alreadyNv12) filters.Add(encoder == "libx264" ? "format=yuv420p" : "format=nv12");
        }
        if (filters.Count > 0) a.AddRange(new[] { "-vf", string.Join(",", filters) });

        // ---- mapping ----
        if (audio.Count == 2 && s.SeparateTracks)
        {
            // Two tracks (for example game and microphone) that the editor can mute independently.
            a.AddRange(new[] { "-map", "0:v", "-map", "1:a", "-map", "2:a" });
        }
        else if (audio.Count == 2)
        {
            a.AddRange(new[]
            {
                "-filter_complex", "[1:a][2:a]amix=inputs=2:duration=longest:normalize=0[mix]",
                "-map", "0:v", "-map", "[mix]",
            });
        }
        else if (audio.Count == 1) a.AddRange(new[] { "-map", "0:v", "-map", "1:a" });
        else a.AddRange(new[] { "-map", "0:v", "-an" });

        // ---- encoders ----
        var kbps = Math.Clamp(s.BitrateMbps, 1, 100) * 1000;
        a.AddRange(new[] { "-c:v", encoder, "-b:v", kbps + "k", "-maxrate", (int)(kbps * 1.5) + "k", "-bufsize", kbps * 2 + "k" });
        var family = encoder.Contains('_') ? encoder[(encoder.IndexOf('_') + 1)..] : encoder;
        switch (family)
        {
            case "videotoolbox": a.AddRange(new[] { "-realtime", "1" }); break;
            case "nvenc": a.AddRange(new[] { "-preset", "p4", "-forced-idr", "1" }); break;
            case "qsv": a.AddRange(new[] { "-preset", "fast" }); break;
            case "amf": a.AddRange(new[] { "-usage", "lowlatency" }); break;
            case "libx264": a.AddRange(new[] { "-preset", "superfast", "-tune", "zerolatency" }); break;
        }
        // A keyframe at every segment boundary so segments can be cut and joined without re-encoding.
        a.AddRange(new[]
        {
            "-g", (fps * SegmentSeconds).ToString(inv),
            "-force_key_frames", $"expr:gte(t,n_forced*{SegmentSeconds})",
        });
        if (audio.Count > 0)
            a.AddRange(new[] { "-c:a", "aac", "-b:a", Math.Clamp(s.AudioBitrateKbps, 64, 320) + "k", "-ac", "2", "-ar", "48000" });

        // ---- rolling segments ----
        // Constant frame rate output. Without it the segment muxer loses avfoundation's frame timing
        // on macOS: every frame is stamped 1/90000 s apart, segments never rotate (one file grows
        // forever) and saved clips claim to be 0 seconds long.
        a.AddRange(new[] { "-fps_mode", "cfr", "-r", fps.ToString(inv) });
        a.AddRange(new[]
        {
            "-f", "segment", "-segment_time", SegmentSeconds.ToString(inv),
            "-segment_format", encoder.StartsWith("av1") ? "matroska" : "mpegts",
            "-flush_packets", "1", Path.Combine(bufferDir, encoder.StartsWith("av1") ? "seg%07d.mkv" : "seg%07d.ts"),
        });
        return a;
    }

    private void Set(ClipState state, string? error)
    {
        State = state;
        Error = error;
        Changed?.Invoke();
    }

    private static string Shorten(string s) => s.Length <= 220 ? s : s[..220] + "…";

    public void Dispose()
    {
        if (IsSession)
        {
            // Quitting mid-session: save it rather than throw the recording away.
            try { Task.Run(StopSessionAsync).Wait(TimeSpan.FromSeconds(30)); } catch { /* shutting down */ }
        }
        StopCore();
        try
        {
            if (Directory.Exists(AppPaths.ClipBufferDir)) Directory.Delete(AppPaths.ClipBufferDir, true);
        }
        catch { /* temp folder; the OS cleans it up eventually */ }
    }
}
