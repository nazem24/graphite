using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Graphite.App.Services;

namespace Graphite.App.ViewModels;

/// <summary>One markup tool's built-in starting style: colour, line thickness (or eraser size) and opacity.
/// A null part means the tool has no such option. <see cref="Key"/> is also the key the person's own
/// defaults are saved under (<see cref="ThemeService.GetToolDefault"/>).</summary>
public sealed record ToolStyle(string Key, string Title, ToolKind Kind, string? Color, double? Width, double? Opacity);

/// <summary>The tools whose style can be set as a default (Settings > Tool defaults), their built-in
/// values, and the value a new document starts with: the person's own default if there is one, else the
/// built-in one.</summary>
public static class ToolStyles
{
    public static readonly IReadOnlyList<ToolStyle> All = new[]
    {
        new ToolStyle("Highlight", "Highlighter", ToolKind.Highlight, "#F2C744", null, 1.0),
        // The freehand marker takes the highlighter's colour; only its see-through-ness is its own.
        new ToolStyle("Marker", "Freehand marker", ToolKind.Highlight, null, null, DocumentViewModel.FreehandHighlightOpacity),
        new ToolStyle("Underline", "Underline", ToolKind.Underline, "#3E6DB5", 1.5, 1.0),
        new ToolStyle("StrikeOut", "Strikethrough", ToolKind.StrikeOut, "#C24444", 1.5, 1.0),
        new ToolStyle("Ink", "Pen", ToolKind.Ink, "#3E6DB5", 1.8, 1.0),
        new ToolStyle("Eraser", "Eraser", ToolKind.Eraser, null, 4.0, null),
        new ToolStyle("Rect", "Rectangle", ToolKind.Rect, "#C24444", 1.6, 1.0),
        new ToolStyle("Ellipse", "Ellipse", ToolKind.Ellipse, "#C24444", 1.6, 1.0),
        new ToolStyle("Arrow", "Arrow", ToolKind.Arrow, "#C24444", 2.0, 1.0),
        new ToolStyle("Text", "Text box", ToolKind.Text, "#1B1B1A", null, null),
        new ToolStyle("Signature", "Signature", ToolKind.Signature, "#1B3A6B", 1.6, null),
    };

    public const double DefaultTextSize = 11;

    /// <summary>The colours offered in the settings (the same ones as in the toolbar's tool options).</summary>
    public static readonly (string Name, string Hex)[] Palette =
    {
        ("Yellow", "#F2C744"), ("Orange", "#E8853C"), ("Red", "#C24444"), ("Pink", "#C75E9A"),
        ("Blue", "#3E6DB5"), ("Teal", "#2E8C83"), ("Green", "#4E9C51"), ("Purple", "#7B5EC7"),
        ("Graphite", "#3B3B38"), ("Black", "#111111"),
    };

    public static string? ColorOf(ToolStyle s) => s.Color == null ? null : ThemeService.GetToolDefault(s.Key)?.Color ?? s.Color;
    public static double? WidthOf(ToolStyle s) => s.Width == null ? null : ThemeService.GetToolDefault(s.Key)?.Width ?? s.Width;
    public static double? OpacityOf(ToolStyle s) => s.Opacity == null ? null : ThemeService.GetToolDefault(s.Key)?.Opacity ?? s.Opacity;
}

/// <summary>A colour dot in a tool's row of defaults.</summary>
public sealed partial class ToolSwatch : ObservableObject
{
    public ToolSwatch(string name, string hex)
    {
        Name = name;
        Hex = hex;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        Brush = brush;
    }

    public string Name { get; }
    public string Hex { get; }
    public Brush Brush { get; }

    [ObservableProperty] private bool isSelected;
}

/// <summary>One line of Settings > Tool defaults: a tool with its default colour, thickness and opacity.
/// Changes are saved a moment after the last one (a slider drag is many changes) and apply to the
/// documents that are open as well as to new ones.</summary>
public sealed partial class ToolDefaultRow : ObservableObject
{
    private readonly DispatcherTimer _saveTimer;
    private bool _loading = true;

    public ToolDefaultRow(ToolStyle style)
    {
        Style = style;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Persist(); };

        if (style.Color != null)
        {
            var swatches = new List<ToolSwatch>();
            // A built-in colour that is not in the palette (the text box's near-black, the signature's ink
            // blue) comes first, so the starting point is always one of the choices.
            if (!ToolStyles.Palette.Any(p => SameColor(p.Hex, style.Color)))
                swatches.Add(new ToolSwatch("Default", style.Color));
            swatches.AddRange(ToolStyles.Palette.Select(p => new ToolSwatch(p.Name, p.Hex)));
            Swatches = swatches;
        }
        else Swatches = Array.Empty<ToolSwatch>();

        Reload();
    }

    public ToolStyle Style { get; }
    public string Title => Style.Title;
    public bool HasColor => Style.Color != null;
    public bool HasWidth => Style.Width != null;
    public bool HasOpacity => Style.Opacity != null;
    public IReadOnlyList<ToolSwatch> Swatches { get; }

    [ObservableProperty] private string colorHex = "";
    [ObservableProperty] private double width;
    [ObservableProperty] private double opacity = 1;

    public string WidthText => $"{Width:0.#} pt";
    public string OpacityText => $"{Math.Round(Opacity * 100)}%";

    /// <summary>Read the saved (or built-in) values again, e.g. after "Reset to defaults".</summary>
    public void Reload()
    {
        _loading = true;
        ColorHex = ToolStyles.ColorOf(Style) ?? "";
        Width = ToolStyles.WidthOf(Style) ?? 0;
        Opacity = ToolStyles.OpacityOf(Style) ?? 1;
        _loading = false;
        MarkSwatches();
    }

    [RelayCommand]
    private void PickColor(string? hex)
    {
        if (!string.IsNullOrEmpty(hex)) ColorHex = hex;
    }

    partial void OnColorHexChanged(string value) { MarkSwatches(); Queue(); }

    partial void OnWidthChanged(double value)
    {
        double rounded = Math.Round(value, 1);
        if (rounded != value) { Width = rounded; return; }
        OnPropertyChanged(nameof(WidthText));
        Queue();
    }

    partial void OnOpacityChanged(double value)
    {
        double rounded = Math.Round(value, 2);
        if (rounded != value) { Opacity = rounded; return; }
        OnPropertyChanged(nameof(OpacityText));
        Queue();
    }

    private void MarkSwatches()
    {
        foreach (var s in Swatches) s.IsSelected = SameColor(s.Hex, ColorHex);
    }

    private void Queue()
    {
        if (_loading) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Only what differs from the built-in style is saved, so the saved file stays small and a
    /// tool the person never touched keeps following the app's own defaults.</summary>
    private void Persist()
    {
        string? c = HasColor && !SameColor(ColorHex, Style.Color) ? ColorHex : null;
        double? w = HasWidth && Math.Abs(Width - Style.Width!.Value) > 0.001 ? Width : null;
        double? o = HasOpacity && Math.Abs(Opacity - Style.Opacity!.Value) > 0.001 ? Opacity : null;
        ThemeService.SetToolDefault(Style.Key, c, w, o);
    }

    private static bool SameColor(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
