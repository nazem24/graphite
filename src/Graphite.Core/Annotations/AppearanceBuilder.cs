using System.Text;
using Graphite.Core.Fonts;
using Graphite.Core.Pdf;
using PdfSharp.Pdf;

namespace Graphite.Core.Annotations;

/// <summary>
/// Builds the normal appearance stream (/AP /N) for each annotation Graphite writes, so the
/// markup looks the same in every viewer — including the ones that never synthesize an
/// appearance themselves (Apple Preview, most mobile readers) and PDFium, which prints
/// FreeText and Line annotations only when they carry one.
///
/// Everything is drawn in the page's display orientation: the form's BBox is the annotation
/// rectangle as the reader sees it (origin bottom-left, y up), and the form /Matrix rotates
/// it back onto a page that has a /Rotate entry. That is what keeps text boxes upright on
/// rotated pages.
/// </summary>
internal static class AppearanceBuilder
{
    /// <summary>How far an annotation's drawing reaches beyond its model bounds (stroke
    /// half-widths, arrow heads). The written /Rect includes it so nothing gets clipped.</summary>
    public static double Padding(Annotation a) => a.Kind switch
    {
        AnnotationKind.Highlight when a.IsFreehand => Math.Max(4.0, a.StrokeWidth) / 2 + 1,
        AnnotationKind.Highlight => 0.5,
        AnnotationKind.Underline or AnnotationKind.StrikeOut => Math.Max(1.0, a.StrokeWidth) / 2 + 1,
        AnnotationKind.Ink or AnnotationKind.Square or AnnotationKind.Circle => a.StrokeWidth / 2 + 1,
        AnnotationKind.Line => ArrowHead(a.StrokeWidth) + a.StrokeWidth + 1,
        AnnotationKind.FreeText => string.IsNullOrEmpty(a.BorderColorHex) ? 0 : Math.Max(0.75, a.StrokeWidth) / 2 + 0.5,
        _ => 0,
    };

    /// <summary>Arrow-head length in points (the same formula the on-screen layer uses at 100 %).</summary>
    public static double ArrowHead(double strokeWidth) => Math.Max(8, strokeWidth * 3.5) + 2;

    /// <summary>The rectangle (display space) the appearance covers: the model bounds plus
    /// <see cref="Padding"/>. For ink and arrows the bounds are recomputed from the geometry so a
    /// stale Bounds can never clip a stroke.</summary>
    public static RectD Extent(Annotation a)
    {
        RectD b = a.Bounds;
        if (a.Kind == AnnotationKind.Ink || (a.Kind == AnnotationKind.Highlight && a.IsFreehand))
        {
            var pts = a.Strokes.SelectMany(s => s).ToList();
            if (pts.Count > 0)
                b = RectD.FromCorners(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Max(p => p.X), pts.Max(p => p.Y));
        }
        else if (a.Kind == AnnotationKind.Line)
        {
            b = RectD.FromCorners(a.LineStart.X, a.LineStart.Y, a.LineEnd.X, a.LineEnd.Y);
        }
        else if (a.Kind is AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut && a.Quads.Count > 0)
        {
            b = a.Quads.Aggregate((x, y) => x.Union(y));
        }
        b = b.Inflate(Padding(a));
        // A zero-sized box makes a degenerate BBox; give it at least a point each way.
        if (b.Width < 1) b = new RectD(b.X - 0.5, b.Y, b.Width + 1, b.Height);
        if (b.Height < 1) b = new RectD(b.X, b.Y - 0.5, b.Width, b.Height + 1);
        return b;
    }

