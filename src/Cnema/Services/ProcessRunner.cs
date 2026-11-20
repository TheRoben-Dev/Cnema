using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Cnema.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

public static class ProcessRunner
{
    /// <summary>Background work (the smart features): give way to games and everything else.</summary>
    public static void Lower(Process p)
    {
        // Not Idle on macOS: nice 19 lands in the kernel's throttled tier there (efficiency cores only and
        // throttled disk reads), so ffmpeg barely moved while anything else was busy. Below normal still gives way.
        try { p.PriorityClass = OperatingSystem.IsWindows() ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal; }
        catch { /* gone already, or not allowed: runs at normal priority */ }
        BackgroundHold.Track(p);
    }

    public static async Task<ProcessResult> RunAsync(
        string file, IEnumerable<string> args, Action<string>? onStdOutLine = null, CancellationToken ct = default,
        string? workingDirectory = null, bool background = false)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = workingDirectory ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            stdout.AppendLine(e.Data);
            onStdOutLine?.Invoke(e.Data);
        };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        p.Start();
        if (background) Lower(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        // Kill straight from the token, not from the await's continuation: when the app quits, the UI
        // thread's dispatcher may never run that continuation, and ffmpeg would keep encoding alone.
        using var killOnCancel = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch { /* already gone */ } });
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            // Wait (briefly) for it to be gone, so its files are closed before the caller cleans up.
            try { p.WaitForExit(3000); } catch { /* already gone */ }
            throw;
        }
        return new ProcessResult(p.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

/// <summary>
/// Freezes the background work's processes (the smart features' ffmpeg runs) while a video plays in
/// Savefile, and lets them carry on after. Lower priority alone wasn't enough: on macOS indexing shares the
/// hardware video decoder with libVLC and made playback crackle. Time limits on that work count only the
/// time it was allowed to run (<see cref="CancelAfterActive"/>), so a long watch never makes a video "too slow".
/// macOS and Linux only (SIGSTOP / SIGCONT); on Windows the work just runs at idle priority.
/// </summary>
public static class BackgroundHold
{
    private static readonly object Sync = new();
    private static readonly HashSet<Process> Running = new();
    private static bool _held;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static TimeSpan _heldBefore, _heldSince;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int sig);

    private static bool Supported => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();
    private static int SigStop => OperatingSystem.IsMacOS() ? 17 : 19;
    private static int SigCont => OperatingSystem.IsMacOS() ? 19 : 18;

    public static bool IsHeld { get { lock (Sync) return _held; } }

    private static CancellationTokenSource _heldCts = new();

    /// <summary>
    /// Cancelled when the next hold starts. Work that runs inside Savefile (the speech and other models), which
    /// freezing ffmpeg doesn't reach, links to it and stops when a video starts playing, to be done again later.
    /// </summary>
    public static CancellationToken WhenHeld { get { lock (Sync) return _heldCts.Token; } }

    /// <summary>All the time background work has been held so far, including a hold that is still on.</summary>
    public static TimeSpan HeldTotal
    {
        get { lock (Sync) return _heldBefore + (_held ? Clock.Elapsed - _heldSince : TimeSpan.Zero); }
    }

    internal static void Track(Process p)
    {
        if (!Supported) return;
        lock (Sync)
        {
            try { if (p.HasExited) return; } catch { return; }
            Running.Add(p);
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => { lock (Sync) Running.Remove(p); };
            if (_held) Signal(p, SigStop);
        }
    }

    /// <summary>Holds (true) or releases (false) every background process, now and any that start meanwhile.</summary>
    public static void Set(bool hold)
    {
        CancellationTokenSource? stop = null;
        lock (Sync)
        {
            if (hold == _held) return;
            _held = hold;
            if (hold) { _heldSince = Clock.Elapsed; stop = _heldCts; }
            else { _heldBefore += Clock.Elapsed - _heldSince; _heldCts = new CancellationTokenSource(); }
            // Freezing processes is for macOS and Linux; on Windows the work runs at idle priority instead.
            if (Supported) foreach (var p in Running.ToList()) Signal(p, hold ? SigStop : SigCont);
        }
        // Off the calling thread: cancelling runs the work's own callbacks right there, and with them whatever was
        // awaiting that work (killing ffmpeg, a model winding down). Called from the UI thread when anything starts
        // playing, that froze the window while the passes stopped (the voice game, the moment it played a snippet).
        if (stop is not null)
            _ = Task.Run(() =>
            {
                var watch = Stopwatch.StartNew();
                try { stop.Cancel(); }
                catch (AggregateException ex) { Log.Warn($"Background hold: {ex.InnerException?.Message}"); }
                if (watch.ElapsedMilliseconds > 500) Log.Info($"Background hold: stopping the work took {watch.ElapsedMilliseconds} ms");
            });
    }

    private static void Signal(Process p, int sig)
    {
        try
        {
            if (p.HasExited) { Running.Remove(p); return; }
            Kill(p.Id, sig);
        }
        catch { Running.Remove(p); /* gone already */ }
    }

    /// <summary>Like CancelAfter, but time spent held doesn't count.</summary>
    public static void CancelAfterActive(CancellationTokenSource cts, TimeSpan limit) => _ = RunLimitAsync(cts, limit);

    private static async Task RunLimitAsync(CancellationTokenSource cts, TimeSpan limit)
    {
        CancellationToken token;
        try { token = cts.Token; } catch (ObjectDisposedException) { return; }
        var start = Clock.Elapsed;
        var heldAtStart = HeldTotal;
        while (!token.IsCancellationRequested)
        {
            var active = Clock.Elapsed - start - (HeldTotal - heldAtStart);
            if (active >= limit)
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { /* the work finished meanwhile */ }
                return;
            }
            try { await Task.Delay(limit - active, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
        }
    }
}
