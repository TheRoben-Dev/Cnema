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
        MiniHit.Tapped += (_, _) => Vm?.Player.TogglePlayCommand.Execute(null);
        MiniHit.DoubleTapped += (_, _) => Vm?.ExpandMiniPlayerCommand.Execute(null);
        // Clicking the dimmed library around the quick player closes it, like Esc.
        MiniScrim.Tapped += (_, _) => Vm?.CloseMiniPlayerCommand.Execute(null);

        // Tunnel so shortcuts work even when a button/slider has focus; skipped while typing.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(ContextRequestedEvent, OnContextRequestedTunnel, RoutingStrategies.Tunnel);

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
        ApplyAppearance();
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

    /// <summary>Interface size, reduced motion and the hover glow, from Accessibility and Features.</summary>
    private void ApplyAppearance()
    {
        if (Vm is not { } vm) return;
        var a = vm.Accessibility;
        var scale = Math.Clamp(a.UiScale, 0.75, 2) * (vm.IsControllerMode ? ControllerScale : 1);
        Scaler.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? null : new ScaleTransform(scale, scale);
        Classes.Set("reduced", a.ReducedMotion);
        Classes.Set("noglow", !vm.Features.HoverGlow);
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
        if (Vm is not { IsPlayerOpen: false, IsEditing: false, IsSettingsOpen: false, IsConfirmingDelete: false } vm || vm.Videos.Count == 0) return false;
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
        foreach (var list in new[] { FolderList, CollectionList, TagList })
            for (var i = 0; list.IsVisible && i < list.ItemCount; i++)
                if (list.ContainerFromIndex(i) is { } item) stops.Add(item);
        stops.AddRange(new Control[] { CleanupButton, StatsButton, ClipToggle, SaveClipButton, SessionButton, AddFolderButton, SettingsButton }
            .Where(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled));
        return stops;
    }

    private bool FocusSidebar()
    {
        var list = Vm?.SelectedTag is not null ? TagList : Vm?.SelectedCollection is not null ? CollectionList : FolderList;
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
        var scope = (Control)(Vm?.IsSetupOpen == true ? SetupScrim
            : Vm?.IsShowingShortcuts == true ? ShortcutsScrim
            : Vm?.IsCleaningUp == true ? CleanupScrim
            : Vm?.IsShowingStats == true ? StatsScrim
            : Vm?.IsConfirmingDelete == true ? DeleteScrim
            : Vm?.IsSettingsOpen == true ? SettingsScrim : EditScrim);
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
        if (vm.IsSetupOpen)
        {
            if (b == PadButton.Start) vm.FinishSetupCommand.Execute(null);
            else if (b == PadButton.A) SendKey(Focused is CheckBox or ToggleSwitch ? Key.Space : Key.Enter);
            else if (direction) MoveInDialog(b);
            return;
        }
        if (vm.IsShowingShortcuts)
        {
            if (b == PadButton.B) vm.CloseShortcutsCommand.Execute(null);
            else if (b == PadButton.A) SendKey(Key.Enter);
            else if (direction) MoveInDialog(b);
            return;
        }
        if (vm.IsShowingWhatsNew)
        {
            if (b is PadButton.B or PadButton.A) vm.CloseWhatsNewCommand.Execute(null);
            return;
        }
        if (vm.IsShowingStats)
        {
            if (b is PadButton.B or PadButton.A) vm.CloseStatsCommand.Execute(null);
            return;
        }
        if (vm.IsCleaningUp && !vm.IsConfirmingDelete)
        {
            if (b == PadButton.B) vm.CloseCleanupCommand.Execute(null);
            else if (b == PadButton.A) SendKey(Focused is CheckBox ? Key.Space : Key.Enter);
            else if (direction) MoveInDialog(b);
            return;
        }
        if (vm.IsConfirmingDelete)
        {
            if (b == PadButton.B) vm.CancelDeleteCommand.Execute(null);
            else if (b == PadButton.A) SendKey(Key.Enter);
            else if (direction) MoveInDialog(b);
            return;
        }
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
            else if (b is PadButton.LB or PadButton.RB && vm.SettingsEditor is { } settingsPage)
                settingsPage.CyclePage(b == PadButton.LB ? -1 : 1);
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
        vm.AppearanceChanged += ApplyAppearance;
        vm.PickSettingsSavePath = PickSettingsSavePathAsync;
        vm.PickSettingsFile = PickSettingsFileAsync;
        _pad.IsActive = () => IsActive && IsVisible;
        _pad.Pressed += OnPad;
        _pad.ConnectionChanged += UpdatePadStatus;
        ApplyControllerMode();
        vm.CopyText = async text => { if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text); };
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
            if (e.PropertyName == nameof(MainViewModel.IsSelecting)) Classes.Set("selecting", vm.IsSelecting);
            // A paused video sends no new frames, so the surface that just appeared draws the last one itself.
            if (e.PropertyName is nameof(MainViewModel.IsMiniPlayerOpen) or nameof(MainViewModel.IsPlayerOpen))
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { Surface.Refresh(); MiniSurface.Refresh(); },
                    Avalonia.Threading.DispatcherPriority.Background);
            if (e.PropertyName == nameof(MainViewModel.IsNamingCollection) && vm.IsNamingCollection)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { CollectionNameBox.Focus(); CollectionNameBox.SelectAll(); },
                    Avalonia.Threading.DispatcherPriority.Background);
            if (e.PropertyName == nameof(MainViewModel.SettingsEditor) && vm.SettingsEditor is { } editor)
                editor.PropertyChanged += (_, pe) =>
                {
                    if (pe.PropertyName == nameof(SettingsViewModel.Page)) SettingsScroll.Offset = default;
                };
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

    private static readonly FilePickerFileType SettingsFileType = new("Cnema settings") { Patterns = new[] { "*.json" } };

    private async Task<string?> PickSettingsSavePathAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export settings",
            SuggestedFileName = "Cnema settings.json",
            DefaultExtension = "json",
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { SettingsFileType },
        });
        return file?.TryGetLocalPath();
    }

    private async Task<string?> PickSettingsFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import settings",
            AllowMultiple = false,
            FileTypeFilter = new[] { SettingsFileType },
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
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

    // Hover scrub: the pointer's x position over a card picks a frame from the video's filmstrip.
    private void OnThumbPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: VideoItemViewModel item } c && Vm is { IsControllerMode: false })
        {
            item.Scrub(c.Bounds.Width > 0 ? e.GetPosition(c).X / c.Bounds.Width : 0);
            item.BeginScrub();
        }
    }

    private void OnThumbPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: VideoItemViewModel item } c && c.Bounds.Width > 0)
            item.Scrub(e.GetPosition(c).X / c.Bounds.Width);
    }

    private void OnThumbPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: VideoItemViewModel item }) item.EndScrub();
    }

    private void OnDeleteItemClick(object? sender, RoutedEventArgs e) =>
        Vm?.RequestDeleteCommand.Execute((sender as Control)?.DataContext as VideoItemViewModel);

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

    private void OnWatchedItemClick(object? sender, RoutedEventArgs e) =>
        Vm?.ToggleWatchedCommand.Execute((sender as Control)?.DataContext as VideoItemViewModel);

    private void OnResetThumbnailClick(object? sender, RoutedEventArgs e) =>
        Vm?.ResetThumbnailCommand.Execute((sender as Control)?.DataContext as VideoItemViewModel);

    private void OnRemoveFromCollectionClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is VideoItemViewModel item) Vm?.RemoveFromCollection(item);
    }

    /// <summary>
    /// The card menu is built per video: "Add to collection" lists the collections there are now,
    /// and items for features that are off are hidden. This runs on the way down (tunnel), before
    /// the card's own handler opens the menu; at that point the menu has no DataContext yet.
    /// </summary>
    private void OnContextRequestedTunnel(object? sender, ContextRequestedEventArgs e)
    {
        if (Vm is not { } vm) return;
        for (var v = e.Source as Avalonia.Visual; v is not null; v = v.GetVisualParent())
        {
            if (v is not Button { Classes: var classes, DataContext: VideoItemViewModel item, ContextMenu: { } menu } || !classes.Contains("card"))
                continue;
            PrepareCardMenu(vm, menu, item);
            return;
        }
    }

    private static void PrepareCardMenu(MainViewModel vm, ContextMenu menu, VideoItemViewModel item)
    {
        foreach (var mi in menu.Items.OfType<MenuItem>())
        {
            switch (mi.Tag as string)
            {
                case "watched":
                    mi.IsVisible = vm.Features.Unwatched;
                    break;
                case "uncollect":
                    mi.IsVisible = vm.ShowCollections && vm.IsInCollectionView;
                    break;
                case "collections":
                    mi.IsVisible = vm.ShowCollections;
                    var rows = new List<object>();
                    foreach (var row in vm.Collections)
                    {
                        var target = row;
                        var sub = new MenuItem { Header = row.Name };
                        sub.Click += (_, _) => vm.AddToCollection(new[] { item }, target);
                        rows.Add(sub);
                    }
                    if (rows.Count > 0) rows.Add(new Separator());
                    var create = new MenuItem { Header = "New collection…" };
                    create.Click += (_, _) => vm.NewCollectionWith(new[] { item });
                    rows.Add(create);
                    mi.ItemsSource = rows;
                    break;
            }
        }
    }

    /// <summary>
    /// The quick player listens to a few player keys while the library has the keyboard: play/pause,
    /// seeking, mute and speed. Arrow keys up/down stay with the library lists. Esc closes it.
    /// </summary>
    private static bool HandleMiniPlayerKey(MainViewModel vm, KeyEventArgs e)
    {
        var p = vm.Player;
        if (e.Key == Key.Escape) { vm.CloseMiniPlayerCommand.Execute(null); return true; }
        switch (Shortcuts.Match(e, Shortcuts.Player))
        {
            case "play": p.TogglePlayCommand.Execute(null); return true;
            case "back5": p.SeekCommand.Execute(p.Position - 5000); return true;
            case "fwd5": p.SeekCommand.Execute(p.Position + 5000); return true;
            case "back1": p.SeekCommand.Execute(p.Position - 1000); return true;
            case "fwd1": p.SeekCommand.Execute(p.Position + 1000); return true;
            case "mute": p.ToggleMuteCommand.Execute(null); return true;
            case "speedUp": p.SpeedUpCommand.Execute(null); return true;
            case "speedDown": p.SpeedDownCommand.Execute(null); return true;
            default: return false;
        }
    }

    /// <summary>A row in a flyout list was picked: close the flyout it sits in.</summary>
    private void OnFlyoutRowClick(object? sender, RoutedEventArgs e)
    {
        // Closed after the click: closing right away detaches the row before its Command runs.
        for (var l = (sender as Avalonia.LogicalTree.ILogical)?.LogicalParent; l is not null; l = l.LogicalParent)
            if (l is Popup popup) { Avalonia.Threading.Dispatcher.UIThread.Post(popup.Close); return; }
    }

    private void OnRenameCollectionClick(object? sender, RoutedEventArgs e) =>
        Vm?.BeginRenameCollection((sender as Control)?.DataContext as FolderViewModel);

    private void OnDeleteCollectionClick(object? sender, RoutedEventArgs e) =>
        Vm?.DeleteCollectionCommand.Execute((sender as Control)?.DataContext as FolderViewModel);

    private void OnCollectionNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Vm?.ConfirmCollectionNameCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { Vm?.CancelNamingCommand.Execute(null); e.Handled = true; }
    }

    private void OnBulkTagKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Vm?.BulkAddTagCommand.Execute(null);
        OnBulkFlyoutDone(sender, e);
        e.Handled = true;
    }

    /// <summary>A choice in a bulk-bar flyout was made: close it, the toast says what happened.</summary>
    private void OnBulkFlyoutDone(object? sender, RoutedEventArgs e) =>
        // Posted: hiding the flyout right away detaches the button before its Command runs.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            BulkTagButton.Flyout?.Hide();
            BulkCollectionButton.Flyout?.Hide();
        });

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
    // Some platforms leave KeySymbol empty for shifted keys, so Shift+/ (US layout) counts too.
    private static bool IsQuestionMark(KeyEventArgs e) =>
        e.KeySymbol == "?" || (e.Key == Key.OemQuestion && e.KeyModifiers == KeyModifiers.Shift);

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
        if (vm.IsShowingShortcuts && vm.ShortcutSheet is { } sheet)
        {
            // While a row waits for its new key, that key belongs to the sheet.
            if (sheet.HandleKey(e)) { e.Handled = true; return; }
            if (e.Key == Key.Escape || IsQuestionMark(e)) { vm.CloseShortcutsCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (vm.IsSetupOpen) return;
        if (vm.IsShowingWhatsNew)
        {
            if (e.Key is Key.Escape or Key.Enter) { vm.CloseWhatsNewCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (vm.IsConfirmingDelete)
        {
            if (e.Key == Key.Escape) { vm.CancelDeleteCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (vm.IsCleaningUp)
        {
            if (e.Key == Key.Escape) { vm.CloseCleanupCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (vm.IsShowingStats)
        {
            if (e.Key == Key.Escape) { vm.CloseStatsCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (vm.IsSelecting && !vm.IsPlayerOpen && e.Source is not TextBox)
        {
            if (e.Key == Key.Escape) { vm.ToggleSelectModeCommand.Execute(null); e.Handled = true; return; }
            if (e.Key == Key.A && e.KeyModifiers is KeyModifiers.Control or KeyModifiers.Meta)
            {
                vm.SelectAllCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (e.Key is Key.Delete or Key.Back) { vm.BulkDeleteCommand.Execute(null); e.Handled = true; return; }
        }
        if (e.Key is Key.Delete or Key.Back && e.Source is Button { DataContext: VideoItemViewModel victim } && !vm.IsPlayerOpen)
        {
            vm.RequestDeleteCommand.Execute(victim);
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
            else if (e.Key == Key.Tab && e.KeyModifiers.HasFlag(KeyModifiers.Control) && vm.SettingsEditor is { } page)
            {
                page.CyclePage(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                e.Handled = true;
            }
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
        // "?" is Shift+/ on a US keyboard and Shift++ on a Swedish one, so match the character, not the key.
        if (IsQuestionMark(e))
        {
            vm.ShowShortcutsCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (!vm.IsPlayerOpen && vm.IsMiniPlayerOpen && HandleMiniPlayerKey(vm, e))
        {
            e.Handled = true;
            return;
        }
        if (!vm.IsPlayerOpen)
        {
            e.Handled = true;
            switch (Shortcuts.Match(e, Shortcuts.Library))
            {
                case "search": SearchBox.Focus(); break;
                case "settings": vm.OpenSettingsCommand.Execute(null); break;
                case "refresh": vm.RescanCommand.Execute(null); break;
                case "select": vm.ToggleSelectModeCommand.Execute(null); break;
                default: e.Handled = false; break;
            }
            return;
        }

        var p = vm.Player;
        if (e.Key == Key.Escape)
        {
            if (WindowState == WindowState.FullScreen) ToggleFullscreen();
            else vm.ClosePlayerCommand.Execute(null);
            e.Handled = true;
            return;
        }
        e.Handled = true;
        switch (Shortcuts.Match(e, Shortcuts.Player))
        {
            case "play": p.TogglePlayCommand.Execute(null); break;
            case "back5": p.SeekCommand.Execute(p.Position - 5000); break;
            case "fwd5": p.SeekCommand.Execute(p.Position + 5000); break;
            case "back1": p.SeekCommand.Execute(p.Position - 1000); break;
            case "fwd1": p.SeekCommand.Execute(p.Position + 1000); break;
            case "back10": p.SeekCommand.Execute(p.Position - 10000); break;
            case "fwd10": p.SeekCommand.Execute(p.Position + 10000); break;
            case "loop": p.ToggleLoopCommand.Execute(null); break;
            case "abLoop": p.AbLoopCommand.Execute(null); break;
            case "speedUp": p.SpeedUpCommand.Execute(null); break;
            case "speedDown": p.SpeedDownCommand.Execute(null); break;
            case "nextChapter": p.NextChapterCommand.Execute(null); break;
            case "prevChapter": p.PreviousChapterCommand.Execute(null); break;
            case "frameBack": p.StepFrameBackCommand.Execute(null); break;
            case "frameFwd": p.StepFrameCommand.Execute(null); break;
            case "volUp": p.Volume = Math.Min(125, p.Volume + 5); break;
            case "volDown": p.Volume = Math.Max(0, p.Volume - 5); break;
            case "mute": p.ToggleMuteCommand.Execute(null); break;
            case "jumpIn": p.JumpToInCommand.Execute(null); break;
            case "jumpOut": p.JumpToOutCommand.Execute(null); break;
            case "setIn": p.SetInCommand.Execute(null); break;
            case "setOut": p.SetOutCommand.Execute(null); break;
            case "clear": p.ClearRangeCommand.Execute(null); break;
            case "addPart": p.AddSegmentCommand.Execute(null); break;
            case "prevMarker": p.PreviousMarkerCommand.Execute(null); break;
            case "nextMarker": p.NextMarkerCommand.Execute(null); break;
            case "preview": p.PreviewRangeCommand.Execute(null); break;
            case "export": p.ExportCommand.Execute(null); break;
            case "fullscreen": ToggleFullscreen(); break;
            case "start": p.SeekCommand.Execute(0d); break;
            case "thumb": vm.UseFrameAsThumbnailCommand.Execute(null); break;
            default: e.Handled = false; break;
        }
    }
}
