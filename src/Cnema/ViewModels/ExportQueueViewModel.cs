using System.Collections.ObjectModel;
using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

public enum ExportState { Waiting, Running, Done, Failed, Cancelled }

/// <summary>One export in the queue.</summary>
public sealed partial class ExportJobViewModel : ObservableObject
{
    public string Title { get; }
    public string Detail { get; }
    public string Target { get; }
    internal Func<IProgress<double>, CancellationToken, Task> Work { get; }
    internal Func<string> DoneText { get; }
    internal CancellationTokenSource Cts { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsFinished), nameof(IsDone), nameof(IsFailed), nameof(CanCancel))]
    private ExportState _state;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "Waiting";

    public bool IsRunning => State == ExportState.Running;
    public bool IsFinished => State is ExportState.Done or ExportState.Failed or ExportState.Cancelled;
    public bool IsDone => State == ExportState.Done;
    public bool IsFailed => State == ExportState.Failed;
    public bool CanCancel => State is ExportState.Waiting or ExportState.Running;

    internal ExportJobViewModel(string target, string detail, Func<IProgress<double>, CancellationToken, Task> work, Func<string> doneText)
    {
        Target = target;
        Title = Path.GetFileName(target);
        Detail = detail;
        Work = work;
        DoneText = doneText;
    }
}

/// <summary>
/// Exports waiting their turn. They run one at a time in the background, so the editor stays free
/// while a long effects export or a two-pass Discord export runs.
/// </summary>
public sealed partial class ExportQueueViewModel : ObservableObject
{
    private bool _pumping;

    public ObservableCollection<ExportJobViewModel> Jobs { get; } = new();

    /// <summary>A job finished, failed or was cancelled.</summary>
    public event Action<ExportJobViewModel>? Finished;

    public bool HasJobs => Jobs.Count > 0;
    public int PendingCount => Jobs.Count(j => j.CanCancel);
    public bool IsBusy => PendingCount > 0;
    public bool HasFinished => Jobs.Any(j => j.IsFinished);

    public string ButtonText
    {
        get
        {
            var running = Jobs.FirstOrDefault(j => j.IsRunning);
            var waiting = Jobs.Count(j => j.State == ExportState.Waiting);
            if (running is not null)
                return waiting > 0 ? $"Exporting {running.Progress:0}% · {waiting} waiting" : $"Exporting {running.Progress:0}%";
            var failed = Jobs.Count(j => j.IsFailed);
            return failed > 0 ? $"Exports · {failed} failed" : "Exports";
        }
    }

    public bool IsTarget(string path) =>
        Jobs.Any(j => j.CanCancel && string.Equals(j.Target, path, StringComparison.OrdinalIgnoreCase));

    public ExportJobViewModel Add(string target, string detail, Func<IProgress<double>, CancellationToken, Task> work, Func<string> doneText)
    {
        var job = new ExportJobViewModel(target, detail, work, doneText);
        Jobs.Add(job);
        Raise();
        _ = PumpAsync();
        return job;
    }

    private async Task PumpAsync()
    {
        if (_pumping) return;
        _pumping = true;
        try
        {
            while (Jobs.FirstOrDefault(j => j.State == ExportState.Waiting) is { } job)
            {
                job.State = ExportState.Running;
                job.Status = "0%";
                Raise();
                var progress = new Progress<double>(p =>
                {
                    if (!job.IsRunning) return;
                    job.Progress = p * 100;
                    job.Status = $"{p * 100:0}%";
                    OnPropertyChanged(nameof(ButtonText));
                });
                try
                {
                    await job.Work(progress, job.Cts.Token);
                    job.State = ExportState.Done;
                    job.Progress = 100;
                    job.Status = job.DoneText();
                }
                catch (OperationCanceledException)
                {
                    job.State = ExportState.Cancelled;
                    job.Status = "Cancelled";
                }
                catch (Exception ex)
                {
                    Log.Warn($"Export of {job.Target} failed: {ex.Message}");
                    job.State = ExportState.Failed;
                    job.Status = "Failed: " + PlayerViewModel.FirstLine(ex.Message);
                }
                Raise();
                Finished?.Invoke(job);
            }
        }
        finally
        {
            _pumping = false;
        }
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(HasJobs));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(HasFinished));
        OnPropertyChanged(nameof(ButtonText));
    }

    [RelayCommand]
    private void Cancel(ExportJobViewModel? job)
    {
        if (job is null || !job.CanCancel) return;
        if (job.State == ExportState.Waiting)
        {
            job.State = ExportState.Cancelled;
            job.Status = "Cancelled";
            Raise();
            return;
        }
        job.Cts.Cancel();
    }

    [RelayCommand]
    private void Reveal(ExportJobViewModel? job)
    {
        if (job is { IsDone: true } && File.Exists(job.Target)) PlatformShell.Reveal(job.Target);
    }

    [RelayCommand]
    private void Remove(ExportJobViewModel? job)
    {
        if (job is { IsFinished: true } && Jobs.Remove(job)) Raise();
    }

    [RelayCommand]
    private void ClearFinished()
    {
        foreach (var job in Jobs.Where(j => j.IsFinished).ToList()) Jobs.Remove(job);
        Raise();
    }

    /// <summary>On quit: stop the running export and drop the waiting ones.</summary>
    public void CancelAll()
    {
        foreach (var job in Jobs.Where(j => j.CanCancel)) job.Cts.Cancel();
    }
}
