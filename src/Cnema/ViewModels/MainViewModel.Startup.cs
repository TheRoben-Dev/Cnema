using Cnema.Services;

namespace Cnema.ViewModels;

// What the loading screen waits for after the library opens: the local models that are switched on.
public sealed partial class MainViewModel
{
    /// <summary>The models to wake, each with what the loading screen says meanwhile. None when none are on.</summary>
    public IReadOnlyList<(string Status, Func<Task> Work)> WarmUpSteps()
    {
        var steps = new List<(string, Func<Task>)>();
        if (Features.Speech && ModelStore.IsInstalled(ModelStore.Speech))
            steps.Add((T("Waking up the speech model…"), () => Task.Run(() => SpeechService.Warm(ModelStore.PathOf(ModelStore.Speech)))));
        // Kept loaded afterwards: the first visual search then answers at once.
        if (Features.Visual && VisualModelReady)
            steps.Add((T("Waking up the visual model…"), () => Task.Run(VisualModel)));
        return steps;
    }
}
