namespace Cinema.Services;

public sealed class AppSettings
{
    public List<string> Folders { get; set; } = new();
    public int SortIndex { get; set; }
    public bool PreciseExport { get; set; }
    public int Volume { get; set; } = 80;
}

public sealed class SettingsStore
{
    public AppSettings Current { get; } = JsonStore.Load<AppSettings>(AppPaths.SettingsFile);
    public void Save() => JsonStore.Save(AppPaths.SettingsFile, Current);
}
