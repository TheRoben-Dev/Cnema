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
    private NativeMenuItem? _trayToggle;
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
            VlcBootstrap.Initialize();
            AutostartService.RefreshIfEnabled();

            var settings = new SettingsStore();
            _vm = new MainViewModel(settings, new LibraryService(), new ClipService(settings));
            _window = new MainWindow { DataContext = _vm };

            // The window is one view onto a process that may live on in the tray, so the app
            // decides when to exit, not the last window closing.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += (_, _) =>
            {
                IsQuitting = true;
                _instance?.Dispose();
                _vm.Dispose();
            };
            _window.Closed += (_, _) => { if (!IsQuitting) Quit(); };
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

            _ = _vm.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }

    private readonly List<PosixSignalRegistration> _signals = new();

    private void OnSignal(PosixSignalContext context)
    {
        context.Cancel = true; // we exit ourselves, after cleaning up
        Dispatcher.UIThread.Post(Quit);
    }

    private void BuildTray()
    {
        var open = new NativeMenuItem("Open Cnema");
        open.Click += (_, _) => ShowWindow();
        _traySave = new NativeMenuItem("Save clip");
        _traySave.Click += (_, _) => _vm?.SaveClipCommand.Execute(null);
        _trayToggle = new NativeMenuItem("Background clipping") { ToggleType = NativeMenuItemToggleType.CheckBox };
        _trayToggle.Click += (_, _) => _vm?.ToggleClippingCommand.Execute(null);
        var quit = new NativeMenuItem("Quit Cnema");
        quit.Click += (_, _) => Quit();

        var menu = new NativeMenu();
        menu.Add(open);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(_traySave);
        menu.Add(_trayToggle);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quit);

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Cnema/Assets/cnema.png"))),
            ToolTipText = "Cnema",
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
        _tray.ToolTipText = "Cnema · " + _vm.ClipTitle;
        if (_traySave is not null)
        {
            _traySave.Header = _vm.SaveClipLabel;
            _traySave.IsEnabled = _vm.IsClipRecording;
        }
        if (_trayToggle is not null) _trayToggle.IsChecked = _vm.IsClipEnabled;
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
