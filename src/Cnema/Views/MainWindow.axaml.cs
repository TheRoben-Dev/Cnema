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
        SetUpDragOut();
        FrameEdit.RegionChanged += (tag, area) => Vm?.Player.MoveFrameRegion(tag, area);
        FrameEdit.RegionPicked += tag => Vm?.Player.PickFrameRegion(tag);

        VideoGrid.ElementPrepared += (_, e) => (e.Element.DataContext as VideoItemViewModel)?.AcquireThumbnail();
        VideoGrid.ElementClearing += (_, e) =>
        {
            if (e.Element.DataContext is not VideoItemViewModel item) return;
            item.EndScrub(); // recycled while hovered: let go of the filmstrip too
            item.ReleaseThumbnail();
        };
        VideoGrid.ElementIndexChanged += (_, e) => (e.Element.DataContext as VideoItemViewModel)?.AcquireThumbnail();

        SurfaceHit.Tapped += (_, _) =>
        {
            // A click on the video also takes the keyboard back from a caption box, so shortcuts work again.
            if (FocusManager?.GetFocusedElement() is TextBox) FocusManager.ClearFocus();
            Vm?.Player.TogglePlayCommand.Execute(null);
        };
        SurfaceHit.DoubleTapped += (_, _) => ToggleFullscreen();
        TheatreHit.Tapped += (_, _) => Vm?.Player.TogglePlayCommand.Execute(null);
        Theatre.PointerMoved += (_, _) => WakeTheatreChrome();
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

    /// <summary>Interface size, reduced motion and the hover glow, from Accessibility and Features.</summary>
    private void ApplyAppearance()
    {
        if (Vm is not { } vm) return;
        var a = vm.Accessibility;
        var scale = Math.Clamp(a.UiScale, 0.75, 2);
        Scaler.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? null : new ScaleTransform(scale, scale);
        Classes.Set("reduced", a.ReducedMotion);
        Classes.Set("noglow", !vm.Features.HoverGlow);
    }

    private Control? Focused => FocusManager?.GetFocusedElement() as Control;

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
        vm.AppearanceChanged += ApplyAppearance;
        vm.PickSettingsSavePath = PickSettingsSavePathAsync;
        vm.PickSettingsFile = PickSettingsFileAsync;
        ApplyAppearance();
        vm.CopyText = async text => { if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text); };
        vm.PickFolder = PickFolderAsync;
        vm.PickFile = PickMediaFileAsync;
        vm.PickAnyFile = PickAnyFileAsync;
        vm.PickAudio = PickAudioFileAsync;
        vm.PickAudios = PickAudioFilesAsync;
        vm.CaptureRecap = CaptureRecap;
        vm.Player.PickAudio = PickAudioFileAsync;
        vm.Player.PickSavePath = PickSavePathAsync;
        // A new caption gets the cursor in its text box, ready to type.
        vm.Player.CaptionAdded += caption => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (CaptionList.ContainerFromItem(caption) is { } container &&
                container.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } box)
            {
                box.Focus();
                box.BringIntoView();
            }
        }, Avalonia.Threading.DispatcherPriority.Background);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsSelecting)) Classes.Set("selecting", vm.IsSelecting);
            if (e.PropertyName == nameof(MainViewModel.IsTheatreOpen)) OnTheatreChanged(vm);
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
            Title = T("Add a video folder"),
            AllowMultiple = false,
        });
        return result.FirstOrDefault()?.TryGetLocalPath();
    }

    private static readonly FilePickerFileType ImageFileType = new(T("Images")) { Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" } };
    private static readonly FilePickerFileType VideoFileType =
        new(T("Videos")) { Patterns = new[] { "*.mp4", "*.mov", "*.mkv", "*.webm", "*.avi", "*.m4v" } };

    private static readonly FilePickerFileType AudioFileType = new(T("Music")) { Patterns = new[] { "*.mp3", "*.m4a", "*.aac", "*.wav", "*.flac", "*.ogg", "*.opus" } };

    private async Task<string?> PickAudioFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Pick a song"),
            AllowMultiple = false,
            FileTypeFilter = new[] { AudioFileType },
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<IReadOnlyList<string>> PickAudioFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("Pick songs for your Wrapped"),
            AllowMultiple = true,
            FileTypeFilter = new[] { AudioFileType },
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    private void OnClearMontageMusicClick(object? sender, RoutedEventArgs e)
    {
        if (Vm?.Montage is { } m) m.MusicPath = null;
    }

    private async Task<string?> PickAnyFileAsync(string title)
    {
        var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = Directory.Exists(ssh) ? await StorageProvider.TryGetFolderFromPathAsync(ssh) : null,
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private async Task<string?> PickMediaFileAsync(string title, bool image)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[] { image ? ImageFileType : VideoFileType },
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private static readonly FilePickerFileType SettingsFileType = new(T("Savefile settings")) { Patterns = new[] { "*.json" } };

    private async Task<string?> PickSettingsSavePathAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = T("Export settings"),
            SuggestedFileName = "Savefile settings.json",
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
            Title = T("Import settings"),
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
            Title = T("Export clip"),
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = start,
            DefaultExtension = ext,
            ShowOverwritePrompt = true,
            FileTypeChoices = new[] { new FilePickerFileType(F("{0} video", ext.ToUpperInvariant())) { Patterns = new[] { "*." + ext } } },
        });
        return file?.TryGetLocalPath();
    }

    // ---------- context menus ----------
    private void OnPlayItemClick(object? sender, RoutedEventArgs e) =>
        Vm?.OpenCommand.Execute((sender as Control)?.DataContext as VideoItemViewModel);

    private void OnMascotPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        Vm?.PokeMascot();
        e.Handled = true;
    }

    private void OnSimilarItemClick(object? sender, RoutedEventArgs e) =>
        Vm?.FindSimilarCommand.Execute((sender as Control)?.DataContext as VideoItemViewModel);

    private void OnRevealItemClick(object? sender, RoutedEventArgs e) =>
        Vm?.RevealCommand.Execute((sender as Control)?.DataContext as VideoItemViewModel);

    private void OnEditItemClick(object? sender, RoutedEventArgs e) =>
        Vm?.BeginEditCommand.Execute((sender as Control)?.DataContext as VideoItemViewModel);

    // Hover scrub: the pointer's x position over a card picks a frame from the video's filmstrip.
    private void OnThumbPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: VideoItemViewModel item } c && Vm is not null)
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
                case "similar":
                    mi.IsVisible = vm.ShowVisual;
                    mi.IsEnabled = vm.CanFindSimilar(item);
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
                    var create = new MenuItem { Header = T("New collection…") };
                    create.Click += (_, _) => vm.NewCollectionWith(new[] { item });
                    rows.Add(create);
                    mi.ItemsSource = rows;
                    break;
            }
        }
    }

    /// <summary>
    /// The quick player listens to a few player keys while the library has the keyboard: play/pause,
    /// previous / next video, seeking, mute and speed. Arrow keys up/down stay with the library lists. Esc closes it.
    /// </summary>
    private static bool HandleMiniPlayerKey(MainViewModel vm, KeyEventArgs e)
    {
        var p = vm.Player;
        if (e.Key == Key.Escape) { vm.CloseMiniPlayerCommand.Execute(null); return true; }
        switch (Shortcuts.Match(e, Shortcuts.Player))
        {
            case "play": p.TogglePlayCommand.Execute(null); return true;
            case "prevClip": vm.StepClipCommand.Execute(-1); return true;
            case "nextClip": vm.StepClipCommand.Execute(1); return true;
            case "back5": p.SeekCommand.Execute(p.Position - 5000); return true;
            case "fwd5": p.SeekCommand.Execute(p.Position + 5000); return true;
            case "back1": p.SeekCommand.Execute(p.Position - 1000); return true;
            case "fwd1": p.SeekCommand.Execute(p.Position + 1000); return true;
            case "mute": p.ToggleMuteCommand.Execute(null); return true;
            case "spoken" when vm.ShowSpeech: p.ToggleSpokenCommand.Execute(null); return true;
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

    private void OnRenameNameClick(object? sender, RoutedEventArgs e)
    {
        Vm?.BeginRenameName((sender as Control)?.DataContext as FolderViewModel);
        // Focus the box that just appeared, with the old name selected.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var box = Vm?.IsRenamingFriend == true ? FriendRenameBox : TagRenameBox;
            box.Focus();
            box.SelectAll();
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void OnRemoveNameClick(object? sender, RoutedEventArgs e) =>
        Vm?.RemoveName((sender as Control)?.DataContext as FolderViewModel);

    private void OnRenameNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Vm?.ConfirmRenameCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Escape) { Vm?.CancelRenameCommand.Execute(null); e.Handled = true; }
    }

    private void OnTogglePlayAll(object? sender, RoutedEventArgs e)
    {
        Vm?.TogglePlayAllCommand.Execute(null);
    }

    private void OnSquadKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Vm?.SaveSquadCommand.Execute(null);
        SquadButton.Flyout?.Hide();
        e.Handled = true;
    }

    // Posted, like the bulk flyouts: hiding right away detaches the button before its Command runs.
    private void OnSquadDone(object? sender, RoutedEventArgs e) =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => SquadButton.Flyout?.Hide());

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

    // ---------- the Wrapped theatre ----------
    private bool _theatreFullscreen;
    private Avalonia.Threading.DispatcherTimer? _theatreIdle;

    /// <summary>The first viewing of a Wrapped fills the screen; closing it puts the window back.</summary>
    private void OnTheatreChanged(MainViewModel vm)
    {
        if (vm.IsTheatreOpen)
        {
            if (vm.TheatreFirst && WindowState != WindowState.FullScreen)
            {
                _preFullscreenState = WindowState;
                WindowState = WindowState.FullScreen;
                _theatreFullscreen = true;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => TheatreSurface.Refresh(), Avalonia.Threading.DispatcherPriority.Background);
            WakeTheatreChrome();
        }
        else
        {
            _theatreIdle?.Stop();
            Cursor = null;
            if (_theatreFullscreen && WindowState == WindowState.FullScreen) WindowState = _preFullscreenState;
            _theatreFullscreen = false;
        }
    }

    /// <summary>The controls and the pointer show on a move and fade after a couple of quiet seconds while it plays.</summary>
    private void WakeTheatreChrome()
    {
        if (Vm is not { IsTheatreOpen: true }) return;
        TheatreChrome.Opacity = 1;
        Theatre.Cursor = null;
        _theatreIdle ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromSeconds(2.5), Avalonia.Threading.DispatcherPriority.Background, (_, _) =>
        {
            _theatreIdle!.Stop();
            if (Vm is not { IsTheatreOpen: true } vm || !vm.Player.IsPlaying || TheatreChrome.IsPointerOver && TheatreChromeBarHovered()) return;
            TheatreChrome.Opacity = 0;
            Theatre.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.None);
        });
        _theatreIdle.Stop();
        _theatreIdle.Start();
    }

    private bool TheatreChromeBarHovered() =>
        TheatreChrome.GetVisualDescendants().OfType<Button>().Any(b => b.IsPointerOver) ||
        TheatreChrome.GetVisualDescendants().OfType<Controls.TrimTimeline>().Any(t => t.IsPointerOver);

    // ---------- keyboard ----------
    // Some platforms leave KeySymbol empty for shifted keys, so Shift+/ (US layout) counts too.
    private static bool IsQuestionMark(KeyEventArgs e) =>
        e.KeySymbol == "?" || (e.Key == Key.OemQuestion && e.KeyModifiers == KeyModifiers.Shift);

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (vm.IsShowingShortcuts && vm.ShortcutSheet is { } sheet)
        {
            // While a row waits for its new key, that key belongs to the sheet.
            if (sheet.HandleKey(e)) { e.Handled = true; return; }
            if (e.Key == Key.Escape || IsQuestionMark(e)) { vm.CloseShortcutsCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (vm.IsSetupOpen) return;
        if (vm.IsTheatreOpen)
        {
            // Only pause, scrubbing and the sound: nothing to edit here.
            var t = vm.Player;
            switch (e.Key)
            {
                case Key.Escape: vm.CloseTheatreCommand.Execute(null); break;
                case Key.Space or Key.K: t.TogglePlayCommand.Execute(null); break;
                case Key.Left or Key.J: t.SkipBackCommand.Execute(null); break;
                case Key.Right or Key.L: t.SkipForwardCommand.Execute(null); break;
                case Key.M: t.ToggleMuteCommand.Execute(null); break;
                default: return;
            }
            WakeTheatreChrome();
            e.Handled = true;
            return;
        }
        if (vm.IsWrappedGiftOpen)
        {
            if (e.Key == Key.Escape) { vm.CloseWrappedGiftCommand.Execute(null); e.Handled = true; }
            return;
        }
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
        if (vm.IsShowingRecap)
        {
            if (e.Key == Key.Escape) { vm.CloseRecapCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (vm.IsMakingMontage)
        {
            if (e.Key == Key.Escape) { vm.CloseMontageCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (vm.IsShowingRecorders)
        {
            if (e.Key == Key.Escape) { vm.CloseRecordersCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (vm.IsShowingRename)
        {
            if (e.Key == Key.Escape) { vm.CloseSmartRenameCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (vm.IsShowingMerge)
        {
            if (e.Key == Key.Escape) { vm.CloseMergeCommand.Execute(null); e.Handled = true; }
            return;
        }
        if (vm.IsShowingBackup)
        {
            if (e.Key == Key.Escape) { vm.CloseBackupCommand.Execute(null); e.Handled = true; }
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
        if (e.Source is TextBox box)
        {
            // Esc leaves a text box; in the search box it also clears the search.
            if (e.Key == Key.Escape)
            {
                if (ReferenceEquals(box, SearchBox)) vm.SearchText = "";
                FocusManager?.ClearFocus();
                e.Handled = true;
            }
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
            case "prevClip": vm.StepClipCommand.Execute(-1); break;
            case "nextClip": vm.StepClipCommand.Execute(1); break;
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
            case "spoken" when vm.ShowSpeech: p.ToggleSpokenCommand.Execute(null); break;
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
            case "effects": p.ToggleToolsCommand.Execute(null); break;
            case "caption": p.AddCaptionCommand.Execute(null); break;
            case "fullscreen": ToggleFullscreen(); break;
            case "start": p.SeekCommand.Execute(0d); break;
            case "thumb": vm.UseFrameAsThumbnailCommand.Execute(null); break;
            case "grab": vm.SaveFrameCommand.Execute(null); break;
            case "punchIn": p.PunchInCommand.Execute(null); break;
            case "blur": p.AddBlurCommand.Execute(null); break;
            default: e.Handled = false; break;
        }
    }

    /// <summary>Draws Year in clips (with a small Savefile header) into a PNG, twice the screen size.</summary>
    private bool CaptureRecap(string path)
    {
        var content = this.FindControl<StackPanel>("RecapContent");
        var header = this.FindControl<StackPanel>("RecapImageHeader");
        if (content is null || header is null) return false;
        var background = this.FindResource("Bg1") as IBrush ?? Brushes.Black;
        header.IsVisible = true;
        try
        {
            content.UpdateLayout();
            const double scale = 2, pad = 32;
            var size = content.Bounds.Size;
            var dpi = new Vector(96 * scale, 96 * scale);
            using var inner = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new PixelSize((int)Math.Ceiling(size.Width * scale), (int)Math.Ceiling(size.Height * scale)), dpi);
            inner.Render(content);
            // The page on the dialog's background with a margin all round, so it reads as a card when shared.
            using var page = new Avalonia.Media.Imaging.RenderTargetBitmap(
                new PixelSize((int)Math.Ceiling((size.Width + pad * 2) * scale), (int)Math.Ceiling((size.Height + pad * 2) * scale)), dpi);
            using (var ctx = page.CreateDrawingContext())
            {
                ctx.FillRectangle(background, new Rect(0, 0, size.Width + pad * 2, size.Height + pad * 2));
                // The source rectangle is in the bitmap's pixels.
                ctx.DrawImage(inner, new Rect(inner.PixelSize.ToSize(1)), new Rect(pad, pad, size.Width, size.Height));
            }
            page.Save(path);
            return true;
        }
        finally
        {
            header.IsVisible = false;
        }
    }
}
