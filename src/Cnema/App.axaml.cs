using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Cnema.Services;
using Cnema.ViewModels;
using Cnema.Views;

namespace Cnema;

public partial class App : Application
{
    private MainViewModel? _vm;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private NativeMenuItem? _traySave;
    private NativeMenuItem? _traySession;
    private NativeMenuItem? _trayUpdate;
    private NativeMenuItem? _trayToggle;
    private NativeMenuItem? _trayOpen;
    private NativeMenuItem? _trayQuit;
    private SingleInstance? _instance;

    /// <summary>True once the user chose Quit: closing the window then really closes it.</summary>
    public bool IsQuitting { get; private set; }

    /// <summary>Should closing the window hide it and leave Cnema running in the tray?</summary>
    public bool StaysInBackground => _vm?.StaysInBackground == true;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var startHidden = desktop.Args?.Contains(AutostartService.BackgroundFlag) == true;
            // A bug in one click handler should be logged, not take the whole app (and the capture) down.
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Log.Error("Unhandled UI exception", e.Exception);
                e.Handled = true;
            };
            // Language first, so even start-up errors (libVLC missing) are translated.
            var settings = new SettingsStore();
            ModelStore.SpeechChoice = settings.Current.SpeechModel;
            Loc.Apply(settings.Current.Language);
            ThemeService.Mode = settings.Current.ThemeMode;
            ThemeService.Apply(settings.Current.ColorScheme, settings.Current.Accessibility.HighContrast);
            // The window is one view onto a process that may live on in the tray, so the app
            // decides when to exit, not the last window closing.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // The loading screen goes up first and stays while the rest gets ready (not when starting
            // hidden at login: then nothing shows until the tray is clicked).
            SplashWindow? splash = null;
            if (!startHidden && settings.Current.Features.Splash && Environment.GetEnvironmentVariable("CNEMA_NO_SPLASH") != "1")
            {
                try
                {
                    splash = new SplashWindow(settings.Current.Accessibility.ReducedMotion);
                    splash.Report(T("Getting ready…"), 0.05);
                    splash.Show();
                }
                catch (Exception ex)
                {
                    Log.Warn($"Loading screen: {ex.Message}");
                    splash = null;
                }
            }
            if (splash is null) _ = Start(desktop, settings, startHidden);
            else Dispatcher.UIThread.Post(() => _ = StartWithSplashAsync(desktop, settings, splash), DispatcherPriority.Background);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Everything after settings and theme: the video player, the library, the window, the tray.</summary>
    private Task Start(IClassicDesktopStyleApplicationLifetime desktop, SettingsStore settings, bool startHidden)
    {
        VlcBootstrap.Initialize();
        VlcBootstrap.RefreshCacheInBackground();
        AutostartService.RefreshIfEnabled();

        Shortcuts.Load(settings.Current.Shortcuts);
        _vm = new MainViewModel(settings, new LibraryService(), new ClipService(settings));
        _window = new MainWindow { DataContext = _vm };

        desktop.ShutdownRequested += (_, _) =>
        {
            IsQuitting = true;
            _instance?.Dispose();
            _vm.Dispose();
        };
        _window.Closed += (_, _) => { if (!IsQuitting) Quit(); };
        // The lifetime shows it at start; after the loading screen, that shows it when it's done.
        if (!startHidden) desktop.MainWindow = _window;

        BuildTray();
        _vm.BackgroundStateChanged += UpdateTray;
        UpdateTray();

        // Logging out, `kill`, Ctrl+C: shut down properly so the capture process never outlives us.
        if (!OperatingSystem.IsWindows())
        {
            _signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal));
            _signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal));
            _signals.Add(PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnSignal));
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => _vm?.Clips.Dispose();

        _instance = new SingleInstance();
        _instance.ShowRequested += () => Dispatcher.UIThread.Post(ShowWindow);
        _instance.Listen();

        // macOS: clicking the Dock icon of the running app brings the window back.
        if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
            activatable.Activated += (_, e) =>
            {
                if (e.Kind == ActivationKind.Reopen) ShowWindow();
            };

        _vm.QuitRequested += () => Dispatcher.UIThread.Post(Quit);
        return _vm.InitializeAsync();
    }

    /// <summary>
    /// The loading screen's steps, each said as it happens: the video player, the library, then the models
    /// that are switched on. It shows for a moment at least (so it doesn't flash), at most 8 seconds, and a click
    /// skips it; a model still loading then carries on in the background.
    /// </summary>
    private async Task StartWithSplashAsync(IClassicDesktopStyleApplicationLifetime desktop, SettingsStore settings, SplashWindow splash)
    {
        var shown = System.Diagnostics.Stopwatch.StartNew();
        var minimum = TimeSpan.FromMilliseconds(settings.Current.Accessibility.ReducedMotion ? 500 : 1600);
        var deadline = Task.Delay(TimeSpan.FromSeconds(8));
        var skip = splash.Skipped;
        try
        {
            splash.Report(T("Starting the video player…"), 0.15);
            await Task.Run(VlcBootstrap.Initialize);
            Log.Info($"Start-up: video player ready after {shown.ElapsedMilliseconds} ms");
            splash.Report(T("Opening your library…"), 0.35);
            // Building the window holds the screen for a moment, so the intro gets to play out first.
            var intro = TimeSpan.FromMilliseconds(settings.Current.Accessibility.ReducedMotion ? 50 : 900) - shown.Elapsed;
            if (intro > TimeSpan.Zero) await Task.WhenAny(Task.Delay(intro), skip);
            await Task.Yield();
            var init = Start(desktop, settings, false);
            Log.Info($"Start-up: library open after {shown.ElapsedMilliseconds} ms");

            var steps = _vm?.WarmUpSteps() ?? Array.Empty<(string, Func<Task>)>();
            for (var i = 0; i < steps.Count && !skip.IsCompleted && !deadline.IsCompleted; i++)
            {
                var (status, work) = steps[i];
                splash.Report(status, 0.5 + 0.45 * i / steps.Count);
                Task task;
                try { task = work(); }
                catch (Exception ex) { Log.Warn($"Warm-up: {ex.Message}"); continue; }
                await Task.WhenAny(task, skip, deadline);
                Log.Info($"Start-up: {status} done after {shown.ElapsedMilliseconds} ms");
            }
            splash.Report(T("Ready"), 1);
            var left = minimum - shown.Elapsed;
            if (left > TimeSpan.Zero && !skip.IsCompleted) await Task.WhenAny(Task.Delay(left), skip);
            _ = init.ContinueWith(t => Log.Warn($"Library start: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex)
        {
            Log.Error("Start-up", ex);
        }
        finally
        {
            if (_vm is null) _ = Start(desktop, settings, false);
            _window?.Show();
            _window?.Activate();
            await splash.FadeOutAsync();
            if (_window is null)
            {
                // The window couldn't be built: say so and quit, rather than live on with nothing on screen
                // (which looked like the app vanishing, and had to be force quit before it would start again).
                Log.Error("Start-up: the main window could not be opened; quitting");
                Console.Error.WriteLine("Start-up failed: no main window (see the log)");
                IsQuitting = true;
                desktop.Shutdown(1);
            }
            else if (desktop.Args?.Contains(StartSelfTestFlag) == true) _ = FinishStartSelfTestAsync(desktop);
        }
    }

    /// <summary>CI: start like a user would (loading screen, library, window), then quit with 0 if the window is up.</summary>
    public const string StartSelfTestFlag = "--selftest-start";

    private async Task FinishStartSelfTestAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Delay(TimeSpan.FromSeconds(5)); // a few layouts and frames of the real window
        var ok = _window is { IsVisible: true } w && w.Bounds.Width > 0;
        Console.WriteLine(ok ? $"start selftest OK ({_vm?.Videos.Count ?? 0} videos)" : "start selftest FAILED: no window");
        IsQuitting = true; // so the window closing doesn't ask to quit a second time
        desktop.Shutdown(ok ? 0 : 1);
    }

    private readonly List<PosixSignalRegistration> _signals = new();

    private void OnSignal(PosixSignalContext context)
    {
        context.Cancel = true; // we exit ourselves, after cleaning up
        Dispatcher.UIThread.Post(Quit);
    }

    private void BuildTray()
    {
        var open = _trayOpen = new NativeMenuItem(T("Open Savefile"));
        open.Click += (_, _) => ShowWindow();
        _traySave = new NativeMenuItem(T("Save clip"));
        _traySave.Click += (_, _) => _vm?.SaveClipCommand.Execute(null);
        _traySession = new NativeMenuItem(T("Record session"));
        _traySession.Click += (_, _) => _vm?.ToggleSessionCommand.Execute(null);
        _trayToggle = new NativeMenuItem(T("Background clipping")) { ToggleType = NativeMenuItemToggleType.CheckBox };
        _trayToggle.Click += (_, _) => _vm?.ToggleClippingCommand.Execute(null);
        var quit = _trayQuit = new NativeMenuItem(T("Quit Savefile"));
        quit.Click += (_, _) => Quit();

        _trayUpdate = new NativeMenuItem(T("Get the update")) { IsVisible = false };
        _trayUpdate.Click += (_, _) => _vm?.OpenUpdateCommand.Execute(null);
        var menu = new NativeMenu();
        menu.Add(open);
        menu.Add(_trayUpdate);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(_traySave);
        menu.Add(_traySession);
        menu.Add(_trayToggle);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Cnema/Assets/cnema.png"))),
            ToolTipText = "Savefile",
            Menu = menu,
            IsVisible = false,
        };
        _tray.Clicked += (_, _) => ShowWindow();
        TrayIcon.SetIcons(this, new TrayIcons { _tray });
    }

    private void UpdateTray()
    {
        if (_vm is null || _tray is null) return;
        _tray.IsVisible = _vm.StaysInBackground;
        _tray.ToolTipText = "Savefile · " + _vm.ClipTitle;
        // Fixed labels too, so the menu follows a language change.
        if (_trayOpen is not null) _trayOpen.Header = T("Open Savefile");
        if (_trayQuit is not null) _trayQuit.Header = T("Quit Savefile");
        if (_trayToggle is not null) _trayToggle.Header = T("Background clipping");
        if (_traySave is not null)
        {
            _traySave.Header = _vm.SaveClipLabel;
            _traySave.IsEnabled = _vm.IsClipRecording;
        }
        if (_traySession is not null)
        {
            _traySession.Header = _vm.SessionLabel;
            _traySession.IsEnabled = _vm.CanToggleSession;
            _traySession.IsVisible = _vm.ShowSessionButton || _vm.IsSession;
        }
        if (_trayUpdate is not null)
        {
            _trayUpdate.IsVisible = _vm.HasUpdate;
            if (_vm.HasUpdate) _trayUpdate.Header = _vm.UpdateText + T(" · get it");
        }
        if (_trayToggle is not null)
        {
            _trayToggle.IsChecked = _vm.IsClipEnabled;
            _trayToggle.IsVisible = _vm.ShowClipping;
        }
        if (_traySave is not null) _traySave.IsVisible = _vm.ShowClipping;
    }

    public void ShowWindow()
    {
        if (_window is null) return;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: null } desktop)
            desktop.MainWindow = _window;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void Quit()
    {
        IsQuitting = true;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.TryShutdown();
    }
}
