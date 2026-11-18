using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Cnema.Services;
using Cnema.ViewModels;
using static Cnema.Services.Loc;

namespace Cnema.Views;

// Drag-out: drag a card (or the selected cards) into Discord, a browser or a folder.
public partial class MainWindow
{
    private Point? _dragStart;
    private VideoItemViewModel? _dragItem;
    private bool _dragging;
    /// <summary>The mouse button is still down since the drag began (a Discord-sized copy waits for it).</summary>
    private bool _held;

    private void SetUpDragOut()
    {
        // Cards are Buttons, which mark the press as handled; listen to handled events too.
        VideoGrid.AddHandler(PointerPressedEvent, OnCardPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        VideoGrid.AddHandler(PointerMovedEvent, OnCardPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        VideoGrid.AddHandler(PointerReleasedEvent, (_, _) => { _dragStart = null; _dragItem = null; _held = false; },
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStart = null;
        _dragItem = null;
        if (Vm is not { AllowDragOut: true } || _dragging) return;
        if (!e.GetCurrentPoint(VideoGrid).Properties.IsLeftButtonPressed) return;
        // Not from the small buttons on the card (favourite, edit, delete).
        if (e.Source is Visual src && src.FindAncestorOfType<Button>(includeSelf: true) is { } b && !b.Classes.Contains("card")) return;
        _dragItem = (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true)?.DataContext as VideoItemViewModel;
        if (_dragItem is not null) { _dragStart = e.GetPosition(VideoGrid); _held = true; }
    }

    private async void OnCardPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || _dragItem is not { } item || _dragging) return;
        var d = e.GetPosition(VideoGrid) - start;
        if (Math.Abs(d.X) < 8 && Math.Abs(d.Y) < 8) return;
        _dragStart = null;
        _dragItem = null;

        var items = Vm is { IsSelecting: true } vm && item.IsSelected
            ? vm.Videos.Where(v => v.IsSelected).ToList()
            : new List<VideoItemViewModel> { item };
        var paths = items.Select(v => v.Entry.Path).Where(File.Exists).ToList();
        if (paths.Count == 0) return;

        _dragging = true;
        try
        {
            // The card would otherwise stay pressed and open the video when the drag ends.
            e.Pointer.Capture(null);
            // Too big for Discord: drag a copy that fits, made now (while the button is held) or earlier.
            if (Vm is { } main && paths.Any(main.NeedsDragCopy))
            {
                var missing = paths.Where(p => main.NeedsDragCopy(p) && main.ReadyDragCopy(p) is null).ToList();
                if (missing.Count > 0)
                {
                    var mb = main.DragLimitBytes / 1_000_000;
                    main.ShowToast(F("Making a {0} MB copy for Discord. Keep holding, or drag it again in a moment.", mb));
                    try
                    {
                        foreach (var p in missing) await main.MakeDragCopyAsync(p, null, CancellationToken.None);
                    }
                    catch (TrimService.TooLongException)
                    {
                        // Hours of session at the lowest bitrate would still be too big: no point encoding it.
                        main.ShowToast(F("Too long for a {0} MB copy, so the original goes instead. Trim it to {1} or shorter to share it there.",
                            mb, TrimService.LongestText(main.DragLimitBytes)));
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Couldn't make the Discord-sized copy: " + ex.Message);
                        main.ShowToast(T("Couldn't make a smaller copy, so the original goes instead."));
                    }
                    if (!_held)
                    {
                        main.ShowToast(T("The Discord-sized copy is ready: drag the video again."));
                        return;
                    }
                }
                paths = paths.Select(p => main.NeedsDragCopy(p) ? main.ReadyDragCopy(p) ?? p : p).ToList();
            }
            var data = new DataTransfer();
            foreach (var p in paths)
                if (await StorageProvider.TryGetFileFromPathAsync(p) is { } f) data.Add(DataTransferItem.CreateFile(f));
            if (data.Items.Count == 0) return;
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy | DragDropEffects.Link);
        }
        catch (Exception ex)
        {
            Log.Warn("Drag-out failed: " + ex.Message);
        }
        finally
        {
            _dragging = false;
        }
    }
}
