using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Cnema.ViewModels;

namespace Cnema.Views;

/// <summary>
/// The Quick player sized to the video: as big as the window allows with the video's own shape, so there are no
/// black bars beside or above it, and a smooth resize when the next video has another shape. Also the
/// favourite star's little burst.
/// </summary>
public partial class MainWindow
{
    /// <summary>The bar's buttons need about this much room; a tall video gets this width and thin side bars.</summary>
    private const double QuickMinWidth = 660;
    private bool _quickAnimated;

    private void HookQuickPlayer(MainViewModel vm)
    {
        MiniArea.SizeChanged += (_, _) => FitQuickPlayer(false);
        MiniBar.SizeChanged += (_, e) => { if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5) FitQuickPlayer(false); };
        vm.Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PlayerViewModel.VideoWidth) or nameof(PlayerViewModel.VideoHeight))
                FitQuickPlayer(vm.IsMiniPlayerOpen);
        };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsMiniPlayerOpen) && vm.IsMiniPlayerOpen) FitQuickPlayer(false);
        };
    }

    private void FitQuickPlayer(bool animate)
    {
        if (Vm is not { } vm) return;
        var area = MiniArea.Bounds.Size;
        var m = MiniPlayer.Margin;
        var availW = area.Width - m.Left - m.Right;
        var availH = area.Height - m.Top - m.Bottom;
        if (availW <= 0 || availH <= 0) return;
        double w = availW, h = availH;
        var (vw, vh) = (vm.Player.VideoWidth, vm.Player.VideoHeight);
        if (vw > 0 && vh > 0)
        {
            var bar = MiniBar.Bounds.Height > 0 ? MiniBar.Bounds.Height + MiniBar.Margin.Top + MiniBar.Margin.Bottom : 80;
            var border = MiniPlayer.BorderThickness;
            var chromeH = bar + border.Top + border.Bottom;
            var chromeW = border.Left + border.Right;
            var aspect = vw / (double)vh;
            var videoW = Math.Min(availW - chromeW, (availH - chromeH) * aspect);
            w = Math.Clamp(videoW + chromeW, Math.Min(QuickMinWidth, availW), availW);
            h = Math.Min(availH, videoW / aspect + chromeH);
        }
        var smooth = animate && !vm.ReducedMotion && MiniPlayer.IsEffectivelyVisible;
        if (smooth != _quickAnimated)
        {
            _quickAnimated = smooth;
            MiniPlayer.Transitions = smooth ? new Transitions
            {
                new DoubleTransition { Property = WidthProperty, Duration = TimeSpan.FromMilliseconds(260), Easing = new CubicEaseOut() },
                new DoubleTransition { Property = HeightProperty, Duration = TimeSpan.FromMilliseconds(260), Easing = new CubicEaseOut() },
            } : null;
        }
        MiniPlayer.Width = Math.Round(w);
        MiniPlayer.Height = Math.Round(h);
    }

    /// <summary>The star in the Quick player: favourite or not, with a glow that bursts out when it's turned on.</summary>
    private void OnQuickFavorite(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => ToggleQuickFavorite();

    private void ToggleQuickFavorite()
    {
        if (Vm is not { } vm || vm.Player.Current is not { } item) return;
        vm.ToggleFavoriteCommand.Execute(item);
        vm.Player.RefreshFavorite();
        if (item.IsFavorite) _ = BurstAsync(vm.ReducedMotion);
    }

    private async Task BurstAsync(bool still)
    {
        if (still) return;
        var ring = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(520),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.9), new Setter(ScaleTransform.ScaleXProperty, 0.5), new Setter(ScaleTransform.ScaleYProperty, 0.5) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.0), new Setter(ScaleTransform.ScaleXProperty, 2.1), new Setter(ScaleTransform.ScaleYProperty, 2.1) } },
            },
        };
        var pop = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(380),
            Easing = new BackEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(ScaleTransform.ScaleXProperty, 0.6), new Setter(ScaleTransform.ScaleYProperty, 0.6) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(ScaleTransform.ScaleXProperty, 1.0), new Setter(ScaleTransform.ScaleYProperty, 1.0) } },
            },
        };
        await Task.WhenAll(ring.RunAsync(QuickFavRing), pop.RunAsync(QuickFavStar));
        QuickFavRing.Opacity = 0;
    }
}
