using System.Runtime.InteropServices;

namespace Cnema.Services;

/// <summary>A rectangle of the Windows desktop, in physical pixels.</summary>
public readonly record struct ScreenArea(int X, int Y, int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height} at {X},{Y}";
}

/// <summary>
/// Windows screens for the GDI capture fallback. GDI grabs the whole desktop, so with several screens
/// the one picked in settings is cut out. Desktop Duplication numbers screens with the main one first,
/// so this does the same: index 0 is the main screen, then the others from left to right.
/// </summary>
public static class WindowsScreens
{
    /// <summary>The picked screen, or null with a single screen (then the whole desktop is that screen).</summary>
    public static ScreenArea? Area(int index)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var screens = new List<(ScreenArea Area, bool Main)>();
            MonitorEnumProc proc = (IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var r = info.Monitor;
                    screens.Add((new ScreenArea(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), (info.Flags & 1) != 0));
                }
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
            GC.KeepAlive(proc);
            if (screens.Count < 2) return null;
            var ordered = screens.OrderByDescending(s => s.Main).ThenBy(s => s.Area.X).ThenBy(s => s.Area.Y).ToList();
            var area = ordered[Math.Clamp(index, 0, ordered.Count - 1)].Area;
            // Encoders want even sizes.
            return area with { Width = area.Width & ~1, Height = area.Height & ~1 };
        }
        catch (Exception ex)
        {
            Log.Warn($"Listing screens failed: {ex.Message}");
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
