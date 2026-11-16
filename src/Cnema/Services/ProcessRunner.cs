using System.Diagnostics;
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
