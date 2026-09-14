namespace Graphite.Core.Annotations;

public enum AnnotationKind
{
    Highlight,
    Underline,
    StrikeOut,
    Ink,
    Square,
    Circle,
    Note,
    FreeText,
    Line,
}

public sealed class AnnotationReply
{
    public string Author { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTime Modified { get; set; } = DateTime.Now;
}

/// <summary>
/// View/edit model for a single annotation.
/// All geometry is in PDF points with TOP-LEFT origin (converted at read/write time).
/// </summary>
public sealed class Annotation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public AnnotationKind Kind { get; init; }
    public int PageIndex { get; set; }

    /// <summary>Overall bounds. For text markup this is the union of quads.</summary>
    public RectD Bounds { get; set; }

    /// <summary>Word quads for Highlight/Underline/StrikeOut.</summary>
    public List<RectD> Quads { get; set; } = new();

    /// <summary>Strokes for Ink (and for Highlight when IsFreehand is set).</summary>
    public List<List<PointD>> Strokes { get; set; } = new();

    /// <summary>When Kind == Highlight: true for a freehand marker stroke (Strokes),
    /// false for the default word-snapped highlight (Quads).</summary>
    public bool IsFreehand { get; set; }

    public string ColorHex { get; set; } = "#FFD24D";
    public double Opacity { get; set; } = 1.0;
    public double StrokeWidth { get; set; } = 1.5;

    /// <summary>Text size in points for FreeText annotations.</summary>
    public double FontSize { get; set; } = 12;

    // ---- FreeText formatting (ColorHex is the text color, as before) ----
    public string FontFamily { get; set; } = "Segoe UI";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Underline { get; set; }

    /// <summary>Background fill behind a FreeText box. Null/empty = no fill.</summary>
    public string? FillColorHex { get; set; }

    /// <summary>Border color around a FreeText box, distinct from the text color. Null/empty = no border.</summary>
    public string? BorderColorHex { get; set; }

    /// <summary>Endpoints for Line (arrow) annotations.</summary>
    public PointD LineStart { get; set; }
    public PointD LineEnd { get; set; }

    public string Author { get; set; } = Environment.UserName;
    public string Contents { get; set; } = "";
    public DateTime Modified { get; set; } = DateTime.Now;

    public List<AnnotationReply> Replies { get; } = new();

    public Annotation Clone()
    {
        var c = new Annotation
        {
            Kind = Kind,
            PageIndex = PageIndex,
            Bounds = Bounds,
            Quads = Quads.ToList(),
            Strokes = Strokes.Select(s => s.ToList()).ToList(),
            IsFreehand = IsFreehand,
            ColorHex = ColorHex,
            Opacity = Opacity,
            StrokeWidth = StrokeWidth,
            FontSize = FontSize,
            FontFamily = FontFamily,
            Bold = Bold,
            Italic = Italic,
            Underline = Underline,
            FillColorHex = FillColorHex,
            BorderColorHex = BorderColorHex,
            LineStart = LineStart,
            LineEnd = LineEnd,
            Author = Author,
            Contents = Contents,
            Modified = Modified,
        };
        c.Replies.AddRange(Replies.Select(r => new AnnotationReply { Author = r.Author, Text = r.Text, Modified = r.Modified }));
        return c;
    }

    /// <summary>Parse "#RRGGBB" (or "#AARRGGBB") into 0..1 components. Malformed input
    /// falls back to black rather than throwing — this runs on the save path, where one
    /// bad color string must not abort writing the whole document.</summary>
    public static (double R, double G, double B) ParseColor(string hex)
    {
        hex = (hex ?? "").TrimStart('#');
        if (hex.Length == 8) hex = hex[2..]; // strip alpha
        if (hex.Length == 6 &&
            int.TryParse(hex[0..2], System.Globalization.NumberStyles.HexNumber, null, out int r) &&
            int.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out int g) &&
            int.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out int b))
            return (r / 255.0, g / 255.0, b / 255.0);
        return (0, 0, 0);
    }

    public static string ToHex(double r, double g, double b) =>
        $"#{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}";
}
