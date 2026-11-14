using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cnema.Services;

namespace Cnema.Views;

// Controller navigation by position: the D-pad moves to the nearest control in that direction, the way
// a TV menu does, instead of walking the tab order (which jumps around in rows of buttons or two-column
// layouts). Only controls inside the current scope (a dialog, the player, the library) are candidates.
public partial class MainWindow
{
    /// <summary>The focusable controls inside <paramref name="scope"/> that the pad may land on.</summary>
    private static IEnumerable<Control> Stops(Control scope) =>
        scope.GetVisualDescendants().OfType<Control>().Where(IsStop);

    private static bool IsStop(Control c) =>
        c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled && KeyboardNavigation.GetIsTabStop(c) &&
        c is not ScrollViewer && (c is not ItemsControl || c is ComboBox);

    private Rect? BoundsOf(Control c)
    {
        var m = c.TransformToVisual(this);
        return m is { } t && c.Bounds.Width > 0 && c.Bounds.Height > 0 ? new Rect(c.Bounds.Size).TransformToAABB(t) : null;
    }

    /// <summary>
    /// The nearest stop from <paramref name="from"/> in direction <paramref name="b"/>. Controls that line up
    /// with it (overlap across the direction of travel) win over ones that are merely closer diagonally.
    /// </summary>
    private Control? Nearest(Control scope, Control from, PadButton b, Func<Control, bool>? filter = null)
    {
        // Inside a scrolling list, keep going through the list (scrolling as needed) before leaving it,
        // so Down reaches the next setting rather than the dialog's buttons under the visible part.
        if (from.FindAncestorOfType<ScrollViewer>() is { } sv && scope.IsVisualAncestorOf(sv) &&
            NearestIn(scope, from, b, c => sv.IsVisualAncestorOf(c) && (filter?.Invoke(c) ?? true)) is { } inList)
            return inList;
        return NearestIn(scope, from, b, filter);
    }

    private Control? NearestIn(Control scope, Control from, PadButton b, Func<Control, bool>? filter)
    {
        if (BoundsOf(from) is not { } a) return null;
        Control? best = null;
        var bestScore = double.MaxValue;
        foreach (var c in Stops(scope))
        {
            if (ReferenceEquals(c, from) || from.IsVisualAncestorOf(c) || c.IsVisualAncestorOf(from)) continue;
            if (filter is not null && !filter(c)) continue;
            if (BoundsOf(c) is not { } r) continue;
            double gap, cross;
            const double slack = 4;
            switch (b)
            {
                case PadButton.Down:
                    if (r.Center.Y <= a.Center.Y + 1 || r.Top < a.Bottom - Math.Min(slack + a.Height / 2, r.Height / 2)) continue;
                    gap = Math.Max(0, r.Top - a.Bottom); cross = Apart(a.Left, a.Right, r.Left, r.Right);
                    break;
                case PadButton.Up:
                    if (r.Center.Y >= a.Center.Y - 1 || r.Bottom > a.Top + Math.Min(slack + a.Height / 2, r.Height / 2)) continue;
                    gap = Math.Max(0, a.Top - r.Bottom); cross = Apart(a.Left, a.Right, r.Left, r.Right);
                    break;
                case PadButton.Right:
                    if (r.Center.X <= a.Center.X + 1 || r.Left < a.Right - Math.Min(slack + a.Width / 2, r.Width / 2)) continue;
                    gap = Math.Max(0, r.Left - a.Right); cross = Apart(a.Top, a.Bottom, r.Top, r.Bottom);
                    break;
                default:
                    if (r.Center.X >= a.Center.X - 1 || r.Right > a.Left + Math.Min(slack + a.Width / 2, r.Width / 2)) continue;
                    gap = Math.Max(0, a.Left - r.Right); cross = Apart(a.Top, a.Bottom, r.Top, r.Bottom);
                    break;
            }
            // Lined up: distance only. Off to the side: a heavy penalty, so a row below beats a diagonal jump.
            var align = b is PadButton.Up or PadButton.Down ? Math.Abs(r.Left - a.Left) : Math.Abs(r.Center.Y - a.Center.Y);
            var score = gap + (cross > 0 ? 400 + cross * 2 : 0) + align * 0.05;
            if (score < bestScore) { bestScore = score; best = c; }
        }
        return best;
    }

    /// <summary>How far apart two ranges are (0 when they overlap).</summary>
    private static double Apart(double a0, double a1, double b0, double b1) =>
        a1 < b0 ? b0 - a1 : b1 < a0 ? a0 - b1 : 0;

    /// <summary>The first stop in the scope, top-left first: where the cursor starts in a dialog.</summary>
    private Control? FirstStop(Control scope) =>
        Stops(scope).Select(c => (c, r: BoundsOf(c))).Where(x => x.r is not null)
            .OrderBy(x => Math.Round(x.r!.Value.Top / 8)).ThenBy(x => x.r!.Value.Left).Select(x => x.c).FirstOrDefault();

    private static void FocusPad(Control c)
    {
        c.Focus(NavigationMethod.Directional);
        c.BringIntoView();
    }
}
