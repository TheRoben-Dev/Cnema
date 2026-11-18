using System.IO.Pipes;

namespace Cnema.Services;

/// <summary>
/// Cnema keeps running in the tray, so opening it again must bring back the running copy instead
/// of starting a second one (which would also fight over the capture). Named pipes work the same
/// on all three platforms.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static string PipeName => "cnema-" + Environment.UserName;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Raised on a background thread when another launch asked this one to show itself.</summary>
    public event Action? ShowRequested;

    private static Mutex? _mutex;

    /// <summary>True if an already-running Cnema was found and told to show its window.</summary>
    public static bool SignalExisting()
    {
        if (TrySignal()) return true;
        if (!OperatingSystem.IsWindows()) return false;
        // The pipe can miss a running copy (between two connections, or its listener broke), and on Windows
        // that left two copies running side by side (an installed one next to an older unzipped one), both
        // fighting over the capture. The mutex can't miss: it lives exactly as long as the copy holding it.
        try
        {
            _mutex = new Mutex(true, "cnema-" + Environment.UserName, out var created);
            if (created) return false;
            // An update relaunch can start before the old copy has quite quit: give it a moment.
            try { if (_mutex.WaitOne(TimeSpan.FromSeconds(10))) return false; }
            catch (AbandonedMutexException) { return false; } // the old copy died: it's ours now
            Log.Info("Another Savefile copy is already running; asked it to show itself");
            TrySignal();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Single-instance check failed: {ex.Message}");
            return false;
        }
    }

    private static bool TrySignal()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(250);
            client.WriteByte(1);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Listen() => _ = Task.Run(async () =>
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(_cts.Token);
                if (server.ReadByte() == 1) ShowRequested?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                await Task.Delay(1000);
            }
        }
    });

    public void Dispose() => _cts.Cancel();
}
