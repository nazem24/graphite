using Graphite.Core.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace Graphite.Core.Annotations;

/// <summary>
/// Reads and writes annotations to/from the PDF itself (no sidecar files),
/// so markup made in Graphite is visible in any other PDF viewer.
///
/// Strategy: on save, all annotations of the subtypes Graphite manages are removed
/// and rewritten from the in-memory model. Links and unknown subtypes are untouched.
///
/// Coordinates go through <see cref="PageGeometry"/>: the model works in the page as displayed
/// (CropBox, /Rotate applied, top-left origin), the file in PDF user space. Every annotation is
/// written with an appearance stream (<see cref="AppearanceBuilder"/>) so it renders the same in
/// every viewer.
/// </summary>
public static class AnnotationCodec
{
    private static readonly HashSet<string> ManagedSubtypes = new()
    {
        "/Highlight", "/Underline", "/StrikeOut", "/Ink", "/Square", "/Circle", "/Text",
        "/FreeText", "/Line"
    };

    // ---------------------------------------------------------------- read

    public static List<Annotation> Read(byte[] pdf)
    {
        var result = new List<Annotation>();
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);

        for (int p = 0; p < doc.PageCount; p++)
        {
            var page = doc.Pages[p];
            var geo = PageGeometry.Of(page);
            var annots = page.Elements.GetArray("/Annots");
            if (annots == null) continue;

            var byDict = new Dictionary<PdfDictionary, Annotation>();
            var replies = new List<(PdfDictionary Parent, AnnotationReply Reply)>();

            foreach (var item in annots.Elements)
            {
                if (Resolve(item) is not PdfDictionary dict) continue;
                string? subtype = dict.Elements.GetName("/Subtype");
                if (subtype == null || !ManagedSubtypes.Contains(subtype)) continue;

                // Reply to another annotation?
                if (dict.Elements.ContainsKey("/IRT"))
                {
                    if (Resolve(dict.Elements["/IRT"]) is PdfDictionary parent)
                        replies.Add((parent, new AnnotationReply
                        {
                            Author = dict.Elements.GetString("/T"),
                            Text = dict.Elements.GetString("/Contents"),
                            Modified = ParseDate(dict.Elements.GetString("/M")),
                        }));
                    continue;
                }

                var kind = subtype switch
                {
                    "/Highlight" => AnnotationKind.Highlight,
                    "/Underline" => AnnotationKind.Underline,
                    "/StrikeOut" => AnnotationKind.StrikeOut,
                    "/Ink" => AnnotationKind.Ink,
                    "/Square" => AnnotationKind.Square,
                    "/Circle" => AnnotationKind.Circle,
                    "/FreeText" => AnnotationKind.FreeText,
                    "/Line" => AnnotationKind.Line,
                    _ => AnnotationKind.Note,
                };

                var ann = new Annotation
                {
                    Kind = kind,
                    PageIndex = p,
                    Author = dict.Elements.GetString("/T"),
                    Contents = dict.Elements.GetString("/Contents"),
                    Modified = ParseDate(dict.Elements.GetString("/M")),
                    Opacity = dict.Elements.ContainsKey("/CA") ? dict.Elements.GetReal("/CA") : 1.0,
                };

                if (dict.Elements.GetArray("/C") is { } c && c.Elements.Count >= 3)
                    ann.ColorHex = Annotation.ToHex(ToDouble(c.Elements[0]), ToDouble(c.Elements[1]), ToDouble(c.Elements[2]));

                // Graphite pads /Rect so the appearance has room for stroke widths and arrow
                // heads; the model's own bounds travel in a private key. Other apps' annotations
                // only have /Rect.
                if ((PdfValues.Box(dict.Elements["/Grph:Rect"]) ?? PdfValues.Box(dict.Elements["/Rect"])) is { } r)
                    ann.Bounds = geo.ToDisplayRect(r.X1, r.Y1, r.X2, r.Y2);

                if (dict.Elements.GetDictionary("/BS") is { } bs)
                {
                    double bw = bs.Elements.GetReal("/W");
                    if (bw > 0) ann.StrokeWidth = bw;
                }

                if (dict.Elements.GetArray("/QuadPoints") is { } q)
                    for (int i = 0; i + 7 < q.Elements.Count; i += 8)
                    {
                        // Writers disagree on the corner order inside a quad, so take the
                        // bounding box of all four corners (in display space).
                        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
                        for (int k = 0; k < 8; k += 2)
                        {
                            var pt = geo.ToDisplay(ToDouble(q.Elements[i + k]), ToDouble(q.Elements[i + k + 1]));
                            minX = Math.Min(minX, pt.X); maxX = Math.Max(maxX, pt.X);
                            minY = Math.Min(minY, pt.Y); maxY = Math.Max(maxY, pt.Y);
                        }
                        ann.Quads.Add(RectD.FromCorners(minX, minY, maxX, maxY));
                    }

                if (kind == AnnotationKind.Line &&
                    dict.Elements.GetArray("/L") is { } line && line.Elements.Count >= 4)
                {
                    ann.LineStart = geo.ToDisplay(ToDouble(line.Elements[0]), ToDouble(line.Elements[1]));
                    ann.LineEnd = geo.ToDisplay(ToDouble(line.Elements[2]), ToDouble(line.Elements[3]));
                }

                if (kind == AnnotationKind.FreeText)
                {
                    // Recover the font size from the default-appearance string ("… 12 Tf …").
                    string da = dict.Elements.GetString("/DA");
                    var m = System.Text.RegularExpressions.Regex.Match(da ?? "", @"([\d.]+)\s+Tf");
                    if (m.Success && double.TryParse(m.Groups[1].Value,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double fs) && fs > 0)
                        ann.FontSize = fs;

                    string face = dict.Elements.GetString("/Grph:Font");
                    if (!string.IsNullOrEmpty(face)) ann.FontFamily = face;
                    ann.Bold = GetBool(dict, "/Grph:Bold");
                    ann.Italic = GetBool(dict, "/Grph:Italic");
                    ann.Underline = GetBool(dict, "/Grph:Underline");

                    string fill = dict.Elements.GetString("/Grph:Fill");
                    if (!string.IsNullOrEmpty(fill)) ann.FillColorHex = fill;

                    string border = dict.Elements.GetString("/Grph:Border");
                    if (!string.IsNullOrEmpty(border)) ann.BorderColorHex = border;
                }

                // A freehand highlight is written as an Ink annotation tagged with a
                // private marker key so it round-trips as Highlight in Graphite while
                // still rendering sensibly (as ink) in any other viewer.
                if (kind == AnnotationKind.Ink && GetBool(dict, "/Grph:FreehandHighlight"))
                {
                    kind = AnnotationKind.Highlight;
                    ann = new Annotation
                    {
                        Kind = kind,
                        PageIndex = ann.PageIndex,
                        Author = ann.Author,
                        Contents = ann.Contents,
                        Modified = ann.Modified,
                        Opacity = ann.Opacity,
                        ColorHex = ann.ColorHex,
                        Bounds = ann.Bounds,
                        StrokeWidth = ann.StrokeWidth,
                        IsFreehand = true,
                    };
                }

                if (dict.Elements.GetArray("/InkList") is { } ink)
                    foreach (var strokeItem in ink.Elements)
                        if (Resolve(strokeItem) is PdfArray stroke)
                        {
                            var pts = new List<PointD>();
                            for (int i = 0; i + 1 < stroke.Elements.Count; i += 2)
                                pts.Add(geo.ToDisplay(ToDouble(stroke.Elements[i]), ToDouble(stroke.Elements[i + 1])));
                            ann.Strokes.Add(pts);
                        }

                byDict[dict] = ann;
                result.Add(ann);
            }

            foreach (var (parent, reply) in replies)
                if (byDict.TryGetValue(parent, out var target))
                    target.Replies.Add(reply);
        }

