using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Cnema.Views;

/// <summary>
/// The loading screen at start. It stays while Savefile really gets ready (the video player, the library,
/// the local models that are switched on) and says which step it's on; a click skips it, and anything still
/// loading carries on in the background.
/// </summary>
public partial class SplashWindow : Window
{
    private readonly bool _still;
    private TaskCompletionSource? _skipped = new();

    public SplashWindow() : this(false) { }

    public SplashWindow(bool reducedMotion)
    {
        _still = reducedMotion;
        AvaloniaXamlLoader.Load(this);
        this.FindControl<TextBlock>("Tagline")!.Text = Services.Loc.T("Some saves you never overwrite.");
        this.FindControl<TextBlock>("SkipHint")!.Text = Services.Loc.T("Click to skip");
        this.FindControl<Controls.BlobMascot>("Blob")!.Still = reducedMotion;
        if (!reducedMotion) PrepareIntro();
    }

    /// <summary>Completes when the person clicks to skip.</summary>
    public Task Skipped => _skipped?.Task ?? Task.CompletedTask;

    private void OnSkip(object? sender, PointerPressedEventArgs e) => _skipped?.TrySetResult();

    /// <summary>A fresh <see cref="Skipped"/>, after a click already skipped one step (the update download).</summary>
    public Task SkippedAgain()
    {
        if (_skipped is null) return Task.CompletedTask;
        _skipped = new TaskCompletionSource();
        return _skipped.Task;
    }

    /// <summary>What's being got ready, and how far along (0 to 1).</summary>
    public void Report(string status, double progress)
    {
        this.FindControl<TextBlock>("Status")!.Text = status;
        this.FindControl<ProgressBar>("Bar")!.Value = Math.Clamp(progress, 0, 1);
    }

    private IEnumerable<Control> Intro() => new Control[]
    {
        this.FindControl<Viewbox>("Mark")!, this.FindControl<TextBlock>("AppName")!,
        this.FindControl<TextBlock>("Tagline")!, this.FindControl<Controls.BlobMascot>("Blob")!,
    };

    /// <summary>Everything starts a little low and see-through; <see cref="OnOpened"/> brings it in, one after another.</summary>
    private void PrepareIntro()
    {
        var ease = new CubicEaseOut();
        foreach (var c in Intro())
        {
            c.Opacity = 0;
            c.RenderTransform = TransformOperations.Parse("translateY(14px) scale(0.92)");
            c.Transitions = new Transitions
            {
                new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(380), Easing = ease },
                new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(460), Easing = new BackEaseOut() },
            };
        }
        this.FindControl<ProgressBar>("Bar")!.Transitions = new Transitions
        {
            new DoubleTransition { Property = Avalonia.Controls.Primitives.RangeBase.ValueProperty, Duration = TimeSpan.FromMilliseconds(300), Easing = ease },
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (_still) return;
        var delay = 60;
        foreach (var c in Intro())
        {
            var control = c;
            DispatcherTimer.RunOnce(() =>
            {
                control.Opacity = 1;
                control.RenderTransform = TransformOperations.Parse("translateY(0px) scale(1)");
                if (control is Controls.BlobMascot blob) DispatcherTimer.RunOnce(blob.Cheer, TimeSpan.FromMilliseconds(260));
            }, TimeSpan.FromMilliseconds(delay));
            delay += 170;
        }
    }

    /// <summary>Fades away (at once with Reduced motion) and closes.</summary>
    public async Task FadeOutAsync()
    {
        _skipped = null;
        var card = this.FindControl<Border>("Card")!;
        if (!_still)
        {
            card.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(200) } };
            card.Opacity = 0;
            await Task.Delay(220);
        }
        Close();
    }
}
