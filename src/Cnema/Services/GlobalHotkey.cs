using System.Runtime.InteropServices;

namespace Cnema.Services;

/// <summary>
/// One system-wide hotkey that fires while any app (a game, usually) has focus. Each OS has a
/// small native registration API for exactly this, so no keyboard hook and no extra permission
/// is needed: Carbon hot keys on macOS, RegisterHotKey on Windows, XGrabKey on X11.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    public static IReadOnlyList<string> Keys { get; } =
        Enumerable.Range('A', 26).Select(c => ((char)c).ToString())
            .Concat(Enumerable.Range(1, 12).Select(n => "F" + n)).ToList();

    private readonly Action _pressed;
    private Action? _unregister;

    public GlobalHotkey(Action pressed) => _pressed = pressed;

    /// <summary>Replaces the current registration. Must be called on the UI thread. False = the OS refused.</summary>
    public bool Register(bool ctrl, bool alt, bool shift, bool meta, string key)
    {
        Unregister();
        key = key.Trim().ToUpperInvariant();
        if (!Keys.Contains(key)) return false;
        try
        {
            if (OperatingSystem.IsMacOS()) return RegisterMac(ctrl, alt, shift, meta, key);
            if (OperatingSystem.IsWindows()) return RegisterWindows(ctrl, alt, shift, meta, key);
            return RegisterX11(ctrl, alt, shift, meta, key);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Cnema] Hotkey registration failed: {ex.Message}");
            return false;
        }
    }

    public void Unregister()
    {
        try { _unregister?.Invoke(); } catch { /* best effort */ }
        _unregister = null;
    }

    public void Dispose() => Unregister();

    private static bool IsFunctionKey(string key, out int n)
    {
        n = 0;
        return key.Length > 1 && key[0] == 'F' && int.TryParse(key.AsSpan(1), out n);
    }

    // ------------------------------------------------------------------ macOS (Carbon)
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec { public uint EventClass; public uint EventKind; }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyID { public uint Signature; public uint Id; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CarbonHandler(IntPtr callRef, IntPtr eventRef, IntPtr userData);

    [DllImport(Carbon)] private static extern IntPtr GetEventDispatcherTarget();
    [DllImport(Carbon)] private static extern int InstallEventHandler(IntPtr target, CarbonHandler handler,
        nuint numTypes, [In] EventTypeSpec[] types, IntPtr userData, out IntPtr handlerRef);
    [DllImport(Carbon)] private static extern int RemoveEventHandler(IntPtr handlerRef);
    [DllImport(Carbon)] private static extern int RegisterEventHotKey(uint keyCode, uint modifiers,
        EventHotKeyID id, IntPtr target, uint options, out IntPtr hotKeyRef);
    [DllImport(Carbon)] private static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

    // ANSI virtual key codes (Events.h), indexed A..Z, then F1..F12.
    private static readonly uint[] MacLetters =
        { 0, 11, 8, 2, 14, 3, 5, 4, 34, 38, 40, 37, 46, 45, 31, 35, 12, 15, 1, 17, 32, 9, 13, 7, 16, 6 };
    private static readonly uint[] MacFunction = { 122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111 };

    private CarbonHandler? _macHandler; // keeps the delegate alive while native code holds it

    private bool RegisterMac(bool ctrl, bool alt, bool shift, bool meta, string key)
    {
        var code = IsFunctionKey(key, out var n) ? MacFunction[n - 1] : MacLetters[key[0] - 'A'];
        uint mods = (meta ? 0x0100u : 0) | (shift ? 0x0200u : 0) | (alt ? 0x0800u : 0) | (ctrl ? 0x1000u : 0);
        // The dispatcher target, not the application target: in a Cocoa app (Avalonia runs on
        // NSApplication) hot key events are only routed through the event dispatcher.
        var target = GetEventDispatcherTarget();
        _macHandler = (_, _, _) =>
        {
            _pressed();
            return 0;
        };
        // 'keyb' / kEventHotKeyPressed
        var types = new[] { new EventTypeSpec { EventClass = 0x6B657962, EventKind = 5 } };
        if (InstallEventHandler(target, _macHandler, 1, types, IntPtr.Zero, out var handlerRef) != 0) return false;
        var id = new EventHotKeyID { Signature = 0x436E656D /* 'Cnem' */, Id = 1 };
        if (RegisterEventHotKey(code, mods, id, target, 0, out var hotKeyRef) != 0)
        {
            RemoveEventHandler(handlerRef);
            return false;
        }
        _unregister = () =>
        {
            UnregisterEventHotKey(hotKeyRef);
            RemoveEventHandler(handlerRef);
            _macHandler = null;
        };
        return true;
    }

    // ------------------------------------------------------------------ Windows
    [StructLayout(LayoutKind.Sequential)]
    private struct WinMsg
    {
        public IntPtr Hwnd; public uint Message; public UIntPtr WParam; public IntPtr LParam;
        public uint Time; public int X; public int Y; public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern int GetMessageW(out WinMsg msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessageW(uint threadId, uint msg, UIntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private bool RegisterWindows(bool ctrl, bool alt, bool shift, bool meta, string key)
    {
        uint vk = IsFunctionKey(key, out var n) ? (uint)(0x70 + n - 1) : key[0];
        uint mods = (alt ? 1u : 0) | (ctrl ? 2u : 0) | (shift ? 4u : 0) | (meta ? 8u : 0) | 0x4000 /* no repeat */;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        uint threadId = 0;
        // WM_HOTKEY is posted to the registering thread's queue, so that thread pumps messages.
        var thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            if (!RegisterHotKey(IntPtr.Zero, 1, mods, vk))
            {
                ready.SetResult(false);
                return;
            }
            ready.SetResult(true);
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
                if (msg.Message == 0x0312) _pressed();
            UnregisterHotKey(IntPtr.Zero, 1);
        }) { IsBackground = true, Name = "Cnema hotkey" };
        thread.Start();
        if (!ready.Task.GetAwaiter().GetResult()) return false;
        _unregister = () => PostThreadMessageW(threadId, 0x0012 /* WM_QUIT */, UIntPtr.Zero, IntPtr.Zero);
        return true;
    }

    // ------------------------------------------------------------------ Linux (X11)
    private const string X11 = "libX11.so.6";
    private delegate int XErrorHandler(IntPtr display, IntPtr errorEvent);

    [DllImport(X11)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(X11)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(X11)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(X11)] private static extern byte XKeysymToKeycode(IntPtr display, nuint keysym);
    [DllImport(X11)] private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers,
        IntPtr window, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(X11)] private static extern int XSync(IntPtr display, int discard);
    [DllImport(X11)] private static extern int XPending(IntPtr display);
    [DllImport(X11)] private static extern int XNextEvent(IntPtr display, IntPtr xevent);
    [DllImport(X11)] private static extern IntPtr XSetErrorHandler(XErrorHandler? handler);
    [DllImport(X11)] private static extern IntPtr XSetErrorHandler(IntPtr handler);

    private static bool _x11Refused;
    private static readonly XErrorHandler NoteX11Error = (_, _) =>
    {
        _x11Refused = true;
        return 0;
    };

    private bool RegisterX11(bool ctrl, bool alt, bool shift, bool meta, string key)
    {
        var display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) return false;
        nuint keysym = IsFunctionKey(key, out var n) ? (nuint)(0xFFBE + n - 1) : (nuint)char.ToLowerInvariant(key[0]);
        int keycode = XKeysymToKeycode(display, keysym);
        if (keycode == 0)
        {
            XCloseDisplay(display);
            return false;
        }
        uint mods = (shift ? 1u : 0) | (ctrl ? 4u : 0) | (alt ? 8u : 0) | (meta ? 0x40u : 0);
        var root = XDefaultRootWindow(display);

        // A grab that another client already owns raises BadAccess, which would otherwise kill the app.
        _x11Refused = false;
        var previous = XSetErrorHandler(NoteX11Error);
        // Also grab with CapsLock (0x2) and NumLock (0x10) held so their state does not matter.
        foreach (var locks in new uint[] { 0, 0x2, 0x10, 0x12 })
            XGrabKey(display, keycode, mods | locks, root, 0, 1, 1);
        XSync(display, 0);
        XSetErrorHandler(previous);
        if (_x11Refused)
        {
            XCloseDisplay(display);
            return false;
        }

        var stop = false;
        var thread = new Thread(() =>
        {
            var ev = Marshal.AllocHGlobal(256); // XEvent is a 192-byte union on 64-bit
            try
            {
                while (!Volatile.Read(ref stop))
                {
                    while (XPending(display) > 0)
                    {
                        XNextEvent(display, ev);
                        if (Marshal.ReadInt32(ev) == 2 /* KeyPress */) _pressed();
                    }
                    Thread.Sleep(40);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ev);
                XCloseDisplay(display);
            }
        }) { IsBackground = true, Name = "Cnema hotkey" };
        thread.Start();
        _unregister = () => Volatile.Write(ref stop, true);
        return true;
    }
}