        return result;
    }

    // ---------------------------------------------------------------- write

    public static byte[] Write(byte[] pdf, IReadOnlyList<Annotation> annotations)
    {
        using var doc = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Modify);

        // 1. Remove every managed annotation (they are rewritten below).
        for (int p = 0; p < doc.PageCount; p++)
        {
            var annots = doc.Pages[p].Elements.GetArray("/Annots");
            if (annots == null) continue;
            for (int i = annots.Elements.Count - 1; i >= 0; i--)
            {
                if (Resolve(annots.Elements[i]) is PdfDictionary dict)
                {
                    string? sub = dict.Elements.GetName("/Subtype");
                    if (sub != null && ManagedSubtypes.Contains(sub))
                        annots.Elements.RemoveAt(i);
                }
            }
        }

        // Standard fonts for FreeText appearances, registered once in the AcroForm resources
        // (where the /DA strings expect them) and shared by every appearance stream.
        PdfDictionary? fontDicts = null;
        PdfDictionary fonts(string name)
        {
            fontDicts ??= EnsureDefaultFontResources(doc);
            return PdfValues.Dict(fontDicts.Elements[name]) ?? throw new InvalidOperationException($"Font {name} missing.");
        }

        // 2. Rewrite from model.
        foreach (var group in annotations.GroupBy(a => a.PageIndex))
        {
            if (group.Key < 0 || group.Key >= doc.PageCount) continue;
            var page = doc.Pages[group.Key];
            var geo = PageGeometry.Of(page);
            var annots = GetOrCreateAnnots(doc, page);

            foreach (var ann in group)
            {
                var dict = BuildDict(doc, ann, geo, fonts);
                doc.Internals.AddObject(dict);
                annots.Elements.Add(dict.Reference!);

                foreach (var reply in ann.Replies)
                {
                    var rd = new PdfDictionary(doc);
                    rd.Elements.SetName("/Type", "/Annot");
                    rd.Elements.SetName("/Subtype", "/Text");
                    var (rx1, ry1, rx2, ry2) = geo.ToUserRect(new RectD(ann.Bounds.Left, ann.Bounds.Bottom - 18, 18, 18));
                    rd.Elements["/Rect"] = NumArray(doc, rx1, ry1, rx2, ry2);
                    rd.Elements.SetString("/Contents", reply.Text);
                    rd.Elements.SetString("/T", reply.Author);
                    rd.Elements.SetString("/M", FormatDate(reply.Modified));
                    rd.Elements.SetInteger("/F", 4);
                    rd.Elements["/IRT"] = dict.Reference!;
                    doc.Internals.AddObject(rd);
                    annots.Elements.Add(rd.Reference!);
                }
            }
        }

        // FreeText /DA strings reference font resource names (/Helv etc.) — those names
        // must resolve to real font dictionaries or strict viewers can't rebuild an
        // appearance for the text boxes.
        if (annotations.Any(a => a.Kind == AnnotationKind.FreeText))
            fontDicts ??= EnsureDefaultFontResources(doc);

        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static PdfDictionary BuildDict(PdfDocument doc, Annotation ann, PageGeometry geo,
        Func<string, PdfDictionary> fonts)
    {
        // A freehand highlight has no word quads to snap to, so it's written as an
        // Ink annotation (valid, well-supported everywhere) tagged with a private
        // marker key — see the matching check in Read().
        bool freehandHighlight = ann.Kind == AnnotationKind.Highlight && ann.IsFreehand;

        var dict = new PdfDictionary(doc);
        dict.Elements.SetName("/Type", "/Annot");
        dict.Elements.SetName("/Subtype", freehandHighlight ? "/Ink" : ann.Kind switch
        {
            AnnotationKind.Highlight => "/Highlight",
            AnnotationKind.Underline => "/Underline",
            AnnotationKind.StrikeOut => "/StrikeOut",
            AnnotationKind.Ink => "/Ink",
            AnnotationKind.Square => "/Square",
            AnnotationKind.Circle => "/Circle",
            AnnotationKind.FreeText => "/FreeText",
            AnnotationKind.Line => "/Line",
            _ => "/Text",
        });

        // /Rect covers everything the appearance draws (stroke widths, arrow heads); the
        // model's exact bounds are kept alongside so reading the file back is lossless.
        var extent = AppearanceBuilder.Extent(ann);
        int apRotation = geo.Rotation;
        if (ann.Kind == AnnotationKind.Note)
        {
            // Viewers keep sticky-note icons upright (no rotation, no zoom) and pin them by the
            // /Rect's upper-left corner in user space. Put that corner where the icon's top-left
            // shows on the displayed page, and draw the icon unrotated.
            var (px, py) = geo.ToUser(extent.Left, extent.Top);
            dict.Elements["/Rect"] = NumArray(doc, px, py - extent.Height, px + extent.Width, py);
            apRotation = 0;
        }
        else
        {
            var (ex1, ey1, ex2, ey2) = geo.ToUserRect(extent);
            dict.Elements["/Rect"] = NumArray(doc, ex1, ey1, ex2, ey2);
        }
        var (bx1, by1, bx2, by2) = geo.ToUserRect(ann.Bounds);
        dict.Elements["/Grph:Rect"] = NumArray(doc, bx1, by1, bx2, by2);

        var (r, g, bl) = Annotation.ParseColor(ann.ColorHex);
        dict.Elements["/C"] = NumArray(doc, r, g, bl);
        if (ann.Opacity < 1.0) dict.Elements.SetReal("/CA", ann.Opacity);

        if (!string.IsNullOrEmpty(ann.Contents)) dict.Elements.SetString("/Contents", ann.Contents);
        dict.Elements.SetString("/T", ann.Author);
        dict.Elements.SetString("/M", FormatDate(ann.Modified));
        dict.Elements.SetInteger("/F", 4); // print

        if (freehandHighlight) dict.Elements.SetBoolean("/Grph:FreehandHighlight", true);

        if (!freehandHighlight && ann.Kind is AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut)
        {
            var quads = new PdfArray(doc);
            foreach (var qd in ann.Quads)
            {
                // Corner order as the reader sees the text: top-left, top-right, bottom-left,
                // bottom-right (Acrobat's order), each mapped into user space.
                foreach (var corner in new[]
                {
                    new PointD(qd.Left, qd.Top), new PointD(qd.Right, qd.Top),
                    new PointD(qd.Left, qd.Bottom), new PointD(qd.Right, qd.Bottom),
                })
                {
                    var (ux, uy) = geo.ToUser(corner);
                    quads.Elements.Add(new PdfReal(ux));
                    quads.Elements.Add(new PdfReal(uy));
                }
            }
            dict.Elements["/QuadPoints"] = quads;
        }

        if (ann.Kind == AnnotationKind.Ink || freehandHighlight)
        {
            var ink = new PdfArray(doc);
            foreach (var stroke in ann.Strokes)
            {
                var arr = new PdfArray(doc);
                foreach (var pt in stroke)
                {
                    var (ux, uy) = geo.ToUser(pt);
                    arr.Elements.Add(new PdfReal(ux));
                    arr.Elements.Add(new PdfReal(uy));
                }
                ink.Elements.Add(arr);
            }
            dict.Elements["/InkList"] = ink;
            var bs = new PdfDictionary(doc);
            bs.Elements.SetReal("/W", ann.StrokeWidth);
            dict.Elements["/BS"] = bs;
        }

        if (ann.Kind is AnnotationKind.Square or AnnotationKind.Circle
            or AnnotationKind.Underline or AnnotationKind.StrikeOut)
        {
            var bs = new PdfDictionary(doc);
            bs.Elements.SetReal("/W", ann.StrokeWidth);
            dict.Elements["/BS"] = bs;
        }

        if (ann.Kind == AnnotationKind.Note)
        {
            dict.Elements.SetName("/Name", "/Comment");
            dict.Elements.SetBoolean("/Open", false);
        }

        if (ann.Kind == AnnotationKind.FreeText)
        {
            var (fr, fg, fb) = Annotation.ParseColor(ann.ColorHex);
            // Pick the closest standard-14 base font for other viewers' default appearance.
            string baseFont = AppearanceBuilder.FreeTextFontName(ann.Bold, ann.Italic);
            string da = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{baseFont} {ann.FontSize:0.##} Tf {fr:0.###} {fg:0.###} {fb:0.###} rg");
            dict.Elements.SetString("/DA", da);
            dict.Elements.SetInteger("/Q", 0); // left-aligned

            if (!string.IsNullOrEmpty(ann.FillColorHex))
            {
                var (ir, ig, ib) = Annotation.ParseColor(ann.FillColorHex);
                dict.Elements["/IC"] = NumArray(doc, ir, ig, ib);
            }
            if (!string.IsNullOrEmpty(ann.BorderColorHex))
            {
                var bs = new PdfDictionary(doc);
                bs.Elements.SetReal("/W", Math.Max(0.75, ann.StrokeWidth));
                dict.Elements["/BS"] = bs;
            }

            // Private keys so Graphite round-trips the full formatting exactly;
            // other viewers ignore these and fall back to /DA (+ /IC if present).
            dict.Elements.SetString("/Grph:Font", ann.FontFamily);
            dict.Elements.SetBoolean("/Grph:Bold", ann.Bold);
            dict.Elements.SetBoolean("/Grph:Italic", ann.Italic);
            dict.Elements.SetBoolean("/Grph:Underline", ann.Underline);
            if (!string.IsNullOrEmpty(ann.FillColorHex)) dict.Elements.SetString("/Grph:Fill", ann.FillColorHex);
            if (!string.IsNullOrEmpty(ann.BorderColorHex)) dict.Elements.SetString("/Grph:Border", ann.BorderColorHex);
        }

        if (ann.Kind == AnnotationKind.Line)
        {
            var (lx1, ly1) = geo.ToUser(ann.LineStart);
            var (lx2, ly2) = geo.ToUser(ann.LineEnd);
            dict.Elements["/L"] = NumArray(doc, lx1, ly1, lx2, ly2);
            var le = new PdfArray(doc);
            le.Elements.Add(new PdfName("/None"));
            le.Elements.Add(new PdfName("/OpenArrow"));
            dict.Elements["/LE"] = le;
            var bs = new PdfDictionary(doc);
            bs.Elements.SetReal("/W", ann.StrokeWidth);
            dict.Elements["/BS"] = bs;
        }

        // Appearance stream: the markup looks the same in every viewer (and prints through PDFium).
        if (AppearanceBuilder.Build(doc, ann, extent, apRotation, fonts) is { } ap)
            dict.Elements["/AP"] = ap;

        return dict;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Register the base-14 Helvetica faces under the resource names our
    /// FreeText /DA strings use (/Helv, /HeBo, /HeOb, /HeBO) in the document's
    /// AcroForm default resources, so every viewer can resolve them.</summary>
    /// <returns>The /DR /Font dictionary.</returns>
    internal static PdfDictionary EnsureDefaultFontResources(PdfDocument doc)
    {
        var catalog = doc.Internals.Catalog;

        var acroForm = catalog.Elements.GetDictionary("/AcroForm");
        if (acroForm == null)
        {
            acroForm = new PdfDictionary(doc);
            acroForm.Elements["/Fields"] = new PdfArray(doc);
            doc.Internals.AddObject(acroForm);
            catalog.Elements["/AcroForm"] = acroForm.Reference;
        }

        var dr = acroForm.Elements.GetDictionary("/DR");
        if (dr == null)
        {
            dr = new PdfDictionary(doc);
            doc.Internals.AddObject(dr);
            acroForm.Elements["/DR"] = dr.Reference;
        }

        var fonts = dr.Elements.GetDictionary("/Font");
        if (fonts == null)
        {
            fonts = new PdfDictionary(doc);
            doc.Internals.AddObject(fonts);
            dr.Elements["/Font"] = fonts.Reference;
        }

        foreach (var (name, baseFont) in new[]
        {
            ("/Helv", "/Helvetica"),
            ("/HeBo", "/Helvetica-Bold"),
            ("/HeOb", "/Helvetica-Oblique"),
            ("/HeBO", "/Helvetica-BoldOblique"),
        })
        {
            if (fonts.Elements.ContainsKey(name)) continue;
            var font = new PdfDictionary(doc);
            font.Elements.SetName("/Type", "/Font");
            font.Elements.SetName("/Subtype", "/Type1");
            font.Elements.SetName("/BaseFont", baseFont);
            font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
            doc.Internals.AddObject(font);
            fonts.Elements[name] = font.Reference;
        }
        return fonts;
    }

    private static PdfArray GetOrCreateAnnots(PdfDocument doc, PdfPage page)
    {
        if (page.Elements.GetArray("/Annots") is { } existing) return existing;
        var arr = new PdfArray(doc);
        page.Elements["/Annots"] = arr;
        return arr;
    }

    private static PdfArray NumArray(PdfDocument doc, params double[] values)
    {
        var arr = new PdfArray(doc);
        foreach (double v in values) arr.Elements.Add(new PdfReal(v));
        return arr;
    }

    private static PdfItem? Resolve(PdfItem? item) =>
        item is PdfReference r ? r.Value : item;

    /// <summary>Reads a boolean dictionary entry directly (rather than relying on a
    /// Get/Set convenience pair that may not exist symmetrically across PdfSharp versions).</summary>
    private static bool GetBool(PdfDictionary dict, string key) =>
        dict.Elements.ContainsKey(key) && Resolve(dict.Elements[key]) is PdfBoolean { Value: true };

    private static double ToDouble(PdfItem item) => item switch
    {
        PdfReal r => r.Value,
        PdfInteger i => i.Value,
        PdfReference rf => ToDouble(rf.Value),
        _ => 0,
    };

    /// <summary>PDF date with local UTC offset (D:yyyyMMddHHmmss+02'00'), so the
    /// timestamp is unambiguous to other viewers.</summary>
    private static string FormatDate(DateTime dt)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(dt);
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        var abs = offset.Duration();
        return $"D:{dt:yyyyMMddHHmmss}{sign}{abs.Hours:00}'{abs.Minutes:00}'";
    }

    private static DateTime ParseDate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return DateTime.Now;
        s = s.StartsWith("D:") ? s[2..] : s;
        return DateTime.TryParseExact(s.Length >= 14 ? s[..14] : s, "yyyyMMddHHmmss",
            null, System.Globalization.DateTimeStyles.None, out var dt) ? dt : DateTime.Now;
    }
}
