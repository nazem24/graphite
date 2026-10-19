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
            // Same Id: a clone is the same annotation at another point in time (undo history, bake),
            // never a second annotation. Lets caches and typing-burst keys survive an undo.
            Id = Id,
            Kind = Kind,
            PageIndex = PageIndex,
            Bounds = Bounds,
            Quads = Quads.ToList(),
            // The outer list is copied, the point lists inside are shared: once committed a stroke's
            // points are never edited (erase / move / resize build new lists), and the renderer caches
            // each stroke's smoothed geometry by list identity — copying them made every undo throw
            // that cache away and re-smooth every stroke on screen.
            Strokes = Strokes.ToList(),
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

    /// <summary>True when <paramref name="o"/> describes exactly the same annotation (same Id, same content).
    /// Undo uses it to leave untouched markup alone instead of rebuilding its card and its drawing.</summary>
    public bool ContentEquals(Annotation o)
    {
        if (ReferenceEquals(this, o)) return true;
        if (Id != o.Id || Kind != o.Kind || PageIndex != o.PageIndex || Bounds != o.Bounds ||
            IsFreehand != o.IsFreehand || ColorHex != o.ColorHex || Opacity != o.Opacity ||
            StrokeWidth != o.StrokeWidth || FontSize != o.FontSize || FontFamily != o.FontFamily ||
            Bold != o.Bold || Italic != o.Italic || Underline != o.Underline ||
            FillColorHex != o.FillColorHex || BorderColorHex != o.BorderColorHex ||
            LineStart != o.LineStart || LineEnd != o.LineEnd ||
            Author != o.Author || Contents != o.Contents || Modified != o.Modified ||
            Quads.Count != o.Quads.Count || Strokes.Count != o.Strokes.Count || Replies.Count != o.Replies.Count)
            return false;

        for (int i = 0; i < Quads.Count; i++)
            if (Quads[i] != o.Quads[i]) return false;
        for (int i = 0; i < Strokes.Count; i++)
            if (!ReferenceEquals(Strokes[i], o.Strokes[i]) && !Strokes[i].SequenceEqual(o.Strokes[i])) return false;
        for (int i = 0; i < Replies.Count; i++)
            if (Replies[i].Author != o.Replies[i].Author || Replies[i].Text != o.Replies[i].Text ||
                Replies[i].Modified != o.Replies[i].Modified) return false;
        return true;
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
