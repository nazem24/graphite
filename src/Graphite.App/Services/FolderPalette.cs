using System.Globalization;
using System.Windows.Media;

namespace Graphite.App.Services;

/// <summary>One colour offered in the folder colour picker.</summary>
public sealed class FolderSwatch
{
    public FolderSwatch(string name, string hex, bool isCurrent = false)
    {
        Name = name;
        Hex = hex;
        IsCurrent = isCurrent;
        var brush = new SolidColorBrush(FolderPalette.TryParse(hex, out var c) ? c : Colors.Gray);
        brush.Freeze();
        Brush = brush;
    }

    public string Name { get; }
    public string Hex { get; }
    public Brush Brush { get; }
    public bool IsCurrent { get; }
}

/// <summary>The two brushes a folder needs, both derived from the single colour the person picked:
/// the colour of the folder's back panel, and a slightly deeper shade of it for the small folder
/// icon in list view. The front flap of a folder card is not here: it is the same colour tinted by
/// the current theme and material (see the Folder.* resources in Themes\Materials).</summary>
public sealed class FolderLook
{
    public required SolidColorBrush Back { get; init; }
    public required SolidColorBrush Front { get; init; }
}

/// <summary>The colours folder cards can have, and the maths that turns one chosen colour into a
/// folder's brushes.</summary>
public static class FolderPalette
{
    /// <summary>The teal of the original design.</summary>
    public const string DefaultHex = "#2C6B66";

    public static readonly IReadOnlyList<FolderSwatch> Presets = new FolderSwatch[]
    {
        new("Teal",     DefaultHex),
        new("Green",    "#3C7A47"),
        new("Olive",    "#6B7A2E"),
        new("Amber",    "#A57C14"),
        new("Orange",   "#B8602A"),
        new("Red",      "#A9453E"),
        new("Pink",     "#A8456E"),
        new("Purple",   "#7A4D9C"),
        new("Indigo",   "#4B4FA8"),
        new("Blue",     "#2F66A6"),
        new("Slate",    "#4E6272"),
        new("Graphite", "#4A4D55"),
    };

    public static readonly Color Default = TryParse(DefaultHex, out var d) ? d : Colors.Teal;

    /// <summary>Accepts "#2C6B66", "2c6b66" and the short form "#2b6".</summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(ch => $"{ch}{ch}"));
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            return false;
        color = Color.FromRgb((byte)((rgb >> 16) & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)(rgb & 0xFF));
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Build the folder's brushes from the colour of its back panel.</summary>
    public static FolderLook Look(Color back)
    {
        var (h, sat, l) = ToHsl(back);
        return new FolderLook
        {
            Back = Brush(back),
            Front = Brush(FromHsl(h, sat, l * 0.78)),
        };
    }

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ------------------------------------------------------------- colour maths

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, d = max - min;
        if (d < 1e-9) return (0, 0, l);

        double s = d / (1 - Math.Abs(2 * l - 1));
        double h;
        if (max == r) h = ((g - b) / d) % 6;
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h *= 60;
        if (h < 0) h += 360;
        return (h, s, l);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        static byte To8(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        return Color.FromRgb(To8(r + m), To8(g + m), To8(b + m));
    }
}
