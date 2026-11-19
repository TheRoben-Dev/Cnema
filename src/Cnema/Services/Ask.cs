namespace Cnema.Services;

/// <summary>
/// Asks before something that can't be taken back (removing a folder, deleting for good, resetting). The main
/// window answers it with a dialog; with no window to ask (tests, tools), the answer is no.
/// </summary>
public static class Ask
{
    /// <summary>Shows the question: a title, what will happen, and the label of the button that does it.</summary>
    public static Func<string, string, string, Task<bool>>? Handler { get; set; }

    public static Task<bool> SureAsync(string title, string message, string yes) =>
        Handler?.Invoke(title, message, yes) ?? Task.FromResult(false);
}
