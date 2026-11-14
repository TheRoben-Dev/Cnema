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

    /// <summary>Raised on any thread when State/Error change.</summary>
    public event Action? Changed;
    /// <summary>Raised on any thread after a clip was written; the argument is its path.</summary>
    public event Action<string>? ClipSaved;

    public ClipService(SettingsStore settings) => _settings = settings;

    private ClipSettings Cfg => _settings.Current.Clip;

    /// <summary>Starts, restarts or stops capture so it matches the current settings.</summary>
    public async Task ApplyAsync()
    {
        await _gate.WaitAsync();
        try
        {
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

        var encoder = await CaptureProbe.PickEncoderAsync(ffmpeg, Cfg.HardwareEncoder);
        var ddagrab = await CaptureProbe.HasDdagrabAsync(ffmpeg);
        var devices = OperatingSystem.IsMacOS() ? await CaptureProbe.ListDevicesAsync() : null;
        Encoder = encoder;

        KillStaleCapture();
        try
        {
            if (Directory.Exists(AppPaths.ClipBufferDir)) Directory.Delete(AppPaths.ClipBufferDir, true);
        }
        catch { /* stale segments are harmless: they sort before the new ones */ }
        Directory.CreateDirectory(AppPaths.ClipBufferDir);

        var args = BuildArguments(Cfg, encoder, ddagrab, devices, AppPaths.ClipBufferDir,
            Environment.GetEnvironmentVariable("DISPLAY"));
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

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
            if (State == ClipState.Starting && segments.Count > 0 && new FileInfo(segments[^1]).Length > 0)
                Set(ClipState.Recording, null);
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

    private void OnExited(int generation)
    {
        if (generation != _generation) return; // we stopped it on purpose
        string tail;
        lock (_log) tail = string.Join(" ", _log.Where(l => l.Trim().Length > 0).TakeLast(3)).Trim();
        var ranFor = DateTime.UtcNow - _startedAt;
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
        if (p is null) return;
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
        try { File.Delete(PidFile); } catch { /* best effort */ }
    }

    /// <summary>Writes the last N seconds to the clips folder. Returns the new file, or null.</summary>
    public async Task<string?> SaveClipAsync()
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
            var output = Path.Combine(folder, $"Clip {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
            var list = Path.Combine(AppPaths.ClipBufferDir, "save.txt");
            await File.WriteAllLinesAsync(list, segments.Select(s => $"file '{s.Replace("'", "'\\''")}'"));

            // The newest segment is still being written, so the clip runs right up to "now". Its
            // exact length is the requested one plus at most one segment.
            var total = segments.Count * (double)SegmentSeconds;
            var skip = Math.Max(0, total - Cfg.Seconds - SegmentSeconds);
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "concat", "-safe", "0" };
            if (skip > 0) args.AddRange(new[] { "-ss", skip.ToString("0.###", CultureInfo.InvariantCulture) });
            args.AddRange(new[] { "-i", list, "-map", "0", "-c", "copy", "-movflags", "+faststart", output });

            var r = await ProcessRunner.RunAsync(ffmpeg, args);
            if (r.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
            {
                try { File.Delete(output); } catch { /* nothing written */ }
                Console.Error.WriteLine($"[Cnema] Saving the clip failed: {r.StdErr}");
                return null;
            }
            ClipSaved?.Invoke(output);
            return output;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Cnema] Saving the clip failed: {ex.Message}");
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
        return Directory.EnumerateFiles(AppPaths.ClipBufferDir, "seg*.ts")
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
            foreach (var name in audio)
                a.AddRange(new[]
                {
                    "-f", "avfoundation", "-thread_queue_size", "1024",
                    "-i", ":" + known.ToList().IndexOf(name).ToString(inv),
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
        var direct = gpuFrames && s.Height <= 0 && encoder is "h264_nvenc" or "h264_amf";
        if (gpuFrames && !direct) filters.Add("hwdownload,format=bgra");
        if (s.Height > 0) filters.Add($"scale=-2:'min({s.Height},ih)':flags=fast_bilinear");
        if (!direct)
        {
            var alreadyNv12 = OperatingSystem.IsMacOS() && s.Height <= 0 && encoder != "libx264";
            if (!alreadyNv12) filters.Add(encoder == "libx264" ? "format=yuv420p" : "format=nv12");
        }
        if (filters.Count > 0) a.AddRange(new[] { "-vf", string.Join(",", filters) });

        // ---- mapping ----
        if (audio.Count == 2)
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
        switch (encoder)
        {
            case "h264_videotoolbox": a.AddRange(new[] { "-realtime", "1" }); break;
            case "h264_nvenc": a.AddRange(new[] { "-preset", "p4", "-forced-idr", "1" }); break;
            case "h264_qsv": a.AddRange(new[] { "-preset", "fast" }); break;
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
        a.AddRange(new[]
        {
            "-f", "segment", "-segment_time", SegmentSeconds.ToString(inv), "-segment_format", "mpegts",
            "-flush_packets", "1", Path.Combine(bufferDir, "seg%07d.ts"),
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
        StopCore();
        try
        {
            if (Directory.Exists(AppPaths.ClipBufferDir)) Directory.Delete(AppPaths.ClipBufferDir, true);
        }
        catch { /* temp folder; the OS cleans it up eventually */ }
    }
}
