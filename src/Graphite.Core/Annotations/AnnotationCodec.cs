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
            double pageH = page.Height.Point;
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

                if (dict.Elements.GetArray("/Rect") is { } r && r.Elements.Count == 4)
                {
                    double x1 = ToDouble(r.Elements[0]), y1 = ToDouble(r.Elements[1]);
                    double x2 = ToDouble(r.Elements[2]), y2 = ToDouble(r.Elements[3]);
                    ann.Bounds = RectD.FromCorners(x1, pageH - y1, x2, pageH - y2);
                }

                if (dict.Elements.GetDictionary("/BS") is { } bs)
                {
                    double bw = bs.Elements.GetReal("/W");
                    if (bw > 0) ann.StrokeWidth = bw;
                }

                if (dict.Elements.GetArray("/QuadPoints") is { } q)
                    for (int i = 0; i + 7 < q.Elements.Count; i += 8)
                    {
                        double left = ToDouble(q.Elements[i]);
                        double top = pageH - ToDouble(q.Elements[i + 1]);
                        double right = ToDouble(q.Elements[i + 2]);
                        double bottom = pageH - ToDouble(q.Elements[i + 5]);
                        ann.Quads.Add(RectD.FromCorners(left, top, right, bottom));
                    }

                if (kind == AnnotationKind.Line &&
                    dict.Elements.GetArray("/L") is { } line && line.Elements.Count >= 4)
                {
                    ann.LineStart = new PointD(ToDouble(line.Elements[0]), pageH - ToDouble(line.Elements[1]));
                    ann.LineEnd = new PointD(ToDouble(line.Elements[2]), pageH - ToDouble(line.Elements[3]));
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
                                pts.Add(new PointD(ToDouble(stroke.Elements[i]), pageH - ToDouble(stroke.Elements[i + 1])));
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

        // 2. Rewrite from model.
        foreach (var group in annotations.GroupBy(a => a.PageIndex))
        {
            if (group.Key < 0 || group.Key >= doc.PageCount) continue;
            var page = doc.Pages[group.Key];
            double pageH = page.Height.Point;
            var annots = GetOrCreateAnnots(doc, page);

            foreach (var ann in group)
            {
                var dict = BuildDict(doc, ann, pageH);
                doc.Internals.AddObject(dict);
                annots.Elements.Add(dict.Reference!);

                foreach (var reply in ann.Replies)
                {
                    var rd = new PdfDictionary(doc);
                    rd.Elements.SetName("/Type", "/Annot");
                    rd.Elements.SetName("/Subtype", "/Text");
                    rd.Elements["/Rect"] = NumArray(doc, ann.Bounds.Left, pageH - ann.Bounds.Bottom,
                                                        ann.Bounds.Left + 18, pageH - ann.Bounds.Bottom + 18);
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

        using var ms = new MemoryStream();
        doc.Save(ms, closeStream: false);
        return ms.ToArray();
    }

    private static PdfDictionary BuildDict(PdfDocument doc, Annotation ann, double pageH)
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

        var b = ann.Bounds;
        dict.Elements["/Rect"] = NumArray(doc, b.Left, pageH - b.Bottom, b.Right, pageH - b.Top);

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
                // PDF order: TL TR BL BR, y-up.
                foreach (double v in new[]
                {
                    qd.Left, pageH - qd.Top, qd.Right, pageH - qd.Top,
                    qd.Left, pageH - qd.Bottom, qd.Right, pageH - qd.Bottom
                })
                    quads.Elements.Add(new PdfReal(v));
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
                    arr.Elements.Add(new PdfReal(pt.X));
                    arr.Elements.Add(new PdfReal(pageH - pt.Y));
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
            string baseFont = (ann.Bold, ann.Italic) switch
            {
                (true, true) => "/HeBO",
                (true, false) => "/HeBo",
                (false, true) => "/HeOb",
                _ => "/Helv",
            };
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
            dict.Elements["/L"] = NumArray(doc,
                ann.LineStart.X, pageH - ann.LineStart.Y,
                ann.LineEnd.X, pageH - ann.LineEnd.Y);
            var le = new PdfArray(doc);
            le.Elements.Add(new PdfName("/None"));
            le.Elements.Add(new PdfName("/OpenArrow"));
            dict.Elements["/LE"] = le;
            var bs = new PdfDictionary(doc);
            bs.Elements.SetReal("/W", ann.StrokeWidth);
            dict.Elements["/BS"] = bs;
        }

        return dict;
    }

    // ---------------------------------------------------------------- helpers

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

    private static string FormatDate(DateTime dt) => $"D:{dt:yyyyMMddHHmmss}";

    private static DateTime ParseDate(string? s)
    {
        if (string.IsNullOrEmpty(s)) return DateTime.Now;
        s = s.StartsWith("D:") ? s[2..] : s;
        return DateTime.TryParseExact(s.Length >= 14 ? s[..14] : s, "yyyyMMddHHmmss",
            null, System.Globalization.DateTimeStyles.None, out var dt) ? dt : DateTime.Now;
    }
}
