using System.Windows;
using System.Windows.Media;

namespace Graphite.App.Services;

/// <summary>
/// The "Tint" material: Glass whose surfaces pick up the colour of where you are. Inside a Canvas course the colour of
/// that course (the one its icon has in the rails), inside a folder of the Local library the colour of that folder's
/// card; anywhere else the plain glass of Themes\Materials\Tint.*.xaml.
///
/// The surfaces are the brushes the other materials define (panel, viewer, glass, its rim, the floating bars). While a
/// course or folder is open, tinted copies of those brushes sit in the application resources and every DynamicResource
/// that uses them follows; with none open the entries are removed and the material's own brushes show again. (The
/// copies are built whole and frozen: WPF freezes brushes that styles and templates pick up from resources, so they
/// cannot be animated in place.) The page glows follow too (App.AmbientColorA / B, read by AmbientBackdrop).
/// </summary>
public static class ContextTint
{
    /// <summary>One surface brush: how far its colours move towards the tint (dark and light mode), and how much
    /// more opaque they get.</summary>
    private sealed record Spec(string Key, double Dark, double Light, double Alpha);

    private static readonly Spec[] Specs =
    {
        new("App.PanelBrush", 0.80, 0.45, 1.25),
        new("App.ViewerBrush", 0.60, 0.25, 1.20),
        new("App.GlassBrush", 0.85, 0.60, 1.35),
        new("App.GlassBorderBrush", 0.45, 0.35, 1.00),
        new("App.GlassSolidBrush", 0.55, 0.30, 1.00),
        new("App.FloatingBrush", 0.60, 0.35, 1.05),
        new("App.PopupBrush", 0.45, 0.25, 1.00),
        new("App.PopupBorderBrush", 0.40, 0.30, 1.00),
    };

    /// <summary>The material's own brush for each key (a copy, never in use), from which tinted ones are made.</summary>
    private static readonly Dictionary<string, Brush> Neutral = new();

    private static Color? _wanted;

    /// <summary>Raised when the wanted colour changed (the glows redraw).</summary>
    public static event Action? Changed;

    /// <summary>Tell the material where the person is: a colour, or null for no course or folder open.</summary>
    public static void Set(Color? color)
    {
        if (_wanted == color) return;
        _wanted = color;
        if (Neutral.Count > 0)
        {
            Apply();
            ThemeService.SoftSwap();   // the surfaces change at once: a quick dip and back reads as a fade
        }
        Changed?.Invoke();
    }

    /// <summary>The theme or the material was just applied: take up the tint again, or take it away.</summary>
    internal static void Reapply()
    {
        var resources = Application.Current.Resources;
        Neutral.Clear();
        foreach (var spec in Specs) resources.Remove(spec.Key);   // so a lookup finds the material's own brush
        resources.Remove("App.AmbientColorA");
        resources.Remove("App.AmbientColorB");
        if (ThemeService.Material == "Tint")
        {
            foreach (var spec in Specs)
                if (resources[spec.Key] is Brush own) Neutral[spec.Key] = own.Clone();
            Apply();
        }
        Changed?.Invoke();
    }

    /// <summary>Put the tinted brushes in the resources, or take them out when no course or folder is open.</summary>
    private static void Apply()
    {
        var resources = Application.Current.Resources;
        bool dark = ThemeService.IsDark;
        foreach (var spec in Specs)
        {
            if (!Neutral.TryGetValue(spec.Key, out var template) || _wanted is not { } tint)
            {
                resources.Remove(spec.Key);
                continue;
            }
            resources[spec.Key] = TintedCopy(template, spec, tint, dark);
        }

        if (_wanted is { } glow)
        {
            resources["App.AmbientColorA"] = dark ? glow : Mix(glow, Colors.White, 0.1);
            resources["App.AmbientColorB"] = Mix(glow, Colors.White, dark ? 0.3 : 0.2);
        }
        else
        {
            resources.Remove("App.AmbientColorA");
            resources.Remove("App.AmbientColorB");
        }
    }

    private static Brush TintedCopy(Brush template, Spec spec, Color tint, bool dark)
    {
        var copy = template.Clone();
        switch (copy)
        {
            case SolidColorBrush solid:
                solid.Color = Tinted(solid.Color, tint, spec, dark);
                break;
            case GradientBrush gradient:
                foreach (var stop in gradient.GradientStops) stop.Color = Tinted(stop.Color, tint, spec, dark);
                break;
        }
        copy.Freeze();
        return copy;
    }

    // ------------------------------------------------------------- colour maths

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t), (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    /// <summary>A surface colour moved towards the tint: the colour itself shifts, and it gets a little more opaque so
    /// the tint is seen. Light mode uses a paler tint so text stays readable.</summary>
    private static Color Tinted(Color neutral, Color tint, Spec spec, bool dark)
    {
        Color towards = dark ? tint : Mix(tint, Colors.White, 0.35);
        double weight = dark ? spec.Dark : spec.Light;
        var rgb = Mix(Color.FromRgb(neutral.R, neutral.G, neutral.B), Color.FromRgb(towards.R, towards.G, towards.B), weight);
        return Color.FromArgb((byte)Math.Clamp(Math.Round(neutral.A * spec.Alpha), 0, 255), rgb.R, rgb.G, rgb.B);
    }
}
