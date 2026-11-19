using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Cnema.Views;

/// <summary>Part of the main window, built the first time it's shown (see <see cref="Controls.LazyView"/>).</summary>
public partial class MergePanel : UserControl
{
    public MergePanel() => AvaloniaXamlLoader.Load(this);
}
