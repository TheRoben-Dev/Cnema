using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Cnema.Views;

/// <summary>Part of the main window, built the first time it's shown (see <see cref="Controls.LazyView"/>).</summary>
public partial class SettingsPanel : UserControl
{
    public SettingsPanel() => AvaloniaXamlLoader.Load(this);

    private void OnQuitClick(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.OnQuitClick(sender, e);
}
