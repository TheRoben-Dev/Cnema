using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cnema.Controls;
using Cnema.Services;

namespace Cnema.Views;

/// <summary>
/// Draws the year video's stat cards as pictures: flat Savefile colours (violet, near-black, lavender paper),
/// big type and the blob. Laid out at 1920×1080 and drawn at the video's size. Must run on the UI thread.
/// </summary>
public static class WrappedCards
{
    private const double W = 1920, H = 1080;

    public static List<string> Render(IReadOnlyList<WrappedCard> cards, string dir, int width, int height)
    {
        Directory.CreateDirectory(dir);
        var dark = ThemeService.Resolve(ThemeService.Schemes[0], false);
        var light = ThemeService.Resolve(ThemeService.Schemes[0], true);
        var violet = Color.Parse(dark.Accent);
        var night = Color.Parse(dark.Bg0);
        var paper = Color.Parse(light.Bg0);
        var ink = Color.Parse(light.Text);
        var deep = Color.Parse(light.Accent);
        var paths = new List<string>();
        for (var i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            var (bg, fg, big, small) = c.Kind switch
            {
                "intro" or "squad" or "outro" => (night, Colors.White, Colors.White, Color.Parse(dark.Highlight)),
                "game" or "laughs" => (paper, ink, deep, ink),
                _ => (violet, Colors.White, Colors.White, Colors.White),
            };
            var page = Build(c, new SolidColorBrush(bg), new SolidColorBrush(fg), new SolidColorBrush(big), new SolidColorBrush(small), new SolidColorBrush(violet));
            page.Measure(new Size(W, H));
            page.Arrange(new Rect(0, 0, W, H));
            using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96 * width / W, 96 * height / H));
            bitmap.Render(page);
            var path = Path.Combine(dir, $"card{i:00}.png");
            bitmap.Save(path);
            paths.Add(path);
        }
        return paths;
    }

    private static Control Build(WrappedCard c, IBrush bg, IBrush fg, IBrush big, IBrush small, IBrush violet)
    {
        var root = new Grid { Width = W, Height = H, Background = bg };
        TextBlock Text(string s, double size, IBrush brush, FontWeight weight = FontWeight.Normal, double opacity = 1) => new()
        {
            Text = s, FontSize = size, Foreground = brush, FontWeight = weight, Opacity = opacity,
            TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("fonts:Inter#Inter, $Default"),
        };
        // A long name shrinks to fit rather than wrapping mid-word.
        Control Fit(TextBlock t, double maxWidth)
        {
            t.TextWrapping = TextWrapping.NoWrap;
            return new Viewbox { Child = t, StretchDirection = StretchDirection.DownOnly, MaxWidth = maxWidth, HorizontalAlignment = HorizontalAlignment.Left };
        }

        var brand = Text("savefile", 40, fg, FontWeight.SemiBold, 0.8);
        brand.Margin = new Thickness(120, 90, 0, 0);
        brand.VerticalAlignment = VerticalAlignment.Top;
        brand.HorizontalAlignment = HorizontalAlignment.Left;

        switch (c.Kind)
        {
            case "intro":
            {
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(120, 0, 0, 40) };
                stack.Children.Add(Fit(Text(c.Big, 190, big, FontWeight.Bold), 1150));
                stack.Children.Add(Text(c.Small, 190, small, FontWeight.Bold));
                root.Children.Add(stack);
                root.Children.Add(Blob(violet, 520, 470, HorizontalAlignment.Right, new Thickness(0, 0, 150, 150)));
                root.Children.Add(brand);
                break;
            }
            case "outro":
            {
                root.Children.Add(Blob(violet, 460, 415, HorizontalAlignment.Left, new Thickness(150, 0, 0, 170)));
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(760, 0, 120, 30), Spacing = 18 };
                stack.Children.Add(Text(c.Kicker, 96, big, FontWeight.Bold));
                stack.Children.Add(Text(c.Big, 56, small, FontWeight.SemiBold));
                stack.Children.Add(Text(c.Small, 44, fg, FontWeight.Normal, 0.75));
                root.Children.Add(stack);
                var tag = Text("savefile", 40, fg, FontWeight.SemiBold, 0.8);
                tag.Margin = new Thickness(0, 0, 120, 90);
                tag.HorizontalAlignment = HorizontalAlignment.Right;
                tag.VerticalAlignment = VerticalAlignment.Bottom;
                root.Children.Add(tag);
                break;
            }
            default:
            {
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(120, 0, 120, 0), Spacing = 22 };
                stack.Children.Add(Text(c.Kicker, 58, fg, FontWeight.SemiBold, 0.85));
                stack.Children.Add(Fit(Text(c.Big, c.Kind == "squad" ? 150 : 200, big, FontWeight.Bold), W - 240));
                stack.Children.Add(Text(c.Small, 50, small, FontWeight.Normal, 0.9));
                root.Children.Add(stack);
                root.Children.Add(brand);
                break;
            }
        }
        return root;
    }

    private static Control Blob(IBrush body, double w, double h, HorizontalAlignment align, Thickness margin) => new BlobMascot
    {
        Width = w, Height = h, Body = body, Still = true,
        Shadow = new SolidColorBrush(Colors.Black, 0.35),
        HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Bottom, Margin = margin,
    };
}
