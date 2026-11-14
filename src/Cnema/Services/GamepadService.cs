using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Cnema.Services;

public enum PadButton { Up, Down, Left, Right, A, B, X, Y, LB, RB, LT, RT, Start, View }

/// <summary>
/// Game controller input for Controller mode. SDL2 is used purely as a cross-platform gamepad
/// reader (no SDL window, audio or event loop): it already knows the Xbox pads and several hundred
/// others on macOS, Windows and Linux, and maps them all to one Xbox-style layout.
///
/// The pad is polled from a UI-thread timer only while Controller mode is on and the window is
/// active, so it costs nothing otherwise and never steals input from a game.
/// </summary>
public sealed class GamepadService : IDisposable
{
    private const string Lib = "SDL2";
    private const uint InitGameController = 0x2000;
    private const short StickThreshold = 18000;   // of 32767: a deliberate push, not drift
    private const short TriggerThreshold = 12000;
    private static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(380);
    private static readonly TimeSpan RepeatRate = TimeSpan.FromMilliseconds(110);

    [DllImport(Lib)] private static extern int SDL_Init(uint flags);
    [DllImport(Lib)] private static extern void SDL_QuitSubSystem(uint flags);
    [DllImport(Lib)] private static extern void SDL_SetMainReady();
    [DllImport(Lib)] private static extern int SDL_SetHint([MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
    [DllImport(Lib)] private static extern void SDL_GameControllerUpdate();
    [DllImport(Lib)] private static extern int SDL_NumJoysticks();
    [DllImport(Lib)] private static extern int SDL_IsGameController(int index);
    [DllImport(Lib)] private static extern IntPtr SDL_GameControllerOpen(int index);
    [DllImport(Lib)] private static extern void SDL_GameControllerClose(IntPtr pad);
    [DllImport(Lib)] private static extern int SDL_GameControllerGetAttached(IntPtr pad);
    [DllImport(Lib)] private static extern byte SDL_GameControllerGetButton(IntPtr pad, int button);
    [DllImport(Lib)] private static extern short SDL_GameControllerGetAxis(IntPtr pad, int axis);
    [DllImport(Lib)] private static extern IntPtr SDL_GameControllerName(IntPtr pad);

    // SDL_GameControllerButton values for each PadButton that is a plain button.
    private static readonly (PadButton Button, int Sdl)[] Buttons =
    {
        (PadButton.A, 0), (PadButton.B, 1), (PadButton.X, 2), (PadButton.Y, 3), (PadButton.View, 4),
        (PadButton.Start, 6), (PadButton.LB, 9), (PadButton.RB, 10),
        (PadButton.Up, 11), (PadButton.Down, 12), (PadButton.Left, 13), (PadButton.Right, 14),
    };

    private static bool _resolverSet;
    private readonly DispatcherTimer _timer;
    private readonly bool[] _down = new bool[Enum.GetValues<PadButton>().Length];
    private readonly DateTime[] _nextRepeat = new DateTime[Enum.GetValues<PadButton>().Length];
    private IntPtr _pad;
    private bool _initialized;
    private bool _failed;
    private DateTime _nextScan;

    /// <summary>Raised on the UI thread. Directions repeat while held; other buttons fire once.</summary>
    public event Action<PadButton>? Pressed;
    /// <summary>Raised on the UI thread when a controller is connected or removed.</summary>
    public event Action? ConnectionChanged;

    public bool IsConnected => _pad != IntPtr.Zero;
    public string? ControllerName { get; private set; }
    /// <summary>Set if SDL could not be loaded; Controller mode then still works from the keyboard.</summary>
    public string? Error { get; private set; }
    /// <summary>Polling is skipped while this returns false (window hidden or in the background).</summary>
    public Func<bool>? IsActive { get; set; }

    public GamepadService()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => Poll());
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled && !_failed)
        {
            if (!_initialized && !Initialize()) return;
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            ClosePad();
        }
    }

    private bool Initialize()
    {
        try
        {
            EnsureResolver();
            SDL_SetMainReady();
            // No SDL window exists, so SDL always believes it is "in the background".
            SDL_SetHint("SDL_JOYSTICK_ALLOW_BACKGROUND_EVENTS", "1");
            SDL_SetHint("SDL_NO_SIGNAL_HANDLERS", "1");
            if (SDL_Init(InitGameController) != 0) throw new InvalidOperationException("SDL_Init failed");
            _initialized = true;
            return true;
        }
        catch (Exception ex)
        {
            _failed = true;
            Error = T("Controller support could not be loaded: ") + ex.Message;
            Log.Warn($"" + Error);
            return false;
        }
    }

    private void Poll()
    {
        if (IsActive?.Invoke() == false)
        {
            Array.Clear(_down); // do not fire a held button when the window comes back
            return;
        }
        try
        {
            SDL_GameControllerUpdate();
            if (_pad != IntPtr.Zero && SDL_GameControllerGetAttached(_pad) == 0) ClosePad();
            if (_pad == IntPtr.Zero)
            {
                if (DateTime.UtcNow < _nextScan) return;
                _nextScan = DateTime.UtcNow.AddSeconds(1.5);
                OpenFirstPad();
                if (_pad == IntPtr.Zero) return;
            }

            // The left stick doubles as the D-pad: work out each direction once, from both.
            var x = SDL_GameControllerGetAxis(_pad, 0);
            var y = SDL_GameControllerGetAxis(_pad, 1);
            var horizontal = Math.Abs(x) > Math.Abs(y);
            foreach (var (button, sdl) in Buttons)
            {
                var held = SDL_GameControllerGetButton(_pad, sdl) != 0;
                held |= button switch
                {
                    PadButton.Left => horizontal && x < -StickThreshold,
                    PadButton.Right => horizontal && x > StickThreshold,
                    PadButton.Up => !horizontal && y < -StickThreshold,
                    PadButton.Down => !horizontal && y > StickThreshold,
                    _ => false,
                };
                Update(button, held);
            }
            Update(PadButton.LT, SDL_GameControllerGetAxis(_pad, 4) > TriggerThreshold);
            Update(PadButton.RT, SDL_GameControllerGetAxis(_pad, 5) > TriggerThreshold);
        }
        catch (Exception ex)
        {
            _failed = true;
            _timer.Stop();
            Log.Warn($"Controller polling stopped: {ex.Message}");
        }
    }

    private static bool Repeats(PadButton b) => b is PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right;

    /// <summary>Edge detection, plus key-repeat for the directions.</summary>
    private void Update(PadButton button, bool held)
    {
        var i = (int)button;
        if (!held)
        {
            _down[i] = false;
            return;
        }
        var now = DateTime.UtcNow;
        if (!_down[i])
        {
            _down[i] = true;
            _nextRepeat[i] = now + RepeatDelay;
            Pressed?.Invoke(button);
        }
        else if (Repeats(button) && now >= _nextRepeat[i])
        {
            _nextRepeat[i] = now + RepeatRate;
            Pressed?.Invoke(button);
        }
    }

    private void OpenFirstPad()
    {
        var count = SDL_NumJoysticks();
        for (var i = 0; i < count; i++)
        {
            if (SDL_IsGameController(i) == 0) continue;
            _pad = SDL_GameControllerOpen(i);
            if (_pad == IntPtr.Zero) continue;
            ControllerName = Marshal.PtrToStringUTF8(SDL_GameControllerName(_pad)) ?? "Controller";
            Array.Clear(_down);
            ConnectionChanged?.Invoke();
            return;
        }
    }

    private void ClosePad()
    {
        if (_pad == IntPtr.Zero) return;
        try { SDL_GameControllerClose(_pad); } catch { /* device already gone */ }
        _pad = IntPtr.Zero;
        ControllerName = null;
        Array.Clear(_down);
        ConnectionChanged?.Invoke();
    }

    /// <summary>
    /// The SDL2 binary ships as libSDL2-2.0.so / libSDL2-2.0.dylib / SDL2.dll, which the default
    /// "SDL2" lookup only finds on Windows.
    /// </summary>
    private static void EnsureResolver()
    {
        if (_resolverSet) return;
        _resolverSet = true;
        NativeLibrary.SetDllImportResolver(typeof(GamepadService).Assembly, (name, assembly, path) =>
        {
            if (name != Lib) return IntPtr.Zero;
            var names = OperatingSystem.IsWindows() ? new[] { "SDL2.dll" }
                : OperatingSystem.IsMacOS() ? new[] { "libSDL2-2.0.dylib", "libSDL2.dylib" }
                : new[] { "libSDL2-2.0.so", "libSDL2-2.0.so.0", "libSDL2.so" };
            foreach (var n in names)
            {
                if (NativeLibrary.TryLoad(n, assembly, path, out var handle)) return handle;
                if (NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, n), out handle)) return handle;
            }
            return IntPtr.Zero;
        });
    }

    /// <summary>For --selftest: can the bundled SDL be loaded and started?</summary>
    public static string SelfTest()
    {
        using var pad = new GamepadService();
        return pad.Initialize() ? "OK" : "MISSING - " + pad.Error;
    }

    public void Dispose()
    {
        _timer.Stop();
        ClosePad();
        if (_initialized)
        {
            try { SDL_QuitSubSystem(InitGameController); } catch { /* shutting down anyway */ }
            _initialized = false;
        }
    }
}
