using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Graphite.App.Services;

namespace Graphite.App.ViewModels;

/// <summary>One choice in the "Window material" picker on the settings page.</summary>
public sealed partial class MaterialOption : ObservableObject
{
    private readonly Func<bool> _enabled;

    /// <param name="colors">The colours of the swatch on the card, left to right.</param>
    public MaterialOption(string id, string title, string blurb, string[] colors, Func<bool> enabled)
    {
        Id = id;
        Title = title;
        Blurb = blurb;
        _enabled = enabled;

        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        for (int i = 0; i < colors.Length; i++)
        {
            var color = FolderPalette.TryParse(colors[i], out var parsed) ? parsed : Colors.Gray;
            brush.GradientStops.Add(new GradientStop(color, colors.Length == 1 ? 0 : (double)i / (colors.Length - 1)));
        }
        brush.Freeze();
        Swatch = brush;
        Refresh();
    }

    /// <summary>The name <see cref="ThemeService.Materials"/> knows it by.</summary>
    public string Id { get; }
    public string Title { get; }
    public string Blurb { get; }

    /// <summary>A gradient in the colours the material is known by: the picture on its card.</summary>
    public Brush Swatch { get; }

    [ObservableProperty] private bool isSelected;
    [ObservableProperty] private bool isEnabled = true;

    public void Refresh()
    {
        IsSelected = ThemeService.Material == Id;
        IsEnabled = _enabled();
    }
}

/// <summary>A heading and the materials under it (solid, glass).</summary>
public sealed class MaterialGroup
{
    public MaterialGroup(string title, string hint, IReadOnlyList<MaterialOption> options)
    {
        Title = title;
        Hint = hint;
        Options = options;
    }

    public string Title { get; }
    public string Hint { get; }
    public IReadOnlyList<MaterialOption> Options { get; }
}
