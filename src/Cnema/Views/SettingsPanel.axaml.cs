using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Cnema.Services;
using Cnema.ViewModels;

namespace Cnema.Views;

/// <summary>
/// Part of the main window, built the first time it's shown (see <see cref="Controls.LazyView"/>). Also keeps
/// the lines between rows tidy, and filters the rows to the ones that match the search box.
/// </summary>
public partial class SettingsPanel : UserControl
{
    private readonly List<IDisposable> _hidden = new();
    private SettingsViewModel? _vm;
    private StackPanel? _pages;

    public SettingsPanel()
    {
        AvaloniaXamlLoader.Load(this);
        _pages = this.FindControl<ScrollViewer>("SettingsScroll")?.Content as StackPanel;
        foreach (var group in Groups()) WatchGroup(group);
        if (Content is Control frame) frame.DataContextChanged += (_, _) => Attach(frame.DataContext as SettingsViewModel);
    }

    private void OnQuitClick(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.OnQuitClick(sender, e);

    private void Attach(SettingsViewModel? vm)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = vm;
        if (vm is not null) vm.PropertyChanged += OnVmChanged;
        Filter();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        // After the pages' own bindings have caught up with the search.
        if (e.PropertyName == nameof(SettingsViewModel.Search)) Dispatcher.UIThread.Post(Filter);
    }

    // ---------- the lines between rows ----------

    /// <summary>Every group of rows: a rounded box holding a stack of rows.</summary>
    private IEnumerable<StackPanel> Groups() =>
        _pages?.GetLogicalDescendants().OfType<Border>().Where(b => b.Classes.Contains("group")).Select(b => b.Child).OfType<StackPanel>()
        ?? Enumerable.Empty<StackPanel>();

    private static bool IsSep(Control c) => c is Border b && b.Classes.Contains("sep");

    /// <summary>A line shows only between two rows that show, so a row that's hidden never leaves two lines or a stray one.</summary>
    private static void WatchGroup(StackPanel group)
    {
        foreach (var child in group.Children.Where(c => !IsSep(c)))
            child.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty) Tidy(group); };
        Tidy(group);
    }

    private static void Tidy(StackPanel group)
    {
        Control? pending = null;
        var seen = false;
        foreach (var child in group.Children)
        {
            if (IsSep(child))
            {
                child.IsVisible = false;
                if (seen) pending ??= child;
            }
            else if (child.IsVisible)
            {
                if (pending is not null) pending.IsVisible = true;
                pending = null;
                seen = true;
            }
        }
    }

    // ---------- search ----------

    private void Filter()
    {
        foreach (var h in _hidden) h.Dispose();
        _hidden.Clear();
        var noMatch = this.FindControl<TextBlock>("SettingsNoMatch");
        var query = _vm?.Search.Trim() ?? "";
        if (noMatch is not null) noMatch.IsVisible = false;
        if (query.Length == 0 || _pages is null) return;
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var found = 0;
        foreach (var page in _pages.Children.OfType<StackPanel>())
        {
            var n = FilterContainer(page, words);
            if (n == 0) Hide(page);
            found += n;
        }
        if (noMatch is not null && found == 0)
        {
            noMatch.Text = F("No settings match “{0}”.", query);
            noMatch.IsVisible = true;
        }
        if (this.FindControl<ScrollViewer>("SettingsScroll") is { } scroll) scroll.Offset = default;
    }

    /// <summary>Hides what doesn't match inside a page (or a part of one); returns how many rows still show.</summary>
    private int FilterContainer(StackPanel container, string[] words)
    {
        var shown = 0;
        var kids = container.Children.ToList();
        for (var i = 0; i < kids.Count; i++)
        {
            var c = kids[i];
            if (!c.IsVisible) continue;
            if (c is TextBlock t && t.Classes.Contains("pagehead")) continue;
            if (c is TextBlock label && label.Classes.Contains("label"))
            {
                // A section: its title, then its box of rows. A title that matches keeps the whole box.
                var box = i + 1 < kids.Count ? kids[i + 1] as Border : null;
                if (box is null || !box.Classes.Contains("group")) { Hide(c); continue; }
                i++;
                if (!box.IsVisible) { Hide(c); continue; }
                int rows;
                if (Matches(label, words)) rows = 1;
                else if (box.Child is StackPanel list) rows = FilterRows(list, words);
                else rows = Matches(box, words) ? 1 : 0;
                if (rows == 0) { Hide(c); Hide(box); }
                shown += rows;
                continue;
            }
            if (c is Border { Child: StackPanel loose } g && g.Classes.Contains("group"))
            {
                var rows = FilterRows(loose, words);
                if (rows == 0) Hide(g);
                shown += rows;
                continue;
            }
            if (c is StackPanel part && !part.Classes.Contains("row"))
            {
                var rows = FilterContainer(part, words);
                if (rows == 0) Hide(part);
                shown += rows;
                continue;
            }
            // Notes and notices between sections only make sense on their own page.
            Hide(c);
        }
        return shown;
    }

    private int FilterRows(StackPanel list, string[] words)
    {
        var shown = 0;
        foreach (var row in list.Children)
        {
            if (IsSep(row) || !row.IsVisible) continue;
            if (Matches(row, words)) shown++;
            else Hide(row);
        }
        return shown;
    }

    private void Hide(Control c)
    {
        // Above any binding the control has, and undone exactly when the search changes.
        if (c.SetValue(IsVisibleProperty, false, BindingPriority.Animation) is { } undo) _hidden.Add(undo);
    }

    private static readonly CompareInfo Compare = CultureInfo.InvariantCulture.CompareInfo;

    /// <summary>Every word is somewhere in the row: its text as shown, or the English it's translated from.</summary>
    private static bool Matches(Control c, string[] words)
    {
        var texts = new List<string>();
        foreach (var o in c.GetLogicalDescendants().Prepend(c).OfType<AvaloniaObject>())
        {
            if (o is TextBlock { Text: { } tb }) texts.Add(tb);
            if (o is ContentControl { Content: string cs }) texts.Add(cs);
            if (Tr.GetText(o) is { } k1) texts.Add(k1);
            if (Tr.GetContent(o) is { } k2) texts.Add(k2);
            if (Tr.GetTip(o) is { } k3) { texts.Add(k3); texts.Add(T(k3)); }
            if (o is Control { Tag: string tag }) texts.Add(tag);
        }
        var all = string.Join(' ', texts);
        return words.All(w => Compare.IndexOf(all, w, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
    }
}
