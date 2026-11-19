using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Cnema.Views;

/// <summary>Part of the main window, built the first time it's shown (see <see cref="Controls.LazyView"/>).</summary>
public partial class RenamePanel : UserControl
{
    public RenamePanel() => AvaloniaXamlLoader.Load(this);
}