    /// <summary>Create the /AP dictionary for <paramref name="a"/>. <paramref name="extent"/> is the
    /// display-space rectangle the annotation's /Rect was written from; <paramref name="fonts"/>
    /// resolves a standard font resource name ("/Helv") to its font dictionary.</summary>
    public static PdfDictionary? Build(PdfDocument doc, Annotation a, RectD extent, int pageRotation,
        Func<string, PdfDictionary> fonts)
    {
        var c = new Content(extent);
        var resources = new PdfDictionary(doc);
        var gs = new PdfDictionary(doc);
        var fontRes = new PdfDictionary(doc);
        var (r, g, b) = Annotation.ParseColor(a.ColorHex);
        double opacity = Math.Clamp(a.Opacity, 0, 1);

        // Opacity (and Multiply for highlighter ink) goes into an ExtGState inside the stream as
        // well as /CA on the annotation — the convention MuPDF and Acrobat follow, since viewers
        // differ on whether they apply /CA to an existing appearance.
        string? GraphicsState(bool multiply)
        {
            if (opacity >= 0.999 && !multiply) return null;
            var state = new PdfDictionary(doc);
            state.Elements.SetName("/Type", "/ExtGState");
            if (multiply) state.Elements.SetName("/BM", "/Multiply");
            state.Elements.SetReal("/CA", opacity);
            state.Elements.SetReal("/ca", opacity);
            gs.Elements["/GS0"] = state;
            return "/GS0 gs";
        }

        switch (a.Kind)
        {
            case AnnotationKind.Highlight when a.IsFreehand:
                c.Op(GraphicsState(multiply: true));
                c.Op($"{F(r)} {F(g)} {F(b)} RG {F(Math.Max(4.0, a.StrokeWidth))} w 1 J 1 j");
                foreach (var stroke in a.Strokes) c.SmoothStroke(stroke);
                break;

            case AnnotationKind.Highlight:
                c.Op(GraphicsState(multiply: true));
                c.Op($"{F(r)} {F(g)} {F(b)} rg");
                foreach (var q in a.Quads) c.Rect(q, "f");
                break;

            case AnnotationKind.Underline:
            case AnnotationKind.StrikeOut:
                c.Op(GraphicsState(multiply: false));
                c.Op($"{F(r)} {F(g)} {F(b)} RG {F(Math.Max(1.0, a.StrokeWidth))} w 0 J");
                foreach (var q in a.Quads)
                {
                    double y = a.Kind == AnnotationKind.Underline ? q.Bottom : (q.Top + q.Bottom) / 2;
                    c.MoveTo(new PointD(q.Left, y));
                    c.LineTo(new PointD(q.Right, y));
                    c.Op("S");
                }
                break;

            case AnnotationKind.Ink:
                c.Op(GraphicsState(multiply: false));
                c.Op($"{F(r)} {F(g)} {F(b)} RG {F(a.StrokeWidth)} w 1 J 1 j");
                foreach (var stroke in a.Strokes) c.SmoothStroke(stroke);
                break;

            case AnnotationKind.Square:
                c.Op(GraphicsState(multiply: false));
                c.Op($"{F(r)} {F(g)} {F(b)} RG {F(a.StrokeWidth)} w 0 j");
                c.Rect(a.Bounds, "S");
                break;

            case AnnotationKind.Circle:
                c.Op(GraphicsState(multiply: false));
                c.Op($"{F(r)} {F(g)} {F(b)} RG {F(a.StrokeWidth)} w");
                c.Ellipse(a.Bounds);
                c.Op("S");
                break;

            case AnnotationKind.Line:
            {
                c.Op(GraphicsState(multiply: false));
                c.Op($"{F(r)} {F(g)} {F(b)} RG {F(a.StrokeWidth)} w 1 J 1 j");
                c.MoveTo(a.LineStart);
                c.LineTo(a.LineEnd);
                c.Op("S");
                double dx = a.LineEnd.X - a.LineStart.X, dy = a.LineEnd.Y - a.LineStart.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len > 0.01)
                {
                    dx /= len; dy /= len;
                    double head = ArrowHead(a.StrokeWidth);
                    foreach (double ang in new[] { 2.65, -2.65 })
                    {
                        double hx = dx * Math.Cos(ang) - dy * Math.Sin(ang);
                        double hy = dx * Math.Sin(ang) + dy * Math.Cos(ang);
                        c.MoveTo(a.LineEnd);
                        c.LineTo(new PointD(a.LineEnd.X + hx * head, a.LineEnd.Y + hy * head));
                        c.Op("S");
                    }
                }
                break;
            }

            case AnnotationKind.Note:
            {
                var box = a.Bounds;
                c.Op($"{F(r)} {F(g)} {F(b)} rg 0.235 0.235 0.235 RG 1 w");
                c.Rect(box.Inflate(-0.5), "B");
                c.Op("0.235 0.235 0.235 RG");
                double inset = box.Width * 0.22;
                foreach (double f in new[] { 0.38, 0.62 })
                {
                    c.MoveTo(new PointD(box.Left + inset, box.Top + box.Height * f));
                    c.LineTo(new PointD(box.Right - inset, box.Top + box.Height * f));
                    c.Op("S");
                }
                break;
            }

            case AnnotationKind.FreeText:
            {
                var box = a.Bounds;
                if (!string.IsNullOrEmpty(a.FillColorHex))
                {
                    var (fr, fg, fb) = Annotation.ParseColor(a.FillColorHex);
                    c.Op($"{F(fr)} {F(fg)} {F(fb)} rg");
                    c.Rect(box, "f");
                }
                if (!string.IsNullOrEmpty(a.BorderColorHex))
                {
                    var (br, bg, bb) = Annotation.ParseColor(a.BorderColorHex);
                    c.Op($"{F(br)} {F(bg)} {F(bb)} RG {F(Math.Max(0.75, a.StrokeWidth))} w");
                    c.Rect(box, "S");
                }
                if (!string.IsNullOrWhiteSpace(a.Contents))
                {
                    string fontName = FreeTextFontName(a.Bold, a.Italic);
                    fontRes.Elements[fontName] = fonts(fontName);
                    double size = a.FontSize > 0 ? a.FontSize : 12;
                    double lineHeight = size * 1.2;
                    var lines = StandardFonts.Wrap(a.Contents, size, Math.Max(size, box.Width), a.Bold);

                    c.Op("q");
                    c.Rect(box, "W n"); // clip to the box like the on-screen text
                    c.Op(GraphicsState(multiply: false));
                    c.Op($"{F(r)} {F(g)} {F(b)} rg");
                    double baseline = box.Top + size * 0.93;
                    var underlines = new List<(double X, double Y, double W)>();
                    var sb = new StringBuilder();
                    sb.Append("BT ").Append(fontName).Append(' ').Append(F(size)).Append(" Tf\n");
                    foreach (string line in lines)
                    {
                        if (baseline - size * 0.8 > box.Bottom) break;
                        var (lx, ly) = c.Local(new PointD(box.Left, baseline));
                        sb.Append("1 0 0 1 ").Append(F(lx)).Append(' ').Append(F(ly)).Append(" Tm ")
                          .Append(StandardFonts.Literal(line)).Append(" Tj\n");
                        if (a.Underline && line.Length > 0)
                            underlines.Add((box.Left, baseline + size * 0.12, StandardFonts.Width(line, size, a.Bold)));
                        baseline += lineHeight;
                    }
                    sb.Append("ET");
                    c.Op(sb.ToString());
                    if (underlines.Count > 0)
                    {
                        c.Op($"{F(r)} {F(g)} {F(b)} RG {F(Math.Max(0.5, size * 0.06))} w");
                        foreach (var (x, y, w) in underlines)
                        {
                            c.MoveTo(new PointD(x, y));
                            c.LineTo(new PointD(x + w, y));
                            c.Op("S");
                        }
                    }
                    c.Op("Q");
                }
                break;
            }

            default:
                return null;
        }

