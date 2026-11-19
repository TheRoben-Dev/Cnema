using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Cnema.Views;

/// <summary>The editor's tool pages (Trim, Captions, Speed…), built the first time the editor opens (see <see cref="Controls.LazyView"/>).</summary>
public partial class ToolsPanel : UserControl
{
    public ToolsPanel() => AvaloniaXamlLoader.Load(this);
}
