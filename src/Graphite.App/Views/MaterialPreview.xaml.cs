using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using Graphite.App.Services;

namespace Graphite.App.Views;

/// <summary>A small picture of the app in one window material, for the settings page's cards.
/// It merges that material's dictionary (for the current light/dark mode) into its own resources,
/// so the brushes it draws with are the real ones.</summary>
public partial class MaterialPreview : UserControl
{
    public static readonly DependencyProperty MaterialProperty = DependencyProperty.Register(
        nameof(Material), typeof(string), typeof(MaterialPreview),
        new PropertyMetadata("Mica", (d, _) => ((MaterialPreview)d).Refresh()));

    /// <summary>One of <see cref="ThemeService.Materials"/>.</summary>
    public string Material
    {
        get => (string)GetValue(MaterialProperty);
        set => SetValue(MaterialProperty, value);
    }

    // How the Windows backdrop treats the wallpaper in each material: blur radius, and how much of
    // the theme's background color is laid over it. Mica is nearly flat; Glass is nearly clear.
    private static readonly Dictionary<string, (double Blur, double Tint)> Look = new()
    {
        ["Mica"] = (48, 0.82),
        ["Blur"] = (26, 0.42),
        ["Frosted"] = (34, 0.60),
        ["Glass"] = (18, 0.20),
    };

    public MaterialPreview()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ThemeService.AppearanceChanged -= Refresh;
            ThemeService.AppearanceChanged += Refresh;
            Refresh();
        };
        Unloaded += (_, _) => ThemeService.AppearanceChanged -= Refresh;
        Refresh();
    }

    private void Refresh()
    {
        string material = ThemeService.Materials.Contains(Material) ? Material : "Mica";
        Resources.MergedDictionaries.Clear();
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = ThemeService.MaterialUri(material, ThemeService.IsDark) });

        var (blur, tint) = Look[material];
        Wallpaper.Effect = new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Performance };
        Tint.Opacity = tint;
    }
}