        if (gs.Elements.Count > 0) resources.Elements["/ExtGState"] = gs;
        if (fontRes.Elements.Count > 0) resources.Elements["/Font"] = fontRes;

        var form = NewForm(doc, extent.Width, extent.Height, pageRotation, resources, c.ToBytes());
        var ap = new PdfDictionary(doc);
        ap.Elements["/N"] = form;
        return ap;
    }

    /// <summary>Resource name of the standard Helvetica face closest to the FreeText formatting —
    /// the same names the /DA string uses.</summary>
    public static string FreeTextFontName(bool bold, bool italic) => (bold, italic) switch
    {
        (true, true) => "/HeBO",
        (true, false) => "/HeBo",
        (false, true) => "/HeOb",
        _ => "/Helv",
    };

    /// <summary>A form XObject of size w × h (display orientation), rotated for the page.</summary>
    public static PdfDictionary NewForm(PdfDocument doc, double w, double h, int rotation,
        PdfDictionary resources, byte[] content)
    {
        var form = new PdfDictionary(doc);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements.SetInteger("/FormType", 1);
        form.Elements["/BBox"] = Numbers(doc, 0, 0, w, h);
        var (ma, mb, mc, md) = PageGeometry.AppearanceRotation(rotation);
        if (PageGeometry.Normalize(rotation) != 0)
            form.Elements["/Matrix"] = Numbers(doc, ma, mb, mc, md, 0, 0);
        form.Elements["/Resources"] = resources;
        form.CreateStream(content);
        doc.Internals.AddObject(form);
        return form;
    }

    public static PdfArray Numbers(PdfDocument doc, params double[] values)
    {
        var arr = new PdfArray(doc);
        foreach (double v in values) arr.Elements.Add(new PdfReal(Math.Round(v, 4)));
        return arr;
    }

    private static string F(double v) => StandardFonts.F(v);

    /// <summary>Content-stream writer working in display coordinates (top-left origin), converted
    /// to the form's local space (origin at the extent's bottom-left corner, y up).</summary>
    private sealed class Content
    {
        private readonly RectD _extent;
        private readonly StringBuilder _sb = new();

        public Content(RectD extent) => _extent = extent;

        public (double X, double Y) Local(PointD p) => (p.X - _extent.Left, _extent.Bottom - p.Y);

        public void Op(string? op)
        {
            if (string.IsNullOrEmpty(op)) return;
            _sb.Append(op).Append('\n');
        }

        public void MoveTo(PointD p)
        {
            var (x, y) = Local(p);
            _sb.Append(F(x)).Append(' ').Append(F(y)).Append(" m\n");
        }

        public void LineTo(PointD p)
        {
            var (x, y) = Local(p);
            _sb.Append(F(x)).Append(' ').Append(F(y)).Append(" l\n");
        }

        private void CurveTo(PointD c1, PointD c2, PointD end)
        {
            var (x1, y1) = Local(c1);
            var (x2, y2) = Local(c2);
            var (x3, y3) = Local(end);
            _sb.Append(F(x1)).Append(' ').Append(F(y1)).Append(' ')
               .Append(F(x2)).Append(' ').Append(F(y2)).Append(' ')
               .Append(F(x3)).Append(' ').Append(F(y3)).Append(" c\n");
        }

        public void Rect(RectD r, string paintOp)
        {
            var (x, y) = Local(new PointD(r.Left, r.Bottom));
            _sb.Append(F(x)).Append(' ').Append(F(y)).Append(' ')
               .Append(F(r.Width)).Append(' ').Append(F(r.Height)).Append(" re ").Append(paintOp).Append('\n');
        }

        /// <summary>Ellipse inscribed in <paramref name="r"/> as four Bézier quadrants.</summary>
        public void Ellipse(RectD r)
        {
            const double k = 0.5522847498;
            double cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2;
            double rx = r.Width / 2, ry = r.Height / 2;
            MoveTo(new PointD(cx + rx, cy));
            CurveTo(new PointD(cx + rx, cy + k * ry), new PointD(cx + k * rx, cy + ry), new PointD(cx, cy + ry));
            CurveTo(new PointD(cx - k * rx, cy + ry), new PointD(cx - rx, cy + k * ry), new PointD(cx - rx, cy));
            CurveTo(new PointD(cx - rx, cy - k * ry), new PointD(cx - k * rx, cy - ry), new PointD(cx, cy - ry));
            CurveTo(new PointD(cx + k * rx, cy - ry), new PointD(cx + rx, cy - k * ry), new PointD(cx + rx, cy));
            _sb.Append("h\n");
        }

        /// <summary>The same midpoint-quadratic smoothing the on-screen pen uses (StrokeSmoothing),
        /// written as cubic Béziers, then stroked.</summary>
        public void SmoothStroke(IReadOnlyList<PointD> pts)
        {
            if (pts.Count == 0) return;
            MoveTo(pts[0]);
            if (pts.Count == 1)
            {
                LineTo(pts[0]); // a dot: zero-length segment with a round cap
            }
            else if (pts.Count == 2)
            {
                LineTo(pts[1]);
            }
            else
            {
                var start = Mid(pts[0], pts[1]);
                LineTo(start);
                for (int i = 1; i < pts.Count - 1; i++)
                {
                    var ctrl = pts[i];
                    var end = Mid(pts[i], pts[i + 1]);
                    var c1 = new PointD(start.X + 2.0 / 3 * (ctrl.X - start.X), start.Y + 2.0 / 3 * (ctrl.Y - start.Y));
                    var c2 = new PointD(end.X + 2.0 / 3 * (ctrl.X - end.X), end.Y + 2.0 / 3 * (ctrl.Y - end.Y));
                    CurveTo(c1, c2, end);
                    start = end;
                }
                LineTo(pts[^1]);
            }
            _sb.Append("S\n");
        }

        private static PointD Mid(PointD a, PointD b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

        public byte[] ToBytes() => Encoding.ASCII.GetBytes(_sb.ToString());
    }
}
