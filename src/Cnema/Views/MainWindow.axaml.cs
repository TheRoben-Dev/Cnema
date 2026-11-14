using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
        vm.PickFolder = PickFolderAsync;
        vm.Player.PickSavePath = PickSavePathAsync;
        vm.PropertyChanged += (_, e) =>
        {
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
