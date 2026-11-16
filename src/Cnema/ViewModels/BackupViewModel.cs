using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cnema.ViewModels;

/// <summary>The Backup panel: where to back up to, what, and how it's going.</summary>
public sealed partial class BackupViewModel : ObservableObject
{
    [ObservableProperty] private string _endpoint;
    [ObservableProperty] private string _region;
    [ObservableProperty] private string _bucket;
    [ObservableProperty] private string _prefix;
    [ObservableProperty] private string _accessKey;
    [ObservableProperty] private string _secretKey;
    [ObservableProperty] private bool _automatic;
    public ChipGroup What { get; }

    /// <summary>A line under the connection fields: "Connected", or what went wrong.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStatus))] private string _status = "";
    [ObservableProperty] private bool _statusIsError;
    public bool HasStatus => Status.Length > 0;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isBusy;
    public bool IsIdle => !IsBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _summary = "";

    /// <summary>Set after "Find videos to restore": how many are on the server but not here.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasRestorePlan))] private BackupService.RestorePlan? _restorePlan;
    public bool HasRestorePlan => RestorePlan is { Missing.Count: > 0 };
    public string RestoreText => RestorePlan is { } p
        ? F("{0} on the server aren't on this computer ({1}).", P(p.Missing.Count, "{0} video", "{0:N0} videos"), SizeText(p.Bytes))
        : "";
    partial void OnRestorePlanChanged(BackupService.RestorePlan? value) => OnPropertyChanged(nameof(RestoreText));

    public BackupViewModel(BackupSettings s, string? secret)
    {
        _endpoint = s.Endpoint;
        _region = s.Region;
        _bucket = s.Bucket;
        _prefix = s.Prefix;
        _accessKey = s.AccessKey;
        _secretKey = secret ?? "";
        _automatic = s.Automatic;
        What = new ChipGroup(s.Everything ? 1 : 0, ("Favourites", 0), ("Every video", 1));
    }

    /// <summary>The fields as settings (the secret key stays separate).</summary>
    public void WriteTo(BackupSettings s)
    {
        s.Endpoint = Endpoint.Trim().TrimEnd('/');
        s.Region = Region.Trim();
        s.Bucket = Bucket.Trim();
        s.Prefix = Prefix.Trim().Trim('/');
        s.AccessKey = AccessKey.Trim();
        s.Automatic = Automatic;
        s.Everything = (int)What.Value == 1;
    }

    public void Report(BackupProgress p)
    {
        Progress = p.TotalBytes > 0 ? (double)p.Bytes / p.TotalBytes : p.Total > 0 ? (double)p.Done / p.Total : 0;
        ProgressText = p.Total == 0 ? T("Checking…")
            : F("{0} of {1} · {2} of {3}", p.Done + (p.Current.Length > 0 ? 1 : 0), p.Total, SizeText(p.Bytes), SizeText(p.TotalBytes));
    }

    public void ShowStatus(string text, bool error)
    {
        Status = text;
        StatusIsError = error;
    }

    public static string SizeText(long bytes) => bytes switch
    {
        >= 1L << 30 => F("{0:0.0} GB", bytes / (double)(1L << 30)),
        >= 1L << 20 => F("{0:0} MB", bytes / (double)(1L << 20)),
        _ => F("{0:0} KB", bytes / 1024.0),
    };
}
