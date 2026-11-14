using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cnema.ViewModels;

/// <summary>The pages of the editor's tool panel, picked from the rail beside it.</summary>
public enum ToolPage { Trim, Captions, Speed, Sound, Brand, Share, Zoom, Blur }

// The editor's tool panel: a rail of labelled tools beside the video, and the page of the one picked.
// Clicking the picked tool again folds the panel away for a bigger picture. Both are remembered.
public sealed partial class PlayerViewModel
{
    [ObservableProperty] private ToolPage _page = ToolPage.Trim;
    [ObservableProperty] private bool _isToolsOpen = true;

    public bool IsTrimPage => IsToolsOpen && Page == ToolPage.Trim;
    public bool IsCaptionsPage => IsToolsOpen && Page == ToolPage.Captions;
    public bool IsSpeedPage => IsToolsOpen && Page == ToolPage.Speed;
    public bool IsSoundPage => IsToolsOpen && Page == ToolPage.Sound;
    public bool IsBrandPage => IsToolsOpen && Page == ToolPage.Brand;
    public bool IsSharePage => IsToolsOpen && Page == ToolPage.Share;
    public bool IsZoomPage => IsToolsOpen && Page == ToolPage.Zoom;
    public bool IsBlurPage => IsToolsOpen && Page == ToolPage.Blur;

    /// <summary>"Remove all captions and speed changes", under the pages it clears.</summary>
    public bool ShowClearEffects => HasClearableEffects && (IsCaptionsPage || IsSpeedPage || IsZoomPage || IsBlurPage);

    /// <summary>
    /// Controller mode's simple editor: trim, parts, markers and export only. The advanced tools are hidden
    /// (anything already made with them still exports), so every button can be reached with the D-pad.
    /// </summary>
    public bool IsSimpleEditor
    {
        get => _isSimpleEditor;
        set
        {
            if (_isSimpleEditor == value) return;
            _isSimpleEditor = value;
            foreach (var name in new[]
                     {
                         nameof(IsSimpleEditor), nameof(IsFullEditor), nameof(ShowCaptionsTool), nameof(ShowSpeedTool),
                         nameof(ShowZoomTool), nameof(ShowBlurTool), nameof(ShowSoundTool), nameof(ShowBrandTool),
                     })
                OnPropertyChanged(name);
            RefreshToolPages();
        }
    }
    private bool _isSimpleEditor;
    public bool IsFullEditor => !IsSimpleEditor;
    public bool ShowCaptionsTool => ShowCaptionTools && !IsSimpleEditor;
    public bool ShowSpeedTool => ShowSpeedTools && !IsSimpleEditor;
    public bool ShowZoomTool => ShowZoomTools && !IsSimpleEditor;
    public bool ShowBlurTool => ShowBlurTools && !IsSimpleEditor;
    public bool ShowSoundTool => ShowSoundPage && !IsSimpleEditor;
    public bool ShowBrandTool => ShowBranding && !IsSimpleEditor;

    /// <summary>The Sound page also holds the audio track switches, so it shows for either.</summary>
    public bool ShowSoundPage => ShowAudioTools || HasAudioTracks || ShowMusicTools;

    public string PageTitle => Page switch
    {
        ToolPage.Captions => T("Captions"),
        ToolPage.Speed => T("Slow motion and fast forward"),
        ToolPage.Sound => T("Sound"),
        ToolPage.Brand => T("Watermark, intro and outro"),
        ToolPage.Share => T("Export and share"),
        ToolPage.Zoom => T("Zoom and pan"),
        ToolPage.Blur => T("Blur"),
        _ => T("Trim"),
    };

    public string MarkerText => Markers.Count switch
    {
        0 => T("No markers"),
        1 => T("1 marker"),
        var n => F("{0} markers", n),
    };

    private void InitTools()
    {
        IsToolsOpen = Fx.ToolsOpen;
        Page = Enum.TryParse<ToolPage>(Fx.ToolPage, out var p) ? p : ToolPage.Trim;
        if (!IsAvailable(Page)) { _page = ToolPage.Trim; ToolsChangedQuiet(); }
    }

    private bool IsAvailable(ToolPage page) => page switch
    {
        ToolPage.Trim or ToolPage.Share => true,
        _ when IsSimpleEditor => false,
        ToolPage.Captions => ShowCaptionTools,
        ToolPage.Speed => ShowSpeedTools,
        ToolPage.Sound => ShowSoundPage,
        ToolPage.Brand => ShowBranding,
        ToolPage.Zoom => ShowZoomTools,
        ToolPage.Blur => ShowBlurTools,
        _ => true,
    };

    /// <summary>After features or the audio tracks changed: a page that went away falls back to Trim.</summary>
    private void RefreshToolPages()
    {
        foreach (var name in new[]
                 {
                     nameof(ShowSoundPage), nameof(ShowCaptionsTool), nameof(ShowSpeedTool), nameof(ShowZoomTool),
                     nameof(ShowBlurTool), nameof(ShowSoundTool), nameof(ShowBrandTool),
                 })
            OnPropertyChanged(name);
        if (!IsAvailable(Page)) _page = ToolPage.Trim; // not saved: the full editor keeps its page
        ToolsChangedQuiet();
    }

    partial void OnPageChanged(ToolPage value) => ToolsChanged();
    partial void OnIsToolsOpenChanged(bool value) => ToolsChanged();

    private void ToolsChangedQuiet()
    {
        foreach (var name in new[]
                 {
                     nameof(Page), nameof(IsTrimPage), nameof(IsCaptionsPage), nameof(IsSpeedPage), nameof(IsSoundPage),
                     nameof(IsBrandPage), nameof(IsSharePage), nameof(PageTitle), nameof(ShowClearEffects),
                     nameof(IsZoomPage), nameof(IsBlurPage),
                 })
            OnPropertyChanged(name);
        UpdateFramePreview();
    }

    private void ToolsChanged()
    {
        foreach (var name in new[]
                 {
                     nameof(IsTrimPage), nameof(IsCaptionsPage), nameof(IsSpeedPage), nameof(IsSoundPage),
                     nameof(IsBrandPage), nameof(IsSharePage), nameof(PageTitle), nameof(ShowClearEffects),
                     nameof(IsZoomPage), nameof(IsBlurPage),
                 })
            OnPropertyChanged(name);
        UpdateFramePreview();
        if (Fx.ToolsOpen == IsToolsOpen && Fx.ToolPage == Page.ToString()) return;
        Fx.ToolsOpen = IsToolsOpen;
        Fx.ToolPage = Page.ToString();
        _settings.Save();
    }

    /// <summary>Opens a page; the page already open folds the panel away.</summary>
    [RelayCommand]
    private void PickPage(ToolPage page)
    {
        if (IsToolsOpen && Page == page) { IsToolsOpen = false; return; }
        ShowPage(page);
    }

    private void ShowPage(ToolPage page)
    {
        if (!IsAvailable(page)) return;
        Page = page;
        IsToolsOpen = true;
    }

    /// <summary>Shift+E: fold the tool panel away, or bring it back.</summary>
    [RelayCommand]
    private void ToggleTools() => IsToolsOpen = !IsToolsOpen;
}
