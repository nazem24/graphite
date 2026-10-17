using System.Runtime.CompilerServices;
using Graphite.Core;
using Graphite.Core.Annotations;
using Graphite.Core.Forms;
using Graphite.Core.Pdf;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace Graphite.Tests;

/// <summary>Writes test output PDFs to verify-out/ when GRAPHITE_DUMP is set, for eyeballing.</summary>
internal static class Dump
{
    public static void Write(string name, byte[] pdf)
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GRAPHITE_DUMP"))) return;
        string dir = Path.Combine(RepoRoot(), "verify-out");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), pdf);
    }

    private static string RepoRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", ".."));
}

public class PageGeometryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void DisplayAndUserSpaceRoundTrip(int rotation)
    {
        var geo = new PageGeometry(10, 20, 610, 820, rotation);
        var p = new PointD(37, 112);
        var (x, y) = geo.ToUser(p);
        var back = geo.ToDisplay(x, y);
        Assert.Equal(p.X, back.X, 6);
        Assert.Equal(p.Y, back.Y, 6);

        // The matrix form agrees with the point form.
        var (a, b, c, d, e, f) = geo.DisplayToUserMatrix();
        Assert.Equal(x, a * p.X + c * p.Y + e, 6);
        Assert.Equal(y, b * p.X + d * p.Y + f, 6);
    }

    [Fact]
    public void RotatedPageSwapsDisplaySize()
    {
        var geo = new PageGeometry(0, 0, 600, 800, 90);
        Assert.Equal(800, geo.DisplayWidth);
        Assert.Equal(600, geo.DisplayHeight);
        // Rotated 90° clockwise: the user-space origin (bottom-left) shows up top-left.
        var origin = geo.ToDisplay(0, 0);
        Assert.Equal(0, origin.X, 6);
        Assert.Equal(0, origin.Y, 6);
        // Top-left of the unrotated page (0, 800) ends up top-right.
        var tl = geo.ToDisplay(0, 800);
        Assert.Equal(800, tl.X, 6);
        Assert.Equal(0, tl.Y, 6);
    }

    [Fact]
    public void NormalizeSnapsAnyRotation()
    {
        Assert.Equal(270, PageGeometry.Normalize(-90));
        Assert.Equal(0, PageGeometry.Normalize(360));
        Assert.Equal(90, PageGeometry.Normalize(450));
    }

    [Fact]
    public void ReadsCropBoxAndRotateFromPage()
    {
        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(600);
        page.Height = XUnit.FromPoint(800);
        page.Elements["/CropBox"] = AppearanceNumbers(doc, 50, 60, 550, 760);
        page.Elements.SetInteger("/Rotate", 270);
        using var ms = new MemoryStream();
        doc.Save(ms, false);

        using var reopened = PdfReader.Open(new MemoryStream(ms.ToArray()), PdfDocumentOpenMode.Import);
        var geo = PageGeometry.Of(reopened.Pages[0]);
        Assert.Equal(270, geo.Rotation);
        Assert.Equal(50, geo.Llx, 3);
        Assert.Equal(60, geo.Lly, 3);
        Assert.Equal(500, geo.UserWidth, 3);
        Assert.Equal(700, geo.UserHeight, 3);
    }

    internal static PdfArray AppearanceNumbers(PdfDocument doc, params double[] v)
    {
        var arr = new PdfArray(doc);
        foreach (double d in v) arr.Elements.Add(new PdfReal(d));
        return arr;
    }
}

