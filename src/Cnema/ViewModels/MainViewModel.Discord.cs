using Cnema.Services;

namespace Cnema.ViewModels;

// Discord status (Settings, General): "Playing Cnema · Editing a clip" on the user's profile while
// the window is open, or while a session records. Off unless switched on.
public sealed partial class MainViewModel
{
    private DiscordPresence? _discord;
    private string? _presenceKey;
    private DateTimeOffset _presenceSince;
    private bool _windowShown = true;

    /// <summary>Set by the window: false while it is hidden in the tray or menu bar.</summary>
    public bool WindowShown
    {
        get => _windowShown;
        set
        {
            if (_windowShown == value) return;
            _windowShown = value;
            UpdatePresence();
        }
    }

    private void InitPresence()
    {
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IsPlayerOpen) or nameof(IsMiniPlayerOpen) or nameof(IsSession)) UpdatePresence();
        };
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerViewModel.Title)) UpdatePresence();
        };
        UpdatePresence();
    }

    private void UpdatePresence()
    {
        if (!DiscordPresence.IsAvailable) return;
        var s = _settings.Current;
        if (!s.DiscordStatus)
        {
            // Closing the connection is what makes Discord drop the status.
            _discord?.Dispose();
            _discord = null;
            _presenceKey = null;
            return;
        }
        _discord ??= new DiscordPresence();

        var name = s.DiscordShowNames && Player.Title.Length > 0 ? Player.Title : null;
        string? details = null, state = null;
        if (IsSession) details = "Recording a session";
        else if (!WindowShown) details = null;
        else if (IsPlayerOpen) (details, state) = ("Editing a clip", name);
        else if (IsMiniPlayerOpen) (details, state) = ("Watching clips", name);
        else details = "Browsing clips";

        var key = details + "|" + state;
        if (key != _presenceKey)
        {
            _presenceKey = key;
            _presenceSince = IsSession ? DateTimeOffset.UtcNow - Clips.SessionElapsed : DateTimeOffset.UtcNow;
        }
        _discord.Set(details, state, _presenceSince);
    }
}
