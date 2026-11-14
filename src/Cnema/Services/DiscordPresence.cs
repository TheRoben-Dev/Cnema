using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Cnema.Services;

/// <summary>
/// Discord Rich Presence ("Playing Cnema · Editing a clip") over Discord's local IPC socket: a named
/// pipe on Windows, a Unix socket elsewhere. No Discord SDK and no network; if Discord isn't
/// running, nothing happens and it tries again a little later.
/// </summary>
public sealed class DiscordPresence : IDisposable
{
    /// <summary>The "Cnema" application in Discord's developer portal; its name is what Discord shows.</summary>
    public const string ApplicationId = "";

    public static bool IsAvailable => ApplicationId.Length > 0;

    private readonly object _lock = new();
    private readonly Timer _timer;
    private JsonObject? _wanted;
    private string? _sent;
    private Stream? _stream;
    private DateTime _lastSend = DateTime.MinValue;
    private DateTime _lastAttempt = DateTime.MinValue;
    private int _pumping;
    private bool _disposed;

    public DiscordPresence() => _timer = new Timer(_ => Pump(), null, 1000, 2000);

    /// <summary>Shows an activity; null <paramref name="details"/> clears it.</summary>
    public void Set(string? details, string? state, DateTimeOffset? since)
    {
        JsonObject? activity = null;
        if (details is not null)
        {
            activity = new JsonObject
            {
                ["details"] = Limit(details),
                ["assets"] = new JsonObject { ["large_image"] = "cnema", ["large_text"] = "Cnema" },
            };
            if (!string.IsNullOrWhiteSpace(state)) activity["state"] = Limit(state);
            if (since is { } t) activity["timestamps"] = new JsonObject { ["start"] = t.ToUnixTimeSeconds() };
        }
        lock (_lock) _wanted = activity;
    }

    public void Clear() => Set(null, null, null);

    // Discord wants 2 to 128 characters.
    private static string Limit(string text) =>
        text.Length > 128 ? text[..127] + "…" : text.Length < 2 ? text.PadRight(2) : text;

    private void Pump()
    {
        if (!IsAvailable || Interlocked.Exchange(ref _pumping, 1) == 1) return;
        try
        {
            string? json;
            lock (_lock) json = _wanted?.ToJsonString();
            if (json == _sent) return;
            // Discord allows five activity updates per 20 seconds.
            if (DateTime.UtcNow - _lastSend < TimeSpan.FromSeconds(4)) return;
            if (_stream is null)
            {
                if (json is null) { _sent = null; return; } // nothing to clear without a connection
                if (DateTime.UtcNow - _lastAttempt < TimeSpan.FromSeconds(20)) return;
                _lastAttempt = DateTime.UtcNow;
                _stream = Connect();
                if (_stream is null) return;
            }
            var payload = new JsonObject
            {
                ["cmd"] = "SET_ACTIVITY",
                ["args"] = new JsonObject
                {
                    ["pid"] = Environment.ProcessId,
                    ["activity"] = json is null ? null : JsonNode.Parse(json),
                },
                ["nonce"] = Guid.NewGuid().ToString(),
            };
            Write(_stream, 1, payload.ToJsonString());
            Read(_stream); // the reply; an error reply is not worth acting on
            _sent = json;
            _lastSend = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            // Discord quit or restarted: connect again next time.
            Log.Info($"Discord status: {ex.Message}");
            Disconnect();
        }
        finally
        {
            Interlocked.Exchange(ref _pumping, 0);
        }
    }

    private static Stream? Connect()
    {
        for (var i = 0; i < 10; i++)
        {
            Stream? stream = null;
            try
            {
                stream = OperatingSystem.IsWindows() ? ConnectPipe("discord-ipc-" + i) : ConnectSocket(i);
                if (stream is null) continue;
                Write(stream, 0, new JsonObject { ["v"] = 1, ["client_id"] = ApplicationId }.ToJsonString());
                var (op, body) = Read(stream);
                if (op == 1 && body.Contains("READY")) return stream;
                stream.Dispose();
            }
            catch
            {
                stream?.Dispose();
            }
        }
        return null;
    }

    private static Stream? ConnectPipe(string name)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            pipe.Connect(200);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            return null;
        }
    }

    private static Stream? ConnectSocket(int i)
    {
        foreach (var dir in SocketDirs())
        {
            var path = Path.Combine(dir, "discord-ipc-" + i);
            if (!File.Exists(path)) continue;
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                socket.Connect(new UnixDomainSocketEndPoint(path));
                socket.ReceiveTimeout = 3000;
                socket.SendTimeout = 3000;
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
            }
        }
        return null;
    }

    /// <summary>Where Discord puts its socket: the temp folder, or the runtime folder (plain, Flatpak, Snap).</summary>
    private static IEnumerable<string> SocketDirs()
    {
        var bases = new[] { "XDG_RUNTIME_DIR", "TMPDIR", "TMP", "TEMP" }
            .Select(Environment.GetEnvironmentVariable)
            .Where(d => !string.IsNullOrEmpty(d)).Select(d => d!)
            .Append(Path.GetTempPath()).Append("/tmp")
            .Distinct().ToList();
        foreach (var b in bases)
        {
            yield return b;
            yield return Path.Combine(b, "app", "com.discordapp.Discord");
            yield return Path.Combine(b, "snap.discord");
        }
    }

    private static void Write(Stream stream, int op, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), body.Length);
        body.CopyTo(frame, 8);
        stream.Write(frame);
        stream.Flush();
    }

    private static (int Op, string Body) Read(Stream stream)
    {
        while (true)
        {
            var header = new byte[8];
            ReadWithTimeout(stream, header);
            var op = BinaryPrimitives.ReadInt32LittleEndian(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
            if (length is < 0 or > 1_000_000) throw new IOException("Unexpected reply from Discord.");
            var body = new byte[length];
            ReadWithTimeout(stream, body);
            var text = Encoding.UTF8.GetString(body);
            if (op == 2) throw new IOException("Discord closed the connection: " + text);
            if (op == 3) { Write(stream, 4, text); continue; } // ping: answer and keep reading
            return (op, text);
        }
    }

    /// <summary>A Discord that stops answering must not hang the updates for good.</summary>
    private static void ReadWithTimeout(Stream stream, byte[] buffer)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        stream.ReadExactlyAsync(buffer, cts.Token).AsTask().GetAwaiter().GetResult();
    }

    private void Disconnect()
    {
        try { _stream?.Dispose(); } catch { /* already gone */ }
        _stream = null;
        _sent = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        // Leave no stale "Editing a clip" behind; closing the connection clears it on Discord's side.
        Disconnect();
    }
}
