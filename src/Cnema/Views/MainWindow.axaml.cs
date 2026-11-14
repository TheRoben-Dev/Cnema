using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Cnema.Services;
using Cnema.ViewModels;

namespace Cnema.Views;

public partial class MainWindow : Window
{
    private WindowState _preFullscreenState = WindowState.Normal;

    public MainWindow()
    {
        InitializeComponent();
        ApplyTitleBarInsets();

        VideoGrid.ElementPrepared += (_, e) => (e.Element.DataContext as VideoItemViewModel)?.AcquireThumbnail();
        VideoGrid.ElementClearing += (_, e) => (e.Element.DataContext as VideoItemViewModel)?.ReleaseThumbnail();
        VideoGrid.ElementIndexChanged += (_, e) => (e.Element.DataContext as VideoItemViewModel)?.AcquireThumbnail();

        SurfaceHit.Tapped += (_, _) => Vm?.Player.TogglePlayCommand.Execute(null);
        SurfaceHit.DoubleTapped += (_, _) => ToggleFullscreen();

        // Tunnel so shortcuts work even when a button/slider has focus; skipped while typing.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // Our content covers the title bar, so the OS no longer drags the window for us.
        AddHandler(PointerPressedEvent, OnTitleBarPointerPressed, RoutingStrategies.Bubble, handledEventsToo: false);

        SettingsScrim.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, SettingsScrim)) Vm?.CancelSettingsCommand.Execute(null);
        };
        EditScrim.PointerPressed += (_, e) =>
        {
            if (ReferenceEquals(e.Source, EditScrim)) Vm?.CancelEditCommand.Execute(null);
        };
        EditTagInput.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            // Enter on an empty tag box saves; otherwise it commits the tag.
            if (string.IsNullOrWhiteSpace(Vm?.Editor?.TagInput)) Vm?.SaveEditCommand.Execute(null);
            else Vm?.Editor?.AddTagCommand.Execute(null);
            e.Handled = true;
        };
        EditName.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Vm?.SaveEditCommand.Execute(null); e.Handled = true; }
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // ---------- controller mode ----------
    private const double ControllerScale = 1.25;
    private static readonly bool PadDebugKeys = Environment.GetEnvironmentVariable("CNEMA_PAD_DEBUG") == "1";
    private readonly GamepadService _pad = new();
    private bool _controllerFullscreen;

    /// <summary>
    /// Switches the whole window between the desktop layout and the controller layout: everything
    /// is scaled up, the D-pad / stick / arrow keys move a visible cursor, and the pad is polled.
    /// </summary>
    private void ApplyControllerMode()
    {
        if (Vm is not { } vm) return;
        var on = vm.IsControllerMode;
        Classes.Set("pad", on);
        Scaler.LayoutTransform = on ? new ScaleTransform(ControllerScale, ControllerScale) : null;
        _pad.SetEnabled(on);
        UpdatePadStatus();

        if (on && vm.ControllerFullscreen && WindowState != WindowState.FullScreen)
        {
            _preFullscreenState = WindowState;
            WindowState = WindowState.FullScreen;
            _controllerFullscreen = true;
        }
        else if (_controllerFullscreen && !(on && vm.ControllerFullscreen))
        {
            if (WindowState == WindowState.FullScreen) WindowState = WindowState.Normal;
            _controllerFullscreen = false;
        }
        if (on) Avalonia.Threading.Dispatcher.UIThread.Post(() => FocusGrid(), Avalonia.Threading.DispatcherPriority.Background);
    }

    private void UpdatePadStatus()
    {
        if (Vm is not { } vm) return;
        vm.PadStatus = _pad.Error ?? (_pad.IsConnected
            ? "Connected: " + _pad.ControllerName
            : "No controller found yet. Arrow keys, Enter and Esc work too.");
    }

    private Control? Focused => FocusManager?.GetFocusedElement() as Control;

    // Navigation is explicit rather than "nearest control in that direction": the screen is a
    // sidebar column, a grid of cards and the occasional dialog, and each has an obvious order.

    private int GridColumns => Math.Max(1, (int)((VideoGrid.Bounds.Width + 6) / (250 + 6)));

    private bool FocusCard(int index)
    {
        if (Vm is not { IsPlayerOpen: false, IsEditing: false, IsSettingsOpen: false } vm || vm.Videos.Count == 0) return false;
        index = Math.Clamp(index, 0, vm.Videos.Count - 1);
        // The grid is virtualized: ask for the card, which creates it if it is scrolled out of view.
        var card = VideoGrid.GetOrCreateElement(index);
        card.BringIntoView();
        return card.Focus(NavigationMethod.Directional);
    }

    private bool FocusGrid() => FocusCard(0);

    /// <summary>The sidebar as one column: every folder and tag, then the controls underneath.</summary>
    private List<Control> SidebarStops()
    {
        var stops = new List<Control>();
        foreach (var list in new[] { FolderList, TagList })
            for (var i = 0; list.IsVisible && i < list.ItemCount; i++)
                if (list.ContainerFromIndex(i) is { } item) stops.Add(item);
        stops.AddRange(new Control[] { ClipToggle, SaveClipButton, AddFolderButton, SettingsButton }
            .Where(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled));
        return stops;
    }

    private bool FocusSidebar()
    {
        var list = Vm?.SelectedTag is not null ? TagList : FolderList;
        var item = list.ContainerFromIndex(Math.Max(0, list.SelectedIndex)) as Control;
        return item?.Focus(NavigationMethod.Directional) == true;
    }

    private static bool IsDropDownOpen(Control? focused) =>
        focused is ComboBoxItem || focused is ComboBox { IsDropDownOpen: true };

    private void MoveInLibrary(PadButton b)
    {
        if (Vm is not { } vm) return;
        var focused = Focused;
        if (IsDropDownOpen(focused)) { SendKey(ArrowFor(b)); return; }

        // ----- on a card -----
        if (focused is Button { DataContext: VideoItemViewModel item })
        {
            var index = vm.Videos.IndexOf(item);
            var columns = GridColumns;
            switch (b)
            {
                case PadButton.Left when index % columns == 0: FocusSidebar(); break;
                case PadButton.Left: FocusCard(index - 1); break;
                case PadButton.Right when index < vm.Videos.Count - 1: FocusCard(index + 1); break;
                case PadButton.Up when index < columns: SortBox.Focus(NavigationMethod.Directional); break;
                case PadButton.Up: FocusCard(index - columns); break;
                case PadButton.Down when index + columns < vm.Videos.Count: FocusCard(index + columns); break;
                case PadButton.Down when index / columns < (vm.Videos.Count - 1) / columns: FocusCard(vm.Videos.Count - 1); break;
            }
            return;
        }

        // ----- sort box above the grid -----
        if (ReferenceEquals(focused, SortBox))
        {
            if (b == PadButton.Down) FocusGrid();
            else if (b == PadButton.Left) FocusSidebar();
            return;
        }

        // ----- sidebar -----
        var stops = SidebarStops();
        var at = focused is null ? -1 : stops.FindIndex(s => ReferenceEquals(s, focused) || s.IsVisualAncestorOf(focused));
        if (at < 0) { if (!FocusGrid()) FocusSidebar(); return; }
        switch (b)
        {
            case PadButton.Right: if (!FocusGrid()) SortBox.Focus(NavigationMethod.Directional); break;
            case PadButton.Up when at > 0: stops[at - 1].Focus(NavigationMethod.Directional); break;
            case PadButton.Down when at < stops.Count - 1: stops[at + 1].Focus(NavigationMethod.Directional); break;
        }
    }

    /// <summary>Dialogs are a column of controls: up/down walk it in tab order.</summary>
    private void MoveInDialog(PadButton b)
    {
        var focused = Focused;
        if (IsDropDownOpen(focused)) { SendKey(ArrowFor(b)); return; }
        if (b is PadButton.Left or PadButton.Right && focused is TextBox or Slider) { SendKey(ArrowFor(b)); return; }
        var forward = b is PadButton.Down or PadButton.Right;
        var scope = (Control)(Vm?.IsSettingsOpen == true ? SettingsScrim : EditScrim);
        if (focused is null || !scope.IsVisualAncestorOf(focused))
        {
            scope.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled && c is not ScrollViewer)
                ?.Focus(NavigationMethod.Directional);
            return;
        }
        var next = KeyboardNavigationHandler.GetNext(focused,
            forward ? NavigationDirection.Next : NavigationDirection.Previous) as Control;
        // Tab order wraps around the whole window; stay inside the dialog.
        if (next is not null && scope.IsVisualAncestorOf(next)) next.Focus(NavigationMethod.Directional);
    }

    /// <summary>Sends a key through the normal input route, as if a pad were a tiny keyboard.</summary>
    private void SendKey(Key key)
    {
        Interactive target = Focused is { } f && f.IsEffectivelyVisible ? f : this;
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = key, KeyDeviceType = KeyDeviceType.Gamepad, Source = target });
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = KeyUpEvent, Key = key, KeyDeviceType = KeyDeviceType.Gamepad, Source = target });
    }

    private static Key ArrowFor(PadButton b) => b switch
    {
        PadButton.Up => Key.Up, PadButton.Down => Key.Down, PadButton.Left => Key.Left, _ => Key.Right,
    };

    private void OnPad(PadButton b)
    {
        if (Vm is not { } vm || !vm.IsControllerMode) return;
        var direction = b is PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right;

        // ----- player: the pad drives playback directly, focus plays no part -----
        if (vm.IsPlayerOpen && !vm.IsSettingsOpen)
        {
            var p = vm.Player;
            switch (b)
            {
                case PadButton.A: p.TogglePlayCommand.Execute(null); break;
                case PadButton.B: vm.ClosePlayerCommand.Execute(null); PlayerTopBar.IsVisible = true; break;
                case PadButton.Left: p.SeekCommand.Execute(p.Position - 5000); break;
                case PadButton.Right: p.SeekCommand.Execute(p.Position + 5000); break;
                case PadButton.Up: p.Volume = Math.Min(125, p.Volume + 5); break;
                case PadButton.Down: p.Volume = Math.Max(0, p.Volume - 5); break;
                case PadButton.X: p.SetInCommand.Execute(null); break;
                case PadButton.Y: p.SetOutCommand.Execute(null); break;
                case PadButton.LB: p.StepFrameBackCommand.Execute(null); break;
                case PadButton.RB: p.StepFrameCommand.Execute(null); break;
                case PadButton.LT: p.SeekCommand.Execute(p.Position - 10000); break;
                case PadButton.RT: p.SeekCommand.Execute(p.Position + 10000); break;
                case PadButton.Start: p.ExportCommand.Execute(null); break;
                case PadButton.View: vm.SaveClipCommand.Execute(null); break;
            }
            return;
        }

        // ----- dialogs -----
        if (vm.IsSettingsOpen || vm.IsEditing)
        {
            if (b == PadButton.B)
            {
                // B first backs out of an open drop-down, then out of the dialog.
                if (IsDropDownOpen(Focused)) SendKey(Key.Escape);
                else if (vm.IsSettingsOpen) vm.CancelSettingsCommand.Execute(null);
                else vm.CancelEditCommand.Execute(null);
            }
            else if (b == PadButton.Start)
            {
                if (vm.IsSettingsOpen) vm.SaveSettingsCommand.Execute(null);
                else vm.SaveEditCommand.Execute(null);
            }
            else if (b == PadButton.A) SendKey(Focused is ToggleSwitch or CheckBox or RadioButton or ToggleButton ? Key.Space : Key.Enter);
            else if (direction) MoveInDialog(b);
            return;
        }

        // ----- library -----
        var card = Focused as Button is { DataContext: VideoItemViewModel } c ? c : null;
        switch (b)
        {
            case PadButton.A:
                if (Focused is null && FocusGrid()) break;
                SendKey(Focused is ToggleSwitch ? Key.Space : Key.Enter);
                break;
            case PadButton.B:
                if (IsDropDownOpen(Focused)) SendKey(Key.Escape);
                else if (card is not null || Focused is null) FocusSidebar();
                else if (!FocusGrid()) FocusSidebar();
                break;
            case PadButton.X when card is not null: vm.ToggleFavoriteCommand.Execute(card.DataContext); break;
            case PadButton.Y when card is not null: vm.BeginEditCommand.Execute(card.DataContext); break;
            case PadButton.LB: vm.CycleNav(-1); RefocusGridSoon(); break;
            case PadButton.RB: vm.CycleNav(+1); RefocusGridSoon(); break;
            case PadButton.Start: vm.OpenSettingsCommand.Execute(null); break;
            case PadButton.View: vm.SaveClipCommand.Execute(null); break;
            default:
                if (direction) MoveInLibrary(b);
                break;
        }
    }

    /// <summary>After the visible videos changed, put the cursor back on the first card.</summary>
    private void RefocusGridSoon() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => FocusGrid(), Avalonia.Threading.DispatcherPriority.Background);

    private void OnQuitClick(object? sender, RoutedEventArgs e) => (Application.Current as App)?.Quit();

    /// <summary>
    /// With clipping on (or "keep running" set) the close button only hides the window: playback
    /// stops, the capture and its hotkey carry on, and the tray icon brings the window back.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Application.Current is App { IsQuitting: false, StaysInBackground: true } &&
            e.CloseReason is WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            if (WindowState == WindowState.FullScreen) WindowState = _preFullscreenState;
            Vm?.ClosePlayerCommand.Execute(null);
            Vm?.CancelSettingsCommand.Execute(null);
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Vm is not { } vm) return;
        vm.ControllerModeChanged += ApplyControllerMode;
        _pad.IsActive = () => IsActive && IsVisible;
        _pad.Pressed += OnPad;
        _pad.ConnectionChanged += UpdatePadStatus;
        ApplyControllerMode();
        vm.PickFolder = PickFolderAsync;
        vm.Player.PickSavePath = PickSavePathAsync;
        vm.PropertyChanged += (_, e) =>
        {
            if (vm.IsControllerMode)
            {
                // Keep the cursor somewhere sensible as views open and close.
                if (e.PropertyName == nameof(MainViewModel.IsPlayerOpen) && !vm.IsPlayerOpen) RefocusGridSoon();
                if (e.PropertyName is nameof(MainViewModel.Editor) or nameof(MainViewModel.SettingsEditor) &&
                    vm is { IsEditing: false, IsSettingsOpen: false }) RefocusGridSoon();
                if (e.PropertyName == nameof(MainViewModel.SettingsEditor) && vm.IsSettingsOpen)
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => SettingsFirst.Focus(NavigationMethod.Directional),
                        Avalonia.Threading.DispatcherPriority.Background);
            }
            if (e.PropertyName != nameof(MainViewModel.Editor) || vm.Editor is null) return;
            // Put the cursor in the name field, ready to type over it.
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { EditName.Focus(); EditName.SelectAll(); },
                Avalonia.Threading.DispatcherPriority.Background);
        };
    }

    /// <summary>
    /// The client area extends under the title bar: leave room for the macOS traffic lights (left)
    /// or the Windows caption buttons (right). Linux keeps its normal decorations.
    /// </summary>
    private void ApplyTitleBarInsets()
    {
        if (OperatingSystem.IsMacOS())
        {
            SidebarInset.Height = 30;
            PlayerTopGrid.Margin = new Thickness(72, 0, 0, 0);
        }
        else if (OperatingSystem.IsWindows())
        {
            Toolbar.Margin = new Thickness(28, 0, 150, 0);
            PlayerTopGrid.Margin = new Thickness(0, 0, 150, 0);
        }
        else
        {
            ExtendClientAreaToDecorationsHint = false;
        }
    }

    private const double TitleBarHeight = 52;

    /// <summary>
    /// Drag the window from the top strip (anywhere that isn't a control), and double-click it to zoom,
    /// matching native title bar behaviour on macOS and Windows.
    /// </summary>
    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ExtendClientAreaToDecorationsHint || WindowState == WindowState.FullScreen) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.GetPosition(this).Y > TitleBarHeight) return;
        if (Vm?.IsEditing == true || Vm?.IsSettingsOpen == true) return;

        // Leave clicks on real controls alone.
        for (var v = e.Source as Avalonia.Visual; v is not null && !ReferenceEquals(v, this); v = v.GetVisualParent())
            if (v is Button or TextBox or ComboBox or Slider or ListBoxItem or ToggleButton) return;

        if (e.ClickCount == 2)
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else
            BeginMoveDrag(e);
        e.Handled = true;
    }

    // ---------- dialogs ----------
    private async Task<string?> PickFolderAsync()
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add a video folder",
            AllowMultiple = false,
        });
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickSavePathAsync(string suggestedName, string startDir)
    {
        var start = Directory.Exists(startDir) ? await StorageProvider.TryGetFolderFromPathAsync(startDir) : null;
        var ext = Path.GetExtension(suggestedName).TrimStart('.');
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export clip",
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = start,
            DefaultExtension = ext,
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType(ext.ToUpperInvariant() + " video") { Patterns = new[] { "*." + ext } } },
        });
        return file?.TryGetLocalPath();
    }

    // ---------- context menus ----------
    private void OnPlayItemClick(object? sender, RoutedEventArgs e) =>
        Vm?.OpenCommand.Execute((sender as Control)?.DataContext as VideoItemViewModel);

    private void OnRevealItemClick(object? sender, RoutedEventArgs e) =>
        Vm?.RevealCommand.Execute((sender as Control)?.DataContext as VideoItemViewModel);

    private void OnEditItemClick(object? sender, RoutedEventArgs e) =>
        Vm?.BeginEditCommand.Execute((sender as Control)?.DataContext as VideoItemViewModel);

    private void OnFavoriteItemClick(object? sender, RoutedEventArgs e) =>
        Vm?.ToggleFavoriteCommand.Execute((sender as Control)?.DataContext as VideoItemViewModel);

    private void OnRemoveTagClick(object? sender, RoutedEventArgs e) =>
        Vm?.Editor?.RemoveTagCommand.Execute((sender as Control)?.DataContext as string);

    private void OnSuggestedTagClick(object? sender, RoutedEventArgs e) =>
        Vm?.Editor?.AddSuggestedCommand.Execute((sender as Control)?.DataContext as string);

    private void OnRevealFolderClick(object? sender, RoutedEventArgs e) =>
        Vm?.RevealFolderCommand.Execute((sender as Control)?.DataContext as FolderViewModel);

    private void OnRemoveFolderClick(object? sender, RoutedEventArgs e) =>
        Vm?.RemoveFolderCommand.Execute((sender as Control)?.DataContext as FolderViewModel);

    // ---------- fullscreen ----------
    private void OnFullscreenClick(object? sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _preFullscreenState;
            PlayerTopBar.IsVisible = true;
        }
        else
        {
            _preFullscreenState = WindowState;
            WindowState = WindowState.FullScreen;
            PlayerTopBar.IsVisible = false;
        }
    }

    // ---------- keyboard ----------
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.KeyDeviceType == KeyDeviceType.Gamepad) return; // already interpreted by OnPad
        if (PadDebugKeys && e.Key is >= Key.F1 and <= Key.F12)
        {
            // Development aid: drive the controller code path without a controller.
            PadButton[] map =
            {
                PadButton.A, PadButton.B, PadButton.X, PadButton.Y, PadButton.LB, PadButton.RB,
                PadButton.Start, PadButton.View, PadButton.Up, PadButton.Down, PadButton.Left, PadButton.Right,
            };
            OnPad(map[e.Key - Key.F1]);
            e.Handled = true;
            return;
        }
        if (vm.IsControllerMode && !vm.IsPlayerOpen && e.KeyModifiers == KeyModifiers.None &&
            e.Key is Key.Up or Key.Down or Key.Left or Key.Right &&
            e.Source is not TextBox && !IsDropDownOpen(Focused) && e.Source is not Slider)
        {
            OnPad(e.Key switch { Key.Up => PadButton.Up, Key.Down => PadButton.Down, Key.Left => PadButton.Left, _ => PadButton.Right });
            e.Handled = true;
            return;
        }
        if (vm.IsEditing)
        {
            if (e.Key == Key.Escape) { vm.CancelEditCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (vm.IsSettingsOpen)
        {
            if (e.Key == Key.Escape) { vm.CancelSettingsCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (e.Source is TextBox)
        {
            if (e.Key == Key.Escape) { vm.SearchText = ""; Focus(); e.Handled = true; }
            return;
        }

        var cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (cmd && e.Key == Key.W)
        {
            Close(); // hides to the tray while clipping is on, see OnClosing
            e.Handled = true;
            return;
        }
        if (cmd && e.Key == Key.Q)
        {
            (Application.Current as App)?.Quit();
            e.Handled = true;
            return;
        }
        if (!vm.IsPlayerOpen)
        {
            if ((cmd && e.Key == Key.F) || e.Key == Key.OemQuestion) { SearchBox.Focus(); e.Handled = true; }
            return;
        }

        var p = vm.Player;
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Space: case Key.K: p.TogglePlayCommand.Execute(null); break;
            case Key.Left: p.SeekCommand.Execute(p.Position - (shift ? 1000 : 5000)); break;
            case Key.Right: p.SeekCommand.Execute(p.Position + (shift ? 1000 : 5000)); break;
            case Key.J: p.SeekCommand.Execute(p.Position - 10000); break;
            case Key.L when !cmd && shift: p.SeekCommand.Execute(p.Position + 10000); break;
            case Key.L: p.LoopRange = !p.LoopRange; break;
            case Key.OemComma: p.StepFrameBackCommand.Execute(null); break;
            case Key.OemPeriod: p.StepFrameCommand.Execute(null); break;
            case Key.Up: p.Volume = Math.Min(125, p.Volume + 5); break;
            case Key.Down: p.Volume = Math.Max(0, p.Volume - 5); break;
            case Key.M: p.ToggleMuteCommand.Execute(null); break;
            case Key.I when shift: p.JumpToInCommand.Execute(null); break;
            case Key.O when shift: p.JumpToOutCommand.Execute(null); break;
            case Key.I: p.SetInCommand.Execute(null); break;
            case Key.O: p.SetOutCommand.Execute(null); break;
            case Key.X: p.ClearRangeCommand.Execute(null); break;
            case Key.P: p.PreviewRangeCommand.Execute(null); break;
            case Key.E: p.ExportCommand.Execute(null); break;
            case Key.F: ToggleFullscreen(); break;
            case Key.Home: p.SeekCommand.Execute(0d); break;
            case Key.Escape:
                if (WindowState == WindowState.FullScreen) ToggleFullscreen();
                else vm.ClosePlayerCommand.Execute(null);
                break;
            default: e.Handled = false; break;
        }
    }
}
