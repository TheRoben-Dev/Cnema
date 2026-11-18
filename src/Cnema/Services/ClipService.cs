using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

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
    /// <summary>Cancelled at quit when joining a session takes too long; the session is recovered next time.</summary>
    private readonly CancellationTokenSource _quitting = new();
    /// <summary>The camera made capture fail at the start, so it runs without it until settings change.</summary>
    private bool _withoutCamera;
    private bool _cameraUsed;
    /// <summary>Why the picked camera isn't in the clips, or null when it is (or none is picked).</summary>
    public string? CameraNote { get; private set; }
    /// <summary>Windows: Desktop Duplication (ddagrab) kept failing, so capture uses GDI until settings change.</summary>
    private bool _gdiFallback;
    /// <summary>The running (or last) capture used Desktop Duplication.</summary>
    private bool _usedDdagrab;
    /// <summary>Why capture switched to the compatible (GDI) mode, or null when it didn't.</summary>
    public string? CaptureNote { get; private set; }
    /// <summary>The raw ffmpeg output behind <see cref="Error"/>, for diagnostics; the card shows only the friendly text.</summary>
    public string? ErrorDetail { get; private set; }
    /// <summary>Which screen capture is in use: "ddagrab", "gdigrab", "avfoundation" or "x11grab".</summary>
    public string? CaptureMode { get; private set; }
    /// <summary>Self-test hook: makes Desktop Duplication fail at once, to exercise the fallback.</summary>
    internal static bool BreakDdagrabForTest;
    private DateTime _startedAt;

    public ClipState State { get; private set; } = ClipState.Off;
    public string? Error { get; private set; }
    public string? Encoder { get; private set; }
    public CodecOption? Codec { get; private set; }

    /// <summary>Raised on any thread when State/Error change.</summary>
    public event Action? Changed;
    /// <summary>Raised on any thread after a clip was written; the argument is its path.</summary>
    public event Action<string, int>? ClipSaved;

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
        WriteMarker();
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
        WriteMarker();
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
        try
        {
            return await JoinSessionAsync(new SessionMarker(first, label, started, marks, Codec?.Codec), recovered: false);
        }
        finally
        {
            DeleteMarker();
            Changed?.Invoke();
        }
    }

    private async Task<string?> JoinSessionAsync(SessionMarker session, bool recovered)
    {
        string? output = null;
        var marks = session.Bookmarks ?? new List<long>();
        try
        {
            var ffmpeg = await CaptureProbe.FindCaptureFfmpegAsync() ?? ToolLocator.Ffmpeg;
            var segments = Segments().Where(s => string.CompareOrdinal(Path.GetFileName(s), session.First) >= 0).ToList();
            if (ffmpeg is null || segments.Count == 0) return null;

            var folder = Cfg.ResolvedFolder;
            Directory.CreateDirectory(folder);
            var name = SafeName(session.Label is null ? "Session" : session.Label + " session");
            if (recovered) name += " (" + T("recovered") + ")";
            output = Path.Combine(folder, $"{name} {session.StartedUtc.ToLocalTime():yyyy-MM-dd HH-mm-ss}.mp4");
            if (File.Exists(output)) output = Path.Combine(folder, $"{name} {session.StartedUtc.ToLocalTime():yyyy-MM-dd HH-mm-ss} {DateTime.Now:HHmmss}.mp4");
            var list = Path.Combine(AppPaths.ClipBufferDir, "session.txt");
            await File.WriteAllLinesAsync(list, segments.Select(s => $"file '{s.Replace("'", "'\\''")}'"));
            // Same disk as the destination (a session can be many GB), under a name the library ignores.
            var partial = output + ".part";
            // Bookmarks become chapters inside the file, so other players can jump between them too.
            var chapters = Chapters.Enabled
                ? Chapters.FromBookmarks(marks, (long)(segments.Count * SegmentSeconds * 1000))
                : Array.Empty<Chapter>();
            var meta = chapters.Count > 1 ? Chapters.WriteMetadataFile(chapters) : null;
            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
            // The last segment of a recording that was cut short ends mid-packet; read past the damage.
            if (recovered) args.AddRange(new[] { "-err_detect", "ignore_err", "-fflags", "+discardcorrupt+genpts" });
            args.AddRange(new[] { "-f", "concat", "-safe", "0", "-i", list });
            if (meta is not null) args.AddRange(new[] { "-i", meta });
            args.AddRange(new[] { "-map", "0", "-c", "copy", "-movflags", "+faststart" });
            if (meta is not null) args.AddRange(new[] { "-map_chapters", "1" });
            if (session.Codec == "hevc") args.AddRange(new[] { "-tag:v", "hvc1" });
            args.AddRange(new[] { "-f", "mp4", partial });
            ProcessResult r;
            try { r = await ProcessRunner.RunAsync(ffmpeg, args, ct: _quitting.Token); }
            catch (OperationCanceledException)
            {
                // Quit before the join finished: drop the half-written file. The session marker stays,
                // so the whole session is saved at the next start instead.
                try { File.Delete(partial); } catch { /* best effort */ }
                Log.Info("Saving the session was stopped at quit; it will be recovered at the next start.");
                return null;
            }
            finally { if (meta is not null) try { File.Delete(meta); } catch { /* temp file */ } }
            if (!File.Exists(partial) || new FileInfo(partial).Length == 0 || (r.ExitCode != 0 && !recovered))
            {
                try { File.Delete(partial); } catch { /* nothing written */ }
                Log.Warn($"Saving the session failed: {r.StdErr}");
                return null;
            }
            File.Move(partial, output, overwrite: false);
            Log.Info($"Session {(recovered ? "recovered" : "saved")}: {segments.Count * SegmentSeconds} s, {marks.Count} bookmark(s)");
            SessionSaved?.Invoke(output, marks);
            return output;
        }
        catch (Exception ex)
        {
            Log.Warn($"Saving the session failed: {ex.Message}");
            return null;
        }
    }

    // ---------- crash safety ----------
    // While a session runs, a small file next to the segments says where it starts, what it is called and
    // where the bookmarks are. If Cnema never gets to save the session (a crash, a power cut, a force quit),
    // the file is still there at the next start and the segments are joined into a "(recovered)" video.

    private sealed record SessionMarker(string First, string? Label, DateTime StartedUtc, List<long>? Bookmarks, string? Codec);

    private static string MarkerFile => Path.Combine(AppPaths.ClipBufferDir, "session.json");

    private void WriteMarker()
    {
        if (_sessionStart is not { } started || _sessionFirst is not { } first) return;
        try
        {
            List<long> marks;
            lock (_bookmarks) marks = _bookmarks.ToList();
            var json = JsonSerializer.Serialize(new SessionMarker(first, _sessionLabel, started, marks, Codec?.Codec));
            var tmp = MarkerFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, MarkerFile, overwrite: true);
        }
        catch (Exception ex) { Log.Warn($"Couldn't write the session marker: {ex.Message}"); }
    }

    private static void DeleteMarker()
    {
        try { File.Delete(MarkerFile); } catch { /* not there */ }
    }

    /// <summary>
    /// Saves a session that an earlier run of Cnema never finished. The caller holds the gate, and capture
    /// is not writing to the buffer. Does nothing without a marker, or when crash-safe sessions are off.
    /// </summary>
    private async Task<string?> RecoverCoreAsync()
    {
        if (IsSession || !File.Exists(MarkerFile)) return null;
        SessionMarker? marker = null;
        try { marker = JsonSerializer.Deserialize<SessionMarker>(File.ReadAllText(MarkerFile)); }
        catch (Exception ex) { Log.Warn($"The session marker couldn't be read: {ex.Message}"); }
        DeleteMarker(); // one attempt only: a broken recording must not block capture at every start
        if (marker is null || string.IsNullOrEmpty(marker.First) || !_settings.Current.Features.RecoverSessions) return null;
        Log.Info("Recovering a session that wasn't saved");
        try { return await JoinSessionAsync(marker, recovered: true); }
        finally { Changed?.Invoke(); }
    }

    /// <summary>
    /// At launch: saves an unfinished session from an earlier run when capture isn't about to do it
    /// (clipping off), and clears the buffer left behind by older versions in the temp folder.
    /// </summary>
    public async Task RecoverAtLaunchAsync()
    {
        try
        {
            if (Directory.Exists(AppPaths.LegacyClipBufferDir)) Directory.Delete(AppPaths.LegacyClipBufferDir, true);
        }
        catch { /* temp folder */ }
        await _gate.WaitAsync();
        try
        {
            if (State is ClipState.Starting or ClipState.Recording) return; // capture start handles it
            KillStaleCapture();
            await RecoverCoreAsync();
            try
            {
                if (Directory.Exists(AppPaths.ClipBufferDir)) Directory.Delete(AppPaths.ClipBufferDir, true);
            }
            catch { /* removed on the next capture start */ }
        }
        finally
        {
            _gate.Release();
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
            // Saving Settings for something unrelated (the theme, a feature switch) must not cut a
            // running session in two or empty the replay buffer: restart only when capture changed.
            var signature = CaptureSignature(Cfg);
            if (Cfg.Enabled && State is (ClipState.Recording or ClipState.Starting) && signature == _runningSignature) return;
            _runningSignature = Cfg.Enabled ? signature : null;
            await FinishSessionCoreAsync(); // a running session is saved before capture restarts
            StopCore();
            _restarts = 0;
            _withoutCamera = false;
            _gdiFallback = false; // switching clipping off and on tries the fast capture again
            CaptureNote = null;
            CameraNote = null;
            if (Cfg.Enabled) await StartCoreAsync();
            else Set(ClipState.Off, null);
        }
        catch (Exception ex)
        {
            // A full or read-only disk, a probe that throws: say so instead of "Starting" forever.
            Log.Error("Starting capture failed", ex);
            _runningSignature = null;
            Set(ClipState.Error, T("Capture could not start: ") + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The settings the capture process was started with, to tell whether a restart is needed.</summary>
    private string? _runningSignature;

    private static string CaptureSignature(ClipSettings c) => string.Join("|",
        c.Fps, c.Height, c.BitrateMbps, c.AudioBitrateKbps, c.Codec, c.HardwareEncoder, c.Screen,
        c.Audio1, c.Audio2, c.Camera, c.CameraCorner, c.CameraSize, c.SeparateTracks);

    private async Task StartCoreAsync()
    {
        Set(ClipState.Starting, null);

        if (OperatingSystem.IsLinux())
        {
            var wayland = string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland",
                StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || wayland)
            {
                Set(ClipState.Error, T("Screen capture needs an X11 session; Wayland is not supported yet."));
                return;
            }
        }

        var ffmpeg = await CaptureProbe.FindCaptureFfmpegAsync();
        if (ffmpeg is null)
        {
            Set(ClipState.Error, OperatingSystem.IsLinux()
                ? T("Screen capture needs ffmpeg from your distribution (sudo apt install ffmpeg).")
                : T("No ffmpeg with screen capture support was found."));
            return;
        }

        if (OperatingSystem.IsMacOS() && !PlatformShell.EnsureScreenCaptureAccess())
        {
            Set(ClipState.Error,
                T("Allow Savefile under System Settings › Privacy & Security › Screen Recording, then restart Savefile."));
            return;
        }

        var codec = await CaptureProbe.PickAsync(ffmpeg, Cfg.HardwareEncoder, Cfg.Codec);
        var encoder = codec.Encoder;
        Codec = codec;
        var ddagrab = !_gdiFallback && await CaptureProbe.HasDdagrabAsync(ffmpeg);
        _usedDdagrab = ddagrab;
        CaptureMode = OperatingSystem.IsMacOS() ? "avfoundation" : OperatingSystem.IsWindows() ? (ddagrab ? "ddagrab" : "gdigrab") : "x11grab";
        var devices = OperatingSystem.IsMacOS() || !string.IsNullOrWhiteSpace(Cfg.Camera)
            ? await CaptureProbe.ListDevicesAsync()
            : null;
        Encoder = encoder;

        KillStaleCapture();
        await FinishSessionCoreAsync(); // capture died mid-session: keep what was recorded
        await RecoverCoreAsync(); // Cnema itself died mid-session last time
        try
        {
            if (Directory.Exists(AppPaths.ClipBufferDir)) Directory.Delete(AppPaths.ClipBufferDir, true);
        }
        catch { /* stale segments are harmless: they sort before the new ones */ }
        Directory.CreateDirectory(AppPaths.ClipBufferDir);

        var cfg = _withoutCamera ? Cfg.WithoutCamera() : Cfg;
        _cameraUsed = !string.IsNullOrWhiteSpace(cfg.Camera) && devices?.Cameras.Contains(cfg.Camera) == true;
        if (!string.IsNullOrWhiteSpace(cfg.Camera) && !_cameraUsed)
            CameraNote = F("{0} isn't connected, so clips are recorded without the camera.", cfg.Camera);
        // GDI captures the whole desktop; with several screens, cut out the one picked in settings.
        var gdiArea = OperatingSystem.IsWindows() && !ddagrab ? WindowsScreens.Area(cfg.Screen) : null;
        var args = BuildArguments(cfg, encoder, ddagrab, devices, AppPaths.ClipBufferDir,
            Environment.GetEnvironmentVariable("DISPLAY"), gdiArea);
        if (ddagrab && BreakDdagrabForTest)
            for (var i = 0; i < args.Count; i++)
                if (args[i].StartsWith("ddagrab=")) args[i] = System.Text.RegularExpressions.Regex.Replace(args[i], @"output_idx=\d+", "output_idx=97");
        if (!StartSystemAudio(devices)) return;
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        Log.Info($"Capture starting: {CaptureMode}, {encoder}, {Cfg.Fps} fps, {(Cfg.Height > 0 ? Cfg.Height + "p" : "native")}, {Cfg.BitrateMbps} Mbps{(_cameraUsed ? ", with camera" : "")}{(gdiArea is { } ga ? $", area {ga}" : "")}");
        var generation = ++_generation;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        lock (_log) _log.Clear();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_log)
            {
                _log.Enqueue(e.Data);
                while (_log.Count > 20) _log.Dequeue();
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
            Set(ClipState.Error, T("Could not start capture: ") + ex.Message);
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
            var keep = SegmentsFor(Cfg.BufferSeconds) + 2;
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
            Set(ClipState.Error, T("The replay buffer stopped rotating, so capture was stopped to protect your disk."));
        }
        catch (Exception ex)
        {
            // async void on a pool thread: an exception escaping here would take the whole app down.
            Log.Error("Stopping a stuck capture failed", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void OnExited(int generation)
    {
        if (generation != _generation) return; // we stopped it on purpose
        string detail;
        lock (_log) detail = string.Join(" | ", _log.Select(l => l.Trim()).Where(l => l.Length > 0)).Trim();
        var ranFor = DateTime.UtcNow - _startedAt;
        Log.Warn($"Capture ffmpeg ({CaptureMode}) exited after {ranFor.TotalSeconds:0} s: {detail}");
        _janitor?.Dispose();
        _janitor = null;
        var dead = _process;
        _process = null;
        try { dead?.Dispose(); } catch { /* already gone */ }
        // The restart starts its own audio helper; this one would otherwise be left running.
        StopSystemAudio();

        // A camera that can't be opened (in use, no permission) must not stop clipping: go on without it.
        if (_cameraUsed && ranFor < TimeSpan.FromSeconds(30) && Cfg.Enabled)
        {
            _withoutCamera = true;
            CameraNote = T(OperatingSystem.IsMacOS()
                ? "The camera could not be opened, so clips are recorded without it. Check that no other app is using it and that Savefile may use it (System Settings › Privacy & Security › Camera)."
                : "The camera could not be opened, so clips are recorded without it. Check that no other app is using it.");
            _ = Task.Run(() => RestartAsync(generation));
            Set(ClipState.Starting, null);
            return;
        }

        // Capture is retried by itself. Windows takes the screen away from Desktop Duplication whenever a
        // game switches between fullscreen and windowed, the display mode or HDR changes, or the PC locks
        // (DXGI "access lost"), and ffmpeg's ddagrab then quits with "Generic error in an external library".
        // A capture that ran a couple of minutes is always worth another go; quick failures get a few
        // tries, and when Desktop Duplication keeps failing at once, the slower GDI capture takes over.
        if (ranFor > TimeSpan.FromMinutes(2)) _restarts = 0;
        var quick = ranFor < TimeSpan.FromSeconds(30);
        if (Cfg.Enabled && _restarts < MaxRestarts)
        {
            _restarts++;
            if (quick && _usedDdagrab && _restarts >= 2)
            {
                _gdiFallback = true;
                CaptureNote = T("Windows' fast screen capture kept stopping, so Savefile switched to its compatible capture. Games in exclusive fullscreen may record black there: borderless or windowed works best.");
                Log.Warn("Capture: Desktop Duplication keeps failing, switching to GDI capture");
            }
            var delay = quick ? TimeSpan.FromSeconds(2 * _restarts) : TimeSpan.FromSeconds(3);
            _ = Task.Run(async () =>
            {
                await Task.Delay(delay);
                await RestartAsync(generation);
            });
            Set(ClipState.Starting, null);
            return;
        }
        SetError(FriendlyError(detail), detail);
    }

    private const int MaxRestarts = 4;

    /// <summary>
    /// Plain words for why capture stopped, from ffmpeg's last lines. The raw lines go to the log and the
    /// diagnostics (<see cref="ErrorDetail"/>), never onto the card.
    /// </summary>
    public static string FriendlyError(string detail)
    {
        var d = detail.ToLowerInvariant();
        string why;
        if (d.Contains("no space left") || d.Contains("not enough space") || d.Contains("disk full"))
            why = T("The drive with the replay buffer is full. Free up some space.");
        else if (d.Contains("openencodesession") || d.Contains("no capable devices") || d.Contains("error while opening encoder")
                 || d.Contains("nvenc") && d.Contains("fail") || d.Contains("_amf") && d.Contains("fail") || d.Contains("_qsv") && d.Contains("fail"))
            why = T("The graphics card's video encoder wouldn't start. Update the graphics driver, or pick another encoder under Settings › Clipping.");
        else if (d.Contains("ddagrab") || d.Contains("acquirenextframe") || d.Contains("duplicat") || d.Contains("gdigrab"))
            why = T("Windows kept taking the screen away from Savefile. That can happen when a game switches between fullscreen and windowed, the display or HDR changes, or the PC locks. Savefile tried again a few times.");
        else if (d.Contains("dshow") || d.Contains("audio=") || d.Contains("avfoundation") && d.Contains("audio") || d.Contains("pulse"))
            why = T("A sound device couldn't be opened. Check that it's plugged in, or pick another one under Settings › Clipping.");
        else
            why = T("Capture kept stopping, even after Savefile tried again a few times.");
        return why + " " + T("Switch clipping off and on to try again.");
    }

    /// <summary>Starts capture again after it quit by itself; a failure shows as an error instead of "Starting" forever.</summary>
    private async Task RestartAsync(int generation)
    {
        await _gate.WaitAsync();
        try
        {
            if (generation == _generation && Cfg.Enabled) await StartCoreAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Restarting capture failed", ex);
            Set(ClipState.Error, T("Capture stopped unexpectedly."));
        }
        finally
        {
            _gate.Release();
        }
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
            var psi = new ProcessStartInfo(helper)
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardInput = true };
            psi.ArgumentList.Add(SystemAudioFifo);
            var proc = Process.Start(psi);
            _audioHelper = proc;
            proc?.BeginErrorReadLine();
            // The helper plays the save sound itself, so the system audio it records leaves that sound out.
            if (proc is not null) PlatformShell.PlayUnrecorded = file => PlayThroughHelper(proc, file);
            return true;
        }
        catch (Exception ex)
        {
            Set(ClipState.Error, T("System audio could not start: ") + ex.Message);
            return false;
        }
    }

    private static bool PlayThroughHelper(Process helper, string file)
    {
        try
        {
            if (helper.HasExited || file.Contains('\n')) return false;
            helper.StandardInput.WriteLine("play " + file);
            helper.StandardInput.Flush();
            return true;
        }
        catch { return false; } // helper went away: play it the normal way
    }

    private void StopSystemAudio()
    {
        PlatformShell.PlayUnrecorded = null;
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
    public async Task<string?> SaveClipAsync(string? label = null, int? seconds = null)
    {
        var length = seconds ?? Cfg.Seconds;
        if (State != ClipState.Recording) return null;
        await _gate.WaitAsync();
        try
        {
            var ffmpeg = await CaptureProbe.FindCaptureFfmpegAsync() ?? ToolLocator.Ffmpeg;
            var segments = Segments().TakeLast(SegmentsFor(length) + 1).ToList();
            if (ffmpeg is null || segments.Count == 0) return null;

            var folder = Cfg.ResolvedFolder;
            Directory.CreateDirectory(folder);
            var prefix = SafeName(label ?? "Clip");
            var stamp = $"{prefix} {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
            var output = Path.Combine(folder, stamp + ".mp4");
            // Two saves in the same second (a double press, or two hotkeys) both keep their clip.
            for (var n = 2; File.Exists(output); n++) output = Path.Combine(folder, $"{stamp} ({n}).mp4");
            var list = Path.Combine(AppPaths.ClipBufferDir, "save.txt");
            await File.WriteAllLinesAsync(list, segments.Select(s => $"file '{s.Replace("'", "'\\''")}'"));

            // The newest segment is still being written, so the clip runs right up to "now". Its
            // exact length is the requested one plus at most one segment.
            var total = segments.Count * (double)SegmentSeconds;
            var skip = Math.Max(0, total - length - SegmentSeconds);
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
            ClipSaved?.Invoke(output, length);
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
    /// a display: input 0 is the screen, inputs 1..2 are the optional audio devices, and the camera
    /// (if one is picked and plugged in) comes after them. <paramref name="devices"/> is what
    /// CaptureProbe found; macOS needs it for the screens and audio devices, every OS for the camera.
    /// </summary>
    public static List<string> BuildArguments(ClipSettings s, string encoder, bool ddagrab,
        CaptureDevices? devices, string bufferDir, string? x11Display, ScreenArea? gdiArea = null)
    {
        var fps = s.Fps is 30 or 60 ? s.Fps : 60;
        var inv = CultureInfo.InvariantCulture;
        var a = new List<string> { "-hide_banner", "-loglevel", "warning", "-nostats", "-y" };
        var filters = new List<string>();
        var gpuFrames = false;

        // ---- video input ----
        if (OperatingSystem.IsMacOS())
        {
            var screens = devices?.Screens ?? Array.Empty<string>();
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
                a.AddRange(new[] { "-f", "gdigrab", "-thread_queue_size", "1024", "-framerate", fps.ToString(inv), "-draw_mouse", "1" });
                if (gdiArea is { } area)
                    a.AddRange(new[]
                    {
                        "-offset_x", area.X.ToString(inv), "-offset_y", area.Y.ToString(inv),
                        "-video_size", $"{area.Width}x{area.Height}",
                    });
                a.AddRange(new[] { "-i", "desktop" });
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
            var known = devices?.Audio ?? Array.Empty<string>();
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

        // ---- camera input ----
        var camera = !string.IsNullOrWhiteSpace(s.Camera) && devices?.Cameras.Contains(s.Camera) == true ? s.Camera : null;
        var cameraInput = 1 + audio.Count;
        if (camera is not null)
        {
            if (OperatingSystem.IsMacOS())
                // avfoundation's default rate (29.97) is one most webcams refuse; 30 they all offer.
                a.AddRange(new[] { "-f", "avfoundation", "-thread_queue_size", "512", "-framerate", "30", "-i", camera + ":none" });
            else if (OperatingSystem.IsWindows())
                a.AddRange(new[] { "-f", "dshow", "-thread_queue_size", "512", "-rtbufsize", "64M", "-i", "video=" + camera });
            else
                a.AddRange(new[] { "-f", "v4l2", "-thread_queue_size", "512", "-i", CaptureProbe.CameraInput(camera) });
        }

        // ---- video filters ----
        // Desktop Duplication hands over GPU textures. NVENC and AMF encode those directly (no copy
        // through system memory); anything else, any resize and the camera need them downloaded first.
        var direct = camera is null && gpuFrames && s.Height <= 0 && (encoder.EndsWith("_nvenc") || encoder.EndsWith("_amf"));
        if (gpuFrames && !direct) filters.Add("hwdownload,format=bgra");
        if (s.Height > 0) filters.Add($"scale=-2:'min({s.Height},ih)':flags=fast_bilinear");
        string? format = null;
        if (!direct)
        {
            var alreadyNv12 = OperatingSystem.IsMacOS() && s.Height <= 0 && encoder != "libx264" && camera is null;
            if (!alreadyNv12) format = encoder == "libx264" ? "format=yuv420p" : "format=nv12";
        }
        var graphs = new List<string>();
        var video = "0:v";
        if (camera is null)
        {
            if (format is not null) filters.Add(format);
            if (filters.Count > 0) a.AddRange(new[] { "-vf", string.Join(",", filters) });
        }
        else
        {
            graphs.AddRange(CameraGraph(filters, cameraInput, s.CameraCorner, s.CameraSize, format ?? "format=nv12"));
            video = "[v]";
        }

        // ---- mapping ----
        var maps = new List<string> { "-map", video };
        if (audio.Count == 2 && s.SeparateTracks)
        {
            // Two tracks (for example game and microphone) that the editor can mute independently.
            maps.AddRange(new[] { "-map", "1:a", "-map", "2:a" });
        }
        else if (audio.Count == 2)
        {
            graphs.Add("[1:a][2:a]amix=inputs=2:duration=longest:normalize=0[mix]");
            maps.AddRange(new[] { "-map", "[mix]" });
        }
        else if (audio.Count == 1) maps.AddRange(new[] { "-map", "1:a" });
        else maps.Add("-an");
        if (graphs.Count > 0) a.AddRange(new[] { "-filter_complex", string.Join(";", graphs) });
        a.AddRange(maps);

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

    /// <summary>
    /// The screen (after <paramref name="screenFilters"/>) with the camera in a corner, as [v]. The
    /// camera is sized against the screen picture, so it stays the same share of it at any resolution.
    /// </summary>
    public static IEnumerable<string> CameraGraph(IReadOnlyList<string> screenFilters, int cameraInput,
        string corner, int sizePercent, string format)
    {
        var inv = CultureInfo.InvariantCulture;
        var share = (Math.Clamp(sizePercent, 10, 50) / 100.0).ToString("0.###", inv);
        // A gap of 3 % of the height between the camera and the edges.
        var x = corner.EndsWith("left") ? "main_h*0.03" : "main_w-overlay_w-main_h*0.03";
        var y = corner.StartsWith("top") ? "main_h*0.03" : "main_h-overlay_h-main_h*0.03";
        yield return $"[0:v]{(screenFilters.Count > 0 ? string.Join(",", screenFilters) : "null")}[screen]";
        yield return $"[{cameraInput}:v][screen]scale2ref=w='trunc(oh*mdar/2)*2':h='trunc(ih*{share}/2)*2'[cam][base]";
        yield return $"[base][cam]overlay=x='{x}':y='{y}':eof_action=pass,{format}[v]";
    }

    private void Set(ClipState state, string? error)
    {
        State = state;
        Error = error;
        ErrorDetail = null;
        Changed?.Invoke();
    }

    private void SetError(string friendly, string detail)
    {
        State = ClipState.Error;
        Error = friendly;
        ErrorDetail = detail.Length > 0 ? detail : null;
        Changed?.Invoke();
    }


    public void Dispose()
    {
        if (IsSession)
        {
            // Quitting mid-session: save it rather than throw the recording away.
            try
            {
                if (!Task.Run(StopSessionAsync).Wait(TimeSpan.FromSeconds(60))) _quitting.Cancel();
            }
            catch { /* shutting down */ }
        }
        StopCore();
        // A session that couldn't be saved in time stays on disk and is recovered at the next start.
        if (File.Exists(MarkerFile)) return;
        try
        {
            if (Directory.Exists(AppPaths.ClipBufferDir)) Directory.Delete(AppPaths.ClipBufferDir, true);
        }
        catch { /* cache folder; cleared on the next capture start */ }
    }
}
