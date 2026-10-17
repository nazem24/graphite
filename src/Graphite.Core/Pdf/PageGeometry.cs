using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace Graphite.Core.Pdf;

/// <summary>
/// Maps between a page's PDF user space (y up, origin wherever the MediaBox/CropBox put it,
/// content unrotated) and the space Graphite works in everywhere else: the page as it is
/// displayed — CropBox only, /Rotate applied, top-left origin, points. This is the space
/// PDFium renders (<c>PdfRenderer.PageSizes</c>) and PdfPig reports words in, so annotations,
/// form fields and content edits written through this mapping line up with the rendered page
/// even on rotated or cropped pages.
/// </summary>
public readonly record struct PageGeometry(double Llx, double Lly, double Urx, double Ury, int Rotation)
{
    /// <summary>A plain upright page whose box starts at the origin.</summary>
    public static PageGeometry Upright(double width, double height) => new(0, 0, width, height, 0);

    /// <summary>Unrotated width/height of the visible box (user space).</summary>
    public double UserWidth => Urx - Llx;
    public double UserHeight => Ury - Lly;

    /// <summary>Size of the page as displayed (rotation applied).</summary>
    public double DisplayWidth => Rotation is 90 or 270 ? UserHeight : UserWidth;
    public double DisplayHeight => Rotation is 90 or 270 ? UserWidth : UserHeight;

    /// <summary>Display point (top-left origin) → PDF user space.</summary>
    public (double X, double Y) ToUser(double u, double v) => Rotation switch
    {
        90 => (Llx + v, Lly + u),
        180 => (Urx - u, Lly + v),
        270 => (Urx - v, Ury - u),
        _ => (Llx + u, Ury - v),
    };

    public (double X, double Y) ToUser(PointD p) => ToUser(p.X, p.Y);

    /// <summary>PDF user space → display point (top-left origin).</summary>
    public PointD ToDisplay(double x, double y) => Rotation switch
    {
        90 => new PointD(y - Lly, x - Llx),
        180 => new PointD(Urx - x, y - Lly),
        270 => new PointD(Ury - y, Urx - x),
        _ => new PointD(x - Llx, Ury - y),
    };

    /// <summary>A user-space rectangle given by two corners (e.g. a /Rect array) → display rectangle.</summary>
    public RectD ToDisplayRect(double x1, double y1, double x2, double y2)
    {
        var a = ToDisplay(x1, y1);
        var b = ToDisplay(x2, y2);
        return RectD.FromCorners(a.X, a.Y, b.X, b.Y);
    }

    /// <summary>Display rectangle → user-space rectangle as (llx, lly, urx, ury), ready for a /Rect array.
    /// Rotations are multiples of 90°, so an axis-aligned rectangle stays axis-aligned.</summary>
    public (double X1, double Y1, double X2, double Y2) ToUserRect(RectD r)
    {
        var a = ToUser(r.Left, r.Top);
        var b = ToUser(r.Right, r.Bottom);
        return (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    }

    /// <summary>
    /// Affine map display → user space as PDF matrix elements [a b c d e f]
    /// (x = a·u + c·v + e, y = b·u + d·v + f).
    /// </summary>
    public (double A, double B, double C, double D, double E, double F) DisplayToUserMatrix() => Rotation switch
    {
        90 => (0, 1, 1, 0, Llx, Lly),
        180 => (-1, 0, 0, 1, Urx, Lly),
        270 => (0, -1, -1, 0, Urx, Ury),
        _ => (1, 0, 0, -1, Llx, Ury),
    };

    /// <summary>
    /// Matrix for an annotation/widget appearance stream drawn "upright as the reader sees it":
    /// the form's BBox is [0 0 w h] in display orientation (y up), and this /Matrix turns it so
    /// it lands upright on the rotated page. Translation doesn't matter — viewers fit the
    /// transformed BBox onto the annotation's /Rect.
    /// </summary>
    public static (double A, double B, double C, double D) AppearanceRotation(int degrees) =>
        Normalize(degrees) switch
        {
            90 => (0, 1, -1, 0),
            180 => (-1, 0, 0, -1),
            270 => (0, -1, 1, 0),
            _ => (1, 0, 0, 1),
        };

    /// <summary>Snap any /Rotate value to 0, 90, 180 or 270.</summary>
    public static int Normalize(int degrees)
    {
        int r = ((degrees % 360) + 360) % 360;
        return (int)(Math.Round(r / 90.0) * 90) % 360;
    }

    /// <summary>Geometry of a PdfSharp page. Reads the raw entries (inherited values are copied
    /// onto every page when PdfSharp opens a document) without PdfSharp's convenience getters,
    /// which create missing boxes as a side effect and swap width/height for rotated pages.</summary>
    public static PageGeometry Of(PdfPage page)
    {
        var media = ReadBox(page, "/MediaBox") ?? (0, 0, 612, 792);
        var crop = ReadBox(page, "/CropBox");

        var box = media;
        if (crop is { } c)
        {
            // Visible area = CropBox ∩ MediaBox (falls back to the CropBox when they don't overlap),
            // the same rule PdfPig and PDFium use.
            double l = Math.Max(media.X1, c.X1), b = Math.Max(media.Y1, c.Y1);
            double r = Math.Min(media.X2, c.X2), t = Math.Min(media.Y2, c.Y2);
            box = r > l && t > b ? (l, b, r, t) : c;
        }

        int rotate = 0;
        if (page.Elements["/Rotate"] is { } rot && PdfValues.Number(rot) is { } deg)
            rotate = Normalize((int)Math.Round(deg));

        return new PageGeometry(box.X1, box.Y1, box.X2, box.Y2, rotate);
    }

    /// <summary>
    /// Transform to prepend to an <see cref="XGraphics"/> created on <paramref name="page"/> so
    /// that drawing coordinates are display coordinates (top-left origin, rotation and CropBox
    /// applied). PdfSharp's own page space ignores /Rotate and the box origin: it flips y around
    /// <c>page.Height</c> (which PdfSharp swaps with the width on rotated pages) and nothing else.
    /// </summary>
    public static XMatrix DisplayTransform(PdfPage page)
    {
        var geo = Of(page);
        var (a, b, c, d, e, f) = geo.DisplayToUserMatrix();
        double pageHeight = page.Height.Point;
        // gfx x = user x;  gfx y = pageHeight − user y.
        return new XMatrix(a, -b, c, -d, e, pageHeight - f);
    }

    private static (double X1, double Y1, double X2, double Y2)? ReadBox(PdfPage page, string key)
    {
        if (PdfValues.Box(page.Elements[key]) is not { } b) return null;
        if (b.X2 - b.X1 < 1e-6 || b.Y2 - b.Y1 < 1e-6) return null;
        return b;
    }
}

/// <summary>Small tolerant accessors for raw PdfSharp items (references resolved, wrong types → null).</summary>
public static class PdfValues
{
    public static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

    public static double? Number(PdfItem? item) => Resolve(item) switch
    {
        PdfReal r => r.Value,
        PdfInteger i => i.Value,
        PdfLongInteger l => l.Value,
        _ => null,
    };

    public static string? Text(PdfItem? item) => Resolve(item) switch
    {
        PdfString s => s.Value,
        PdfStringObject so => so.Value,
        PdfName n => n.Value.TrimStart('/'),
        PdfNameObject no => no.Value.TrimStart('/'),
        _ => null,
    };

    /// <summary>A name without its leading slash ("/Yes" → "Yes").</summary>
    public static string? Name(PdfItem? item) => Resolve(item) is PdfName n ? n.Value.TrimStart('/') : null;

    public static PdfDictionary? Dict(PdfItem? item) => Resolve(item) as PdfDictionary;

    public static PdfArray? Array(PdfItem? item) => Resolve(item) as PdfArray;

    /// <summary>A rectangle entry (/MediaBox, /Rect, …) as normalized corners. PdfSharp keeps these
    /// as plain arrays or — once something has read them through its own API, which PdfSharp 6.2
    /// does for page boxes when it opens a file — as <see cref="PdfRectangle"/> objects; both work.</summary>
    public static (double X1, double Y1, double X2, double Y2)? Box(PdfItem? item)
    {
        double x1, y1, x2, y2;
        switch (Resolve(item))
        {
            case PdfRectangle r:
                (x1, y1, x2, y2) = (r.X1, r.Y1, r.X2, r.Y2);
                break;
            case PdfArray { Elements.Count: >= 4 } a:
                x1 = Number(a.Elements[0]) ?? 0; y1 = Number(a.Elements[1]) ?? 0;
                x2 = Number(a.Elements[2]) ?? 0; y2 = Number(a.Elements[3]) ?? 0;
                break;
            default:
                return null;
        }
        return (Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }
}
