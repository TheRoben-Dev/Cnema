using Avalonia;
using Avalonia.Controls;

namespace Cnema.Controls;

/// <summary>
/// Holds a panel that is built the first time it's shown, not when the window opens. The main window carries
/// Settings and a dozen dialogs that most starts never open; building them all up front was most of the time
/// the window took to appear. Layout only measures what can be seen, so the first measure is the moment the
/// panel (or the dialog around it) became visible. Once built, it stays.
/// </summary>
public sealed class LazyView : Decorator
{
    /// <summary>The panel to build: a control with a public constructor taking nothing.</summary>
    public Type? Of { get; set; }

    /// <summary>The panel, building it now if it hasn't been yet.</summary>
    public Control? Realize()
    {
        if (Child is null && Of is not null)
        {
            try
            {
                Child = (Control)Activator.CreateInstance(Of)!;
            }
            catch (Exception ex)
            {
                Services.Log.Error($"Could not build {Of.Name}", ex);
                Of = null; // say it once, not at every layout
            }
        }
        return Child;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Realize();
        return base.MeasureOverride(availableSize);
    }
}
