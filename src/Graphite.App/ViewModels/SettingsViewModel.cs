using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Graphite.App.Interop;
using Graphite.App.Services;

namespace Graphite.App.ViewModels;

/// <summary>The settings page: light/dark mode, window material and reduced motion. Everything
/// applies the moment it is chosen (no Apply button); <see cref="ThemeService"/> does the work and
/// remembers it, this class only mirrors the current state for the page.</summary>
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

    /// <summary>A line under the material cards when Windows will not draw them as intended.</summary>
    public string NoteText =>
        !Backdrop.IsMicaSupported
            ? "Window materials need Windows 11 (version 22H2 or newer). On this PC the window stays flat, but Light and Dark still work."
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
    public void Refresh() => OnPropertyChanged(string.Empty);

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
