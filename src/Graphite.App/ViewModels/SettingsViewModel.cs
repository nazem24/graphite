using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Graphite.App.Interop;
using Graphite.App.Services;

namespace Graphite.App.ViewModels;

/// <summary>The settings page, in five sections picked on its left: Appearance (mode, material, motion),
/// Start page (layout, sidebar), Reading &amp; markup (progress line, tool defaults), Canvas, and About.
/// Everything applies the moment it is chosen (no Apply button); <see cref="ThemeService"/> and
/// <see cref="CanvasSettings"/> do the work and remember it, this class only mirrors the current state.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly Action _close;
    private readonly Action _checkForUpdates;

    public SettingsViewModel(Action close, string version, Action checkForUpdates)
    {
        _close = close;
        _checkForUpdates = checkForUpdates;
        VersionText = $"Version {version}";
        foreach (var style in ToolStyles.All) ToolRows.Add(new ToolDefaultRow(style));
        ThemeService.AppearanceChanged += Refresh;
    }

    // ---- sections ----
    public static readonly string[] Sections = { "Appearance", "StartPage", "Reading", "Canvas", "About" };

    /// <summary>The section on show.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAppearanceSection))]
    [NotifyPropertyChangedFor(nameof(IsStartPageSection))]
    [NotifyPropertyChangedFor(nameof(IsReadingSection))]
    [NotifyPropertyChangedFor(nameof(IsCanvasSection))]
    [NotifyPropertyChangedFor(nameof(IsAboutSection))]
    private string section = "Appearance";

    public bool IsAppearanceSection => Section == "Appearance";
    public bool IsStartPageSection => Section == "StartPage";
    public bool IsReadingSection => Section == "Reading";
    public bool IsCanvasSection => Section == "Canvas";
    public bool IsAboutSection => Section == "About";

    partial void OnSectionChanged(string value)
    {
        if (value == "Canvas") RefreshCanvas();
        SectionShown?.Invoke();
    }

    /// <summary>Raised after another section was picked (the page scrolls back to the top).</summary>
    public event Action? SectionShown;

    [RelayCommand]
    private void ShowSection(string? name)
    {
        if (name != null && Sections.Contains(name)) Section = name;
    }

    // ---- mode ----
    public bool IsDark => ThemeService.IsDark;
    public bool IsLight => !ThemeService.IsDark;

    // ---- material ----
    public string Material => ThemeService.Material;
    public bool IsMica => Material == "Mica";
    public bool IsBlur => Material == "Blur";
    public bool IsFrosted => Material == "Frosted";
    public bool IsGlass => Material == "Glass";

    /// <summary>Materials need the Windows 11 backdrop API (build 22621 or newer).</summary>
    public bool MaterialsSupported => Backdrop.IsMicaSupported;

    /// <summary>Blur, Frosted glass and Glass: not offered while Energy Saver is on (Windows draws
    /// Acrylic flat then, and the window shows Mica instead).</summary>
    public bool GlassMaterialsAvailable => Backdrop.IsMicaSupported && !ThemeService.EnergySaverActive;

    /// <summary>A line under the material cards when Windows will not draw them as intended.</summary>
    public string NoteText =>
        !Backdrop.IsMicaSupported
            ? "Window materials need Windows 11 (version 22H2 or newer). On this PC the window stays flat, but Light and Dark still work."
            : ThemeService.EnergySaverActive
                ? "Energy Saver is on, so Windows draws blur and glass as a flat color. The window uses Mica until Energy Saver is turned off; your choice comes back then."
            : !Backdrop.TransparencyEnabled
                ? "Windows transparency effects are turned off, so every material shows as a flat color. Turn them on in Windows Settings > Personalization > Colors."
                : "";

    public bool HasNote => NoteText.Length > 0;

    // ---- start page ----
    /// <summary>Hide the start page's clock, greeting and "continue reading" card: the library's name
    /// becomes the title and the folders fill the page.</summary>
    public bool CompactHome
    {
        get => ThemeService.CompactHome;
        set
        {
            if (value == ThemeService.CompactHome) return;
            ThemeService.SetCompactHome(value);
            OnPropertyChanged();
        }
    }

    // ---- tool defaults ----
    /// <summary>One row per markup tool: the colour, thickness and opacity a new document starts it with.</summary>
    public ObservableCollection<ToolDefaultRow> ToolRows { get; } = new();

    /// <summary>Font size (pt) of a new text box.</summary>
    public double QuickTextSize
    {
        get => ThemeService.QuickTextSize;
        set
        {
            if (Math.Round(value) == ThemeService.QuickTextSize) return;
            ThemeService.SetQuickTextSize(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(QuickTextSizeText));
        }
    }

    public string QuickTextSizeText => $"{ThemeService.QuickTextSize:0} pt";

    [RelayCommand]
    private void ResetToolDefaults()
    {
        ThemeService.ResetToolDefaults();
        foreach (var row in ToolRows) row.Reload();
        OnPropertyChanged(nameof(QuickTextSize));
        OnPropertyChanged(nameof(QuickTextSizeText));
    }

    // ---- sidebar ----
    /// <summary>What collapsing the start page's sidebar leaves behind.</summary>
    public bool CollapseToRail
    {
        get => ThemeService.CollapseToRail;
        set
        {
            if (value == ThemeService.CollapseToRail) return;
            ThemeService.SetCollapseToRail(value);
            OnPropertyChanged(string.Empty);
        }
    }

    public bool CollapseHidden
    {
        get => !ThemeService.CollapseToRail;
        set => CollapseToRail = !value;
    }

    // ---- about ----
    public string VersionText { get; }

    [RelayCommand]
    private void CheckForUpdates() => _checkForUpdates();

    // ---- reading ----
    public bool ShowProgressLine
    {
        get => ThemeService.ShowProgressLine;
        set
        {
            if (value == ThemeService.ShowProgressLine) return;
            ThemeService.SetShowProgressLine(value);
            OnPropertyChanged();
        }
    }

    // ---- motion ----
    public bool ReduceMotion
    {
        get => ThemeService.ReduceMotion;
        set
        {
            if (value == ThemeService.ReduceMotion) return;
            ThemeService.SetReduceMotion(value);
            OnPropertyChanged();
        }
    }

    /// <summary>Re-read everything (also when the page opens: the person may have changed a
    /// Windows setting or used the command palette in the meantime).</summary>
    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        if (IsCanvasSection) RefreshCanvas();
    }

    [RelayCommand]
    private void SetAppearance(string mode)
    {
        bool dark = string.Equals(mode, "dark", StringComparison.OrdinalIgnoreCase);
        if (dark != ThemeService.IsDark) ThemeService.ApplyTheme(dark);
    }

    [RelayCommand]
    private void SetMaterial(string material) => ThemeService.SetMaterial(material);

    [RelayCommand]
    private void Close() => _close();
}