public class RotatedAnnotationTests
{
    private static byte[] RotatedPdf(int rotation)
    {
        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(600);
        page.Height = XUnit.FromPoint(800);
        page.Elements.SetInteger("/Rotate", rotation);
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void MarkupRoundTripsInDisplaySpace(int rotation)
    {
        var pdf = RotatedPdf(rotation);
        var anns = new List<Annotation>
        {
            new() { Kind = AnnotationKind.Highlight, PageIndex = 0, Bounds = new RectD(40, 50, 120, 14),
                    Quads = { new RectD(40, 50, 120, 14) }, Opacity = 0.8 },
            new() { Kind = AnnotationKind.Ink, PageIndex = 0, Bounds = new RectD(100, 200, 80, 40), StrokeWidth = 2,
                    Strokes = { new List<PointD> { new(100, 200), new(140, 240), new(180, 210) } } },
            new() { Kind = AnnotationKind.Line, PageIndex = 0, Bounds = new RectD(300, 300, 100, 50),
                    LineStart = new PointD(300, 300), LineEnd = new PointD(400, 350), StrokeWidth = 2 },
            new() { Kind = AnnotationKind.FreeText, PageIndex = 0, Bounds = new RectD(60, 400, 220, 60),
                    Contents = "Grüezi — text box on a rotated page, wrapping onto a second line.", FontSize = 12,
                    BorderColorHex = "#C24444", FillColorHex = "#FFF7D6", ColorHex = "#1B1B1A" },
            new() { Kind = AnnotationKind.Square, PageIndex = 0, Bounds = new RectD(320, 420, 120, 80), StrokeWidth = 1.6, ColorHex = "#C24444" },
            new() { Kind = AnnotationKind.Note, PageIndex = 0, Bounds = new RectD(500, 60, 20, 20), Contents = "note" },
        };

        var written = AnnotationCodec.Write(pdf, anns);
        Dump.Write($"rotated-{rotation}.pdf", written);
        var read = AnnotationCodec.Read(written);

        Assert.Equal(anns.Count, read.Count);
        foreach (var (a, b) in anns.Zip(read))
        {
            Assert.Equal(a.Kind, b.Kind);
            AssertRect(a.Bounds, b.Bounds);
        }
        AssertRect(anns[0].Quads[0], read[0].Quads[0]);
        Assert.Equal(anns[1].Strokes[0][1].X, read[1].Strokes[0][1].X, 2);
        Assert.Equal(anns[1].Strokes[0][1].Y, read[1].Strokes[0][1].Y, 2);
        Assert.Equal(anns[2].LineEnd.X, read[2].LineEnd.X, 2);
        Assert.Equal(anns[2].LineEnd.Y, read[2].LineEnd.Y, 2);

        // Every annotation carries an appearance stream.
        using var doc = PdfReader.Open(new MemoryStream(written), PdfDocumentOpenMode.Import);
        var annots = doc.Pages[0].Elements.GetArray("/Annots")!;
        foreach (var item in annots.Elements)
        {
            var dict = PdfValues.Dict(item)!;
            Assert.NotNull(PdfValues.Dict(PdfValues.Dict(dict.Elements["/AP"])?.Elements["/N"]));
        }
    }

    private static void AssertRect(RectD expected, RectD actual)
    {
        Assert.Equal(expected.X, actual.X, 2);
        Assert.Equal(expected.Y, actual.Y, 2);
        Assert.Equal(expected.Width, actual.Width, 2);
        Assert.Equal(expected.Height, actual.Height, 2);
    }
}

public class FormCodecTests
{
    /// <summary>A one-page form: text, multiline text, check box (without appearances), a
    /// two-button radio group, a combo box and a read-only field.</summary>
    internal static byte[] SampleForm(int rotation = 0)
    {
        var doc = new PdfDocument();
        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(600);
        page.Height = XUnit.FromPoint(800);
        if (rotation != 0) page.Elements.SetInteger("/Rotate", rotation);
        var annots = new PdfArray(doc);
        page.Elements["/Annots"] = annots;
        var fields = new PdfArray(doc);

        PdfDictionary Add(PdfDictionary d, bool topLevel = true)
        {
            doc.Internals.AddObject(d);
            if (topLevel) fields.Elements.Add(d.Reference!);
            return d;
        }

        PdfDictionary Widget(double x1, double y1, double x2, double y2)
        {
            var w = new PdfDictionary(doc);
            w.Elements.SetName("/Type", "/Annot");
            w.Elements.SetName("/Subtype", "/Widget");
            w.Elements["/Rect"] = PageGeometryTests.AppearanceNumbers(doc, x1, y1, x2, y2);
            var mk = new PdfDictionary(doc);
            mk.Elements["/BC"] = PageGeometryTests.AppearanceNumbers(doc, 0.3, 0.3, 0.3);
            w.Elements["/MK"] = mk;
            w.Elements["/P"] = page.Reference!;
            return w;
        }

        var name = Widget(100, 700, 400, 724);
        name.Elements.SetName("/FT", "/Tx");
        name.Elements.SetString("/T", "name");
        name.Elements.SetString("/TU", "Your full name");
        name.Elements.SetString("/DA", "/Helv 0 Tf 0 g");
        annots.Elements.Add(Add(name).Reference!);

        var notes = Widget(100, 560, 400, 660);
        notes.Elements.SetName("/FT", "/Tx");
        notes.Elements.SetString("/T", "notes");
        notes.Elements.SetInteger("/Ff", 1 << 12);
        notes.Elements.SetString("/DA", "/Helv 10 Tf 0 0 0.6 rg");
        annots.Elements.Add(Add(notes).Reference!);

        var agree = Widget(100, 520, 116, 536);
        agree.Elements.SetName("/FT", "/Btn");
        agree.Elements.SetString("/T", "agree");
        agree.Elements.SetName("/V", "/Off");
        annots.Elements.Add(Add(agree).Reference!);

        var size = new PdfDictionary(doc);
        size.Elements.SetName("/FT", "/Btn");
        size.Elements.SetString("/T", "size");
        size.Elements.SetInteger("/Ff", 1 << 15);
        var kids = new PdfArray(doc);
        size.Elements["/Kids"] = kids;
        Add(size);
        foreach (var (state, x) in new[] { ("Small", 100.0), ("Large", 160.0) })
        {
            var kid = Widget(x, 480, x + 14, 494);
            kid.Elements["/Parent"] = size.Reference!;
            var ap = new PdfDictionary(doc);
            var n = new PdfDictionary(doc);
            n.Elements["/" + state] = StateStream(doc, "0 g 4 4 6 6 re f");
            n.Elements["/Off"] = StateStream(doc, "");
            ap.Elements["/N"] = n;
            kid.Elements["/AP"] = ap;
            Add(kid, topLevel: false);
            kids.Elements.Add(kid.Reference!);
            annots.Elements.Add(kid.Reference!);
        }

        var canton = Widget(100, 430, 260, 450);
        canton.Elements.SetName("/FT", "/Ch");
        canton.Elements.SetString("/T", "canton");
        canton.Elements.SetInteger("/Ff", 1 << 17);
        var opt = new PdfArray(doc);
        foreach (var (export, display) in new[] { ("SG", "St. Gallen"), ("AI", "Appenzell Innerrhoden"), ("AR", "Appenzell Ausserrhoden") })
        {
            var pair = new PdfArray(doc);
            pair.Elements.Add(new PdfString(export));
            pair.Elements.Add(new PdfString(display));
            opt.Elements.Add(pair);
        }
        canton.Elements["/Opt"] = opt;
        annots.Elements.Add(Add(canton).Reference!);

        var id = Widget(300, 430, 400, 450);
        id.Elements.SetName("/FT", "/Tx");
        id.Elements.SetString("/T", "id");
        id.Elements.SetInteger("/Ff", 1);
        id.Elements.SetString("/V", "fixed");
        annots.Elements.Add(Add(id).Reference!);

        var acro = new PdfDictionary(doc);
        acro.Elements["/Fields"] = fields;
        acro.Elements.SetString("/DA", "/Helv 0 Tf 0 g");
        doc.Internals.AddObject(acro);
        doc.Internals.Catalog.Elements["/AcroForm"] = acro.Reference!;

        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    private static PdfDictionary StateStream(PdfDocument doc, string content)
    {
        var st = new PdfDictionary(doc);
        st.Elements.SetName("/Type", "/XObject");
        st.Elements.SetName("/Subtype", "/Form");
        st.Elements["/BBox"] = PageGeometryTests.AppearanceNumbers(doc, 0, 0, 14, 14);
        st.CreateStream(System.Text.Encoding.ASCII.GetBytes(content));
        doc.Internals.AddObject(st);
        return st;
    }

    [Fact]
    public void ReadsEveryFieldKind()
    {
        Dump.Write("form-empty.pdf", SampleForm());
        var fields = FormCodec.Read(SampleForm());
        Assert.True(FormCodec.HasFields(SampleForm()));

        var byName = fields.ToDictionary(f => f.Name);
        Assert.Equal(FormFieldKind.Text, byName["name"].Kind);
        Assert.Equal("Your full name", byName["name"].Tooltip);
        Assert.True(byName["notes"].Multiline);
        Assert.Equal(10, byName["notes"].FontSize);
        Assert.Equal(FormFieldKind.CheckBox, byName["agree"].Kind);
        Assert.Equal("Off", byName["agree"].Value);
        Assert.Equal(FormFieldKind.Radio, byName["size"].Kind);
        Assert.Equal(new[] { "Small", "Large" }, byName["size"].Widgets.Select(w => w.OnState));
        Assert.Equal(FormFieldKind.ComboBox, byName["canton"].Kind);
        Assert.Equal(3, byName["canton"].Options.Count);
        Assert.True(byName["id"].ReadOnly);
        Assert.False(byName["id"].IsFillable);

        // Display space: top-left origin. The name field's /Rect y 700..724 on an 800pt page.
        var b = byName["name"].Widgets[0].Bounds;
        Assert.Equal(100, b.X, 3);
        Assert.Equal(76, b.Y, 3);
        Assert.Equal(300, b.Width, 3);
        Assert.Equal(24, b.Height, 3);
    }

    [Fact]
    public void FillWritesValuesAndAppearances()
    {
        var filled = FormCodec.Fill(SampleForm(), new Dictionary<string, string>
        {
            ["name"] = "Michael Zihlmann",
            ["notes"] = "First line of a longer answer that has to wrap inside the box.\nSecond paragraph.",
            ["agree"] = "Yes",
            ["size"] = "Large",
            ["canton"] = "AI",
            ["id"] = "should be ignored",
        });
        Dump.Write("form-filled.pdf", filled);

        var byName = FormCodec.Read(filled).ToDictionary(f => f.Name);
        Assert.Equal("Michael Zihlmann", byName["name"].Value);
        Assert.Equal("Yes", byName["agree"].Value);
        Assert.Equal("Large", byName["size"].Value);
        Assert.Equal("AI", byName["canton"].Value);
        Assert.Equal("Appenzell Innerrhoden", byName["canton"].DisplayValue);
        Assert.Equal("fixed", byName["id"].Value);

        using var doc = PdfReader.Open(new MemoryStream(filled), PdfDocumentOpenMode.Import);
        // Graphite's own appearances must be what viewers show, so it doesn't ask them to rebuild.
        var acro = PdfValues.Dict(doc.Internals.Catalog.Elements["/AcroForm"])!;
        Assert.False(acro.Elements.GetBoolean("/NeedAppearances"));
        foreach (var item in doc.Pages[0].Elements.GetArray("/Annots")!.Elements)
        {
            var w = PdfValues.Dict(item)!;
            string? t = PdfValues.Text(w.Elements["/T"]);
            if (t is "name" or "notes" or "canton")
                Assert.NotNull(PdfValues.Dict(PdfValues.Dict(w.Elements["/AP"])?.Elements["/N"]));
            if (t == "agree")
                Assert.Equal("Yes", PdfValues.Name(w.Elements["/AS"]));
        }

        // Switching back off.
        var off = FormCodec.Read(FormCodec.Fill(filled, new Dictionary<string, string> { ["agree"] = "Off", ["size"] = "Small" }))
            .ToDictionary(f => f.Name);
        Assert.Equal("Off", off["agree"].Value);
        Assert.Equal("Small", off["size"].Value);
    }

    [Fact]
    public void FieldsOnRotatedPageMapToDisplaySpace()
    {
        var fields = FormCodec.Read(SampleForm(rotation: 90)).ToDictionary(f => f.Name);
        // /Rect x 100..400, y 700..724 on a 600×800 page rotated 90°: display u = y, v = x.
        var b = fields["name"].Widgets[0].Bounds;
        Assert.Equal(700, b.X, 3);
        Assert.Equal(100, b.Y, 3);
        Assert.Equal(24, b.Width, 3);
        Assert.Equal(300, b.Height, 3);
        Dump.Write("form-rotated-filled.pdf", FormCodec.Fill(SampleForm(rotation: 90),
            new Dictionary<string, string> { ["name"] = "Rotated page" }));
    }

    [Fact]
    public void DocumentWithoutFormHasNoFields()
    {
        var doc = new PdfDocument();
        doc.AddPage();
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        Assert.Empty(FormCodec.Read(ms.ToArray()));
        Assert.False(FormCodec.HasFields(ms.ToArray()));
    }
}
