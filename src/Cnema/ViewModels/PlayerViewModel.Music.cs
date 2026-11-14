using Cnema.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

// Sound page: a music track under the export, lowered while the clip itself is loud.
public sealed partial class PlayerViewModel
{
    /// <summary>Set by the view: an open dialog for an audio file.</summary>
    public Func<Task<string?>>? PickAudio { get; set; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMusic), nameof(MusicName))] private string? _musicPath;
    public ChipGroup MusicVolume { get; private set; } = null!;
    public bool ShowMusicTools => Features.Music;
    public bool HasMusic => !string.IsNullOrEmpty(MusicPath);
    public string MusicName => HasMusic ? Path.GetFileName(MusicPath!) : T("No song picked");

    public bool MusicDuck
    {
        get => Fx.MusicDuck;
        set { if (Fx.MusicDuck == value) return; Fx.MusicDuck = value; _settings.Save(); OnPropertyChanged(); EffectsChanged(); }
    }

    private void InitMusic()
    {
        MusicVolume = MusicVolumeChips(Fx.MusicVolumeDb);
        MusicVolume.PropertyChanged += (_, _) =>
        {
            Fx.MusicVolumeDb = (double)MusicVolume.Value;
            _settings.Save();
            EffectsChanged();
        };
    }

    public static ChipGroup MusicVolumeChips(double db) => new(
        new[] { -20.0, -12.0, -6.0 }.OrderBy(o => Math.Abs(o - db)).First(),
        ("Quiet", -20.0, "Well under the game"), ("Medium", -12.0, "Under the game"), ("Loud", -6.0, "About as loud as the game"));

    partial void OnMusicPathChanged(string? value) => EffectsChanged();

    private MusicSpec? BuildMusic() => ShowMusicTools && HasMusic && File.Exists(MusicPath)
        ? new MusicSpec(MusicPath!, (double)MusicVolume.Value, MusicDuck)
        : null;

    [RelayCommand]
    private async Task PickMusicAsync()
    {
        if (PickAudio is null || await PickAudio() is not { Length: > 0 } path) return;
        MusicPath = path;
        ShowPage(ToolPage.Sound);
    }

    [RelayCommand] private void RemoveMusic() => MusicPath = null;
}
