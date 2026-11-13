using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LibVLCSharp.Shared;

namespace Cinema.Controls;

/// <summary>
/// Receives decoded frames from libVLC through the video callbacks (RV32 / BGRA) instead of giving
/// VLC a native window handle. That makes video a normal Avalonia visual: identical on macOS, Windows
/// and Linux, and UI can be layered over it (no "airspace" issues with NativeControlHost).
/// Cost: one CPU copy per frame, which is fine up to 4K on any modern machine.
/// </summary>
public sealed class VideoFrameBuffer : IDisposable
{
    private const int MaxWidth = 3840;

    private readonly object _sync = new();
    private IntPtr _vlcBuffer;
    private byte[]? _latest;
    private int _width, _height;
    private long _frameId, _presentedId = -1;

    // Delegates must stay referenced for as long as native code may call them.
    private readonly MediaPlayer.LibVLCVideoFormatCb _format;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanup;
    private readonly MediaPlayer.LibVLCVideoLockCb _lock;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _display;

    /// <summary>Raised on a libVLC thread whenever a new frame is available.</summary>
    public event Action? FrameReady;

    public VideoFrameBuffer(MediaPlayer player)
    {
        _format = OnFormat;
        _cleanup = OnCleanup;
        _lock = OnLock;
        _display = OnDisplay;
        player.SetVideoFormatCallbacks(_format, _cleanup);
        player.SetVideoCallbacks(_lock, null, _display);
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        Marshal.Copy("RV32"u8.ToArray(), 0, chroma, 4);
        if (width > MaxWidth)
        {
            height = (uint)Math.Round(height * (double)MaxWidth / width);
            width = MaxWidth;
        }
        lock (_sync)
        {
            FreeNative();
            _width = (int)width;
            _height = (int)height;
            var size = _width * 4 * _height;
            _vlcBuffer = Marshal.AllocHGlobal(size);
            _latest = new byte[size];
            _presentedId = -1;
        }
        pitches = width * 4;
        lines = height;
        return 1;
    }

    private void OnCleanup(ref IntPtr opaque) { /* buffers are reused/freed on next format or Dispose */ }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _vlcBuffer);
        return IntPtr.Zero;
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        lock (_sync)
        {
            if (_latest is null || _vlcBuffer == IntPtr.Zero) return;
            Marshal.Copy(_vlcBuffer, _latest, 0, _latest.Length);
            _frameId++;
        }
        FrameReady?.Invoke();
    }

    /// <summary>UI thread: copy the newest frame into <paramref name="bitmap"/>, (re)creating it if needed.</summary>
    public bool Present(ref WriteableBitmap? bitmap)
    {
        lock (_sync)
        {
            if (_latest is null || _frameId == _presentedId) return false;
            if (bitmap is null || bitmap.PixelSize.Width != _width || bitmap.PixelSize.Height != _height)
            {
                bitmap?.Dispose();
                bitmap = new WriteableBitmap(new PixelSize(_width, _height), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }
            using var fb = bitmap.Lock();
            var srcStride = _width * 4;
            if (fb.RowBytes == srcStride)
            {
                Marshal.Copy(_latest, 0, fb.Address, _latest.Length);
            }
            else
            {
                for (var y = 0; y < _height; y++)
                    Marshal.Copy(_latest, y * srcStride, fb.Address + y * fb.RowBytes, srcStride);
            }
            _presentedId = _frameId;
            return true;
        }
    }

    /// <summary>Raised when the current picture should be discarded (new media loaded).</summary>
    public event Action? Cleared;

    public void Reset()
    {
        lock (_sync)
        {
            _latest = null;
            _presentedId = _frameId;
        }
        Cleared?.Invoke();
    }

    private void FreeNative()
    {
        if (_vlcBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_vlcBuffer);
        _vlcBuffer = IntPtr.Zero;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            FreeNative();
            _latest = null;
        }
    }
}
