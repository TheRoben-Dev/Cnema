using Avalonia.Input;

namespace Cnema.Services;

/// <summary>One thing a key can do. <see cref="Defaults"/> are Avalonia key gestures, e.g. "Shift+I".</summary>
public sealed record ShortcutAction(string Id, string Group, string Label, params string[] Defaults);

/// <summary>
/// The app's keyboard shortcuts and the user's changes to them. Each action has default keys; a
/// changed action has exactly one key (or none), stored in <see cref="AppSettings.Shortcuts"/>.
/// </summary>
public static class Shortcuts
{
    public const string Library = "Library";
    public const string Player = "Player";

    public static IReadOnlyList<ShortcutAction> All { get; } = new ShortcutAction[]
    {
        new("search", Library, "Search", "Ctrl+F", "OemQuestion"),
        new("settings", Library, "Open settings", "Ctrl+OemComma"),
        new("refresh", Library, "Rescan folders", "F5"),
        new("select", Library, "Select several videos", "S"),

        new("play", Player, "Play or pause", "Space", "K"),
        new("prevClip", Player, "Previous video", "Left"),
        new("nextClip", Player, "Next video", "Right"),
        new("back5", Player, "Back 5 seconds", "Shift+Left"),
        new("fwd5", Player, "Forward 5 seconds", "Shift+Right"),
        new("back1", Player, "Back 1 second", "Alt+Left"),
        new("fwd1", Player, "Forward 1 second", "Alt+Right"),
        new("back10", Player, "Back 10 seconds", "J"),
        new("fwd10", Player, "Forward 10 seconds", "Shift+L"),
        new("frameBack", Player, "Previous frame", "OemComma"),
        new("frameFwd", Player, "Next frame", "OemPeriod"),
        new("start", Player, "Go to the start", "Home"),
        new("volUp", Player, "Volume up", "Up"),
        new("volDown", Player, "Volume down", "Down"),
        new("mute", Player, "Mute", "M"),
        new("fullscreen", Player, "Full screen", "F"),
        new("setIn", Player, "Mark the start of a selection", "I"),
        new("setOut", Player, "Mark the end of a selection", "O"),
        new("jumpIn", Player, "Jump to the selection start", "Shift+I"),
        new("jumpOut", Player, "Jump to the selection end", "Shift+O"),
        new("clear", Player, "Clear the selection", "X"),
        new("loop", Player, "Loop the selection", "L"),
        new("preview", Player, "Play the selection", "P"),
        new("addPart", Player, "Add the selection as a part", "A"),
        new("nextMarker", Player, "Next marker", "N"),
        new("prevMarker", Player, "Previous marker", "Shift+N"),
        new("speedDown", Player, "Slower", "Shift+OemComma"),
        new("speedUp", Player, "Faster", "Shift+OemPeriod"),
        new("abLoop", Player, "A-B loop: start, end, stop", "B"),
        new("prevChapter", Player, "Previous chapter", "Ctrl+Left"),
        new("nextChapter", Player, "Next chapter", "Ctrl+Right"),
        new("export", Player, "Export", "E"),
        new("effects", Player, "Show or hide the tool panel", "Shift+E"),
        new("caption", Player, "Add a caption at the playhead", "C"),
        new("thumb", Player, "Use this frame as the thumbnail", "T"),
        new("grab", Player, "Save this frame as an image", "G"),
    };

    /// <summary>Keys that always work and can't be changed, shown in the shortcut sheet for reference.</summary>
    public static IReadOnlyList<(string Group, string Label, string Keys)> Fixed { get; } = new[]
    {
        ("General", "Show this list", "?"),
        ("General", "Close the window", Mod + "+W"),
        ("General", "Quit Cnema", Mod + "+Q"),
        ("General", "Close a dialog or the player", "Esc"),
        ("General", "Select all (while selecting)", Mod + "+A"),
        ("General", "Quick player: play/pause, previous / next video, seek, mute, close", "Space ← → Shift+← → M Esc"),
    };

    private static string Mod => OperatingSystem.IsMacOS() ? "⌘" : "Ctrl";

    private static Dictionary<string, string> _custom = new();

