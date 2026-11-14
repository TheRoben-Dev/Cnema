using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Cnema.Services;
using Cnema.ViewModels;

namespace Cnema.Views;

// Drag-out: drag a card (or the selected cards) into Discord, a browser or a folder.
public partial class MainWindow
{
    private Point? _dragStart;
    private VideoItemViewModel? _dragItem;
    private bool _dragging;

    private void SetUpDragOut()
    {
        // Cards are Buttons, which mark the press as handled; listen to handled events too.
        VideoGrid.AddHandler(PointerPressedEvent, OnCardPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        VideoGrid.AddHandler(PointerMovedEvent, OnCardPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        VideoGrid.AddHandler(PointerReleasedEvent, (_, _) => { _dragStart = null; _dragItem = null; },
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
        if (_dragItem is not null) _dragStart = e.GetPosition(VideoGrid);
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
