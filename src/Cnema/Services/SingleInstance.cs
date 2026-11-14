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

    /// <summary>True if an already-running Cnema was found and told to show its window.</summary>
    public static bool SignalExisting()
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