    public static void Load(Dictionary<string, string> custom) => _custom = custom;

    public static IReadOnlyList<KeyGesture> GesturesFor(string id)
    {
        var action = All.First(a => a.Id == id);
        var texts = _custom.TryGetValue(id, out var c) ? (c.Length == 0 ? Array.Empty<string>() : new[] { c }) : action.Defaults;
        var list = new List<KeyGesture>();
        foreach (var t in texts)
        {
            try { list.Add(KeyGesture.Parse(t)); } catch { /* an unreadable custom key is ignored */ }
        }
        return list;
    }

    public static bool IsCustom(string id) => _custom.ContainsKey(id);

    /// <summary>The action in <paramref name="group"/> that this key press triggers, if any.</summary>
    public static string? Match(KeyEventArgs e, string group)
    {
        foreach (var a in All)
        {
            if (a.Group != group) continue;
            foreach (var g in GesturesFor(a.Id))
                if (Matches(g, e)) return a.Id;
        }
        return null;
    }

    // Ctrl in a default means Cmd on a Mac; either is accepted so a Windows keyboard on a Mac works too.
    private static bool Matches(KeyGesture g, KeyEventArgs e)
    {
        if (g.Key != e.Key) return false;
        var want = g.KeyModifiers;
        var have = e.KeyModifiers;
        if (want.HasFlag(KeyModifiers.Control) && have.HasFlag(KeyModifiers.Meta))
            have = (have & ~KeyModifiers.Meta) | KeyModifiers.Control;
        return want == have;
    }

    /// <summary>
    /// Gives <paramref name="id"/> this key. Another action in the same group that had it loses it;
    /// its label is returned so the user can be told.
    /// </summary>
    public static string? Assign(string id, KeyGesture gesture)
    {
        var action = All.First(a => a.Id == id);
        string? bumped = null;
        foreach (var other in All)
        {
            if (other.Id == id || other.Group != action.Group) continue;
            var keys = GesturesFor(other.Id);
            if (!keys.Any(k => k.Equals(gesture))) continue;
            var left = keys.Where(k => !k.Equals(gesture)).ToList();
            _custom[other.Id] = left.Count > 0 ? left[0].ToString() : "";
            bumped = other.Label;
        }
        _custom[id] = gesture.ToString();
        return bumped;
    }

    public static void Unbind(string id) => _custom[id] = "";
    public static void Reset(string id) => _custom.Remove(id);
    public static void ResetAll() => _custom.Clear();

    public static string Display(string id)
    {
        var keys = GesturesFor(id);
        return keys.Count == 0 ? "None" : string.Join("  or  ", keys.Select(Display));
    }

    public static string Display(KeyGesture g)
    {
        var parts = new List<string>();
        if (g.KeyModifiers.HasFlag(KeyModifiers.Control)) parts.Add(Mod);
        if (g.KeyModifiers.HasFlag(KeyModifiers.Meta)) parts.Add(OperatingSystem.IsMacOS() ? "⌘" : "Win");
        if (g.KeyModifiers.HasFlag(KeyModifiers.Alt)) parts.Add(OperatingSystem.IsMacOS() ? "⌥" : "Alt");
        if (g.KeyModifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(KeyName(g.Key));
        return string.Join("+", parts);
    }

    public static string KeyName(Key key) => key switch
    {
        Key.Left => "←", Key.Right => "→", Key.Up => "↑", Key.Down => "↓",
        Key.OemComma => ",", Key.OemPeriod => ".", Key.OemQuestion => "/", Key.OemPlus => "+", Key.OemMinus => "-",
        Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]", Key.OemSemicolon => ";", Key.OemQuotes => "'",
        Key.OemPipe => "\\", Key.OemTilde => "`", Key.Escape => "Esc", Key.Return => "Enter", Key.Back => "Backspace",
        Key.PageUp => "Page Up", Key.PageDown => "Page Down",
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        _ => key.ToString(),
    };

    /// <summary>Keys that can't be bound on their own because they only modify other keys.</summary>
    public static bool IsModifierOnly(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin or Key.CapsLock or Key.NumLock or Key.None;
}
