using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cnema.ViewModels;

/// <summary>The Backup panel: where to back up to, what, and how it's going.</summary>
public sealed partial class BackupViewModel : ObservableObject
{
    /// <summary>SFTP (a folder on an SSH server) or S3.</summary>
    public ChipGroup Kind { get; }
    public bool IsSftp => (string)Kind.Value == "sftp";
    public bool IsS3 => !IsSftp;

    // SFTP
    [ObservableProperty] private string _host;
    [ObservableProperty] private string _port;
    [ObservableProperty] private string _user;
    [ObservableProperty] private string _remoteFolder;
    [ObservableProperty] private string _keyFile;
    /// <summary>The password, or the SSH key's passphrase (both live in the keychain).</summary>
    [ObservableProperty] private string _sftpSecret;
    public ChipGroup SignIn { get; }
    public bool UseKey => (int)SignIn.Value == 0;
    public bool UsePassword => !UseKey;
    /// <summary>The trusted server key ("SHA256:…"); empty until the first connection.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasHostKey), nameof(HostKeyText))] private string _hostKey;
    public bool HasHostKey => HostKey.Length > 0;
    public string HostKeyText => F("Server key: {0}", HostKey);
    /// <summary>The key Savefile would use when none is picked, shown as a hint.</summary>
    public string KeyFileHint { get; }

    // S3
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

    public BackupViewModel(BackupSettings s, string? secret, string? sftpSecret)
    {
        Kind = new ChipGroup(s.IsSftp ? "sftp" : "s3", ("SFTP (SSH)", "sftp"), ("S3", "s3"));
        Kind.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(IsSftp)); OnPropertyChanged(nameof(IsS3)); };
        SignIn = new ChipGroup(s.UseKey ? 0 : 1, ("SSH key", 0), ("Password", 1));
        SignIn.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(UseKey)); OnPropertyChanged(nameof(UsePassword)); };
        _host = s.Host;
        _port = s.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _user = s.User.Length > 0 ? s.User : Environment.UserName;
        _remoteFolder = s.RemoteFolder;
        _keyFile = s.KeyFile;
        _sftpSecret = sftpSecret ?? "";
        _hostKey = s.HostKey;
        KeyFileHint = BackupService.KeyFileFor(new BackupSettings()) ?? "~/.ssh/id_ed25519";
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
        s.Kind = (string)Kind.Value;
        var host = Host.Trim();
        // "user@server" typed into the server box: take it apart.
        if (host.IndexOf('@') is var at and > 0 && User.Trim().Length == 0) { User = host[..at]; host = host[(at + 1)..]; }
        // A different server (or port) means a different server key.
        if (!string.Equals(host, s.Host, StringComparison.OrdinalIgnoreCase) || ParsePort() != s.Port) HostKey = "";
        s.Host = host;
        s.Port = ParsePort();
        s.User = User.Trim();
        s.RemoteFolder = RemoteFolder.Trim();
        s.UseKey = UseKey;
        s.KeyFile = KeyFile.Trim();
        s.HostKey = HostKey;
        s.Endpoint = Endpoint.Trim().TrimEnd('/');
        s.Region = Region.Trim();
        s.Bucket = Bucket.Trim();
        s.Prefix = Prefix.Trim().Trim('/');
        s.AccessKey = AccessKey.Trim();
        s.Automatic = Automatic;
        s.Everything = (int)What.Value == 1;
    }

    private int ParsePort() => int.TryParse(Port.Trim(), out var p) && p is > 0 and < 65536 ? p : 22;

    /// <summary>The secret that goes with the chosen kind of server.</summary>
    public string CurrentSecret => IsSftp ? SftpSecret : SecretKey;

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
