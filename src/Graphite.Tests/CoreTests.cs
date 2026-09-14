using Graphite.Core;
using Graphite.Core.Annotations;
using Graphite.Core.Export;
using Graphite.Core.Pdf;
using Graphite.Core.Text;
using PdfSharp.Pdf;
using Xunit;

namespace Graphite.Tests;

public class PageRangeTests
{
    [Fact]
    public void ParsesSinglesRangesAndMixes()
    {
        Assert.Equal(new[] { 0, 2, 3, 4, 7 }, PageOperations.ParsePageRanges("1,3-5,8", 10));
        Assert.Equal(new[] { 0 }, PageOperations.ParsePageRanges(" 1 ", 10));
        Assert.Equal(new[] { 0, 1, 2 }, PageOperations.ParsePageRanges("1-3", 5));
    }

    [Fact]
    public void RejectsOutOfRangeAndGarbage()
    {
        Assert.Throws<FormatException>(() => PageOperations.ParsePageRanges("0", 10));
        Assert.Throws<FormatException>(() => PageOperations.ParsePageRanges("11", 10));
        Assert.Throws<FormatException>(() => PageOperations.ParsePageRanges("5-2", 10));
        Assert.Throws<FormatException>(() => PageOperations.ParsePageRanges("1-2-3", 10));
        Assert.Throws<FormatException>(() => PageOperations.ParsePageRanges("abc", 10));
    }
}

public class RectDTests
{
    [Fact]
    public void FromCornersNormalizes()
    {
        var r = RectD.FromCorners(10, 20, 4, 8);
        Assert.Equal(4, r.X);
        Assert.Equal(8, r.Y);
        Assert.Equal(6, r.Width);
        Assert.Equal(12, r.Height);
    }

    [Fact]
    public void IntersectsContainsUnionInflate()
    {
        var a = new RectD(0, 0, 10, 10);
        Assert.True(a.IntersectsWith(new RectD(5, 5, 10, 10)));
        Assert.False(a.IntersectsWith(new RectD(20, 20, 5, 5)));
        Assert.True(a.Contains(new PointD(5, 5)));
        Assert.False(a.Contains(new PointD(15, 5)));

        var u = a.Union(new RectD(5, 5, 10, 10));
        Assert.Equal(new RectD(0, 0, 15, 15), u);

        Assert.Equal(new RectD(-2, -2, 14, 14), a.Inflate(2));
    }
}

public class ColorTests
{
    [Theory]
    [InlineData("#FF0000", 1.0, 0.0, 0.0)]
    [InlineData("#00FF00", 0.0, 1.0, 0.0)]
    [InlineData("#1B3A6B", 27 / 255.0, 58 / 255.0, 107 / 255.0)]
    public void ParsesValidHex(string hex, double r, double g, double b)
    {
        var (pr, pg, pb) = Annotation.ParseColor(hex);
        Assert.Equal(r, pr, 3);
        Assert.Equal(g, pg, 3);
        Assert.Equal(b, pb, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("#12")]
    [InlineData("zzzzzz")]
    [InlineData("#GGHHII")]
    public void MalformedHexFallsBackToBlackInsteadOfThrowing(string hex)
    {
        var (r, g, b) = Annotation.ParseColor(hex);
        Assert.Equal(0, r);
        Assert.Equal(0, g);
        Assert.Equal(0, b);
    }

    [Fact]
    public void HexRoundTrips()
    {
        var (r, g, b) = Annotation.ParseColor("#3E6DB5");
        Assert.Equal("#3E6DB5", Annotation.ToHex(r, g, b));
    }
}

public class WordsInRangeTests
{
    private static PageText MakePage(params string[] words)
    {
        var pt = new PageText { PageIndex = 0, Width = 100, Height = 100 };
        foreach (var w in words)
            pt.Words.Add(new WordBox(w, new RectD(0, 0, 10, 10)));
        pt.RebuildJoined();
        return pt;
    }

    [Fact]
    public void MatchesBruteForce()
    {
        var pt = MakePage("the", "quick", "brown", "fox", "jumps");
        string joined = pt.Joined; // "the quick brown fox jumps"

        for (int start = 0; start < joined.Length; start++)
        for (int len = 1; len <= joined.Length - start; len += 3)
        {
            var viaSearch = pt.WordsInRange(start, len).ToList();
            var brute = new List<int>();
            int pos = 0;
            for (int i = 0; i < pt.Words.Count; i++)
            {
                int end = pos + pt.Words[i].Text.Length;
                if (end > start && pos < start + len) brute.Add(i);
                pos = end + 1; // single space separator
            }
            Assert.Equal(brute, viaSearch);
        }
    }
}

public class TextLayoutTests
{
    private static WordBox W(string text, double x, double y = 0, double h = 10) =>
        new(text, new RectD(x, y, text.Length * 5, h));

    [Fact]
    public void ClustersWordsIntoLines()
    {
        var pt = new PageText { PageIndex = 0, Width = 500, Height = 700 };
        pt.Words.Add(W("hello", 10, 10));
        pt.Words.Add(W("world", 50, 11));   // same line (within tolerance)
        pt.Words.Add(W("second", 10, 40));  // new line
        var lines = TextLayout.GetLines(pt);
        Assert.Equal(2, lines.Count);
        Assert.Equal("hello world", lines[0].Text);
        Assert.Equal("second", lines[1].Text);
    }

    [Fact]
    public void SplitsCellsOnHorizontalGaps()
    {
        var line = new TextLayout.Line(0, 10, new List<WordBox>
        {
            W("name", 0),        // right edge at 20
            W("john", 30),       // gap 10 < 14 -> same cell
            W("age", 200),       // big gap -> new cell
        });
        var cells = TextLayout.SplitIntoCells(line);
        Assert.Equal(new[] { "name john", "age" }, cells);
    }

    [Fact]
    public void GroupsParagraphsOnVerticalGaps()
    {
        var lines = new List<TextLayout.Line>
        {
            new(0, 10, new List<WordBox> { W("a", 0) }),
            new(12, 10, new List<WordBox> { W("b", 0) }),  // gap 2 -> same paragraph
            new(50, 10, new List<WordBox> { W("c", 0) }),  // gap 28 -> new paragraph
        };
        var paras = TextLayout.GetParagraphs(lines);
        Assert.Equal(2, paras.Count);
        Assert.Equal(2, paras[0].Count);
        Assert.Single(paras[1]);
    }
}

/// <summary>Round-trips through a real PDF built with PdfSharp.</summary>
public class AnnotationCodecTests
{
    private static byte[] BlankPdf(int pages = 2)
    {
        var doc = new PdfDocument();
        for (int i = 0; i < pages; i++) doc.AddPage(new PdfPage { Width = PdfSharp.Drawing.XUnit.FromPoint(595), Height = PdfSharp.Drawing.XUnit.FromPoint(842) });
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    [Fact]
    public void HighlightRoundTrips()
    {
        var pdf = BlankPdf();
        var ann = new Annotation
        {
            Kind = AnnotationKind.Highlight,
            PageIndex = 1,
            Bounds = new RectD(10, 20, 100, 12),
            Quads = { new RectD(10, 20, 50, 12), new RectD(60, 20, 50, 12) },
            ColorHex = "#F2C744",
            Contents = "important",
        };
        var read = AnnotationCodec.Read(AnnotationCodec.Write(pdf, new[] { ann }));
        var got = Assert.Single(read);
        Assert.Equal(AnnotationKind.Highlight, got.Kind);
        Assert.Equal(1, got.PageIndex);
        Assert.Equal(2, got.Quads.Count);
        Assert.Equal(20, got.Quads[0].Y, 1);
        Assert.Equal("#F2C744", got.ColorHex);
        Assert.Equal("important", got.Contents);
    }

    [Fact]
    public void FreehandHighlightRoundTripsAsHighlight()
    {
        var pdf = BlankPdf();
        var ann = new Annotation
        {
            Kind = AnnotationKind.Highlight,
            IsFreehand = true,
            PageIndex = 0,
            Bounds = new RectD(5, 5, 100, 20),
            Strokes = { new List<PointD> { new(5, 5), new(50, 10), new(100, 8) } },
            StrokeWidth = 14,
            Opacity = 0.42,
        };
        var read = AnnotationCodec.Read(AnnotationCodec.Write(pdf, new[] { ann }));
        var got = Assert.Single(read);
        Assert.Equal(AnnotationKind.Highlight, got.Kind);
        Assert.True(got.IsFreehand);
        var stroke = Assert.Single(got.Strokes);
        Assert.Equal(3, stroke.Count);
        Assert.Equal(50, stroke[1].X, 1);
        Assert.Equal(14, got.StrokeWidth, 1);
        Assert.Equal(0.42, got.Opacity, 2);
    }

    [Fact]
    public void FreeTextFormattingRoundTrips()
    {
        var pdf = BlankPdf();
        var ann = new Annotation
        {
            Kind = AnnotationKind.FreeText,
            PageIndex = 0,
            Bounds = new RectD(30, 40, 180, 44),
            Contents = "hello pdf",
            FontSize = 16,
            FontFamily = "Georgia",
            Bold = true,
            Italic = true,
            Underline = true,
            FillColorHex = "#EEEEEE",
            BorderColorHex = "#333333",
        };
        var read = AnnotationCodec.Read(AnnotationCodec.Write(pdf, new[] { ann }));
        var got = Assert.Single(read);
        Assert.Equal("hello pdf", got.Contents);
        Assert.Equal(16, got.FontSize, 1);
        Assert.Equal("Georgia", got.FontFamily);
        Assert.True(got.Bold);
        Assert.True(got.Italic);
        Assert.True(got.Underline);
        Assert.Equal("#EEEEEE", got.FillColorHex);
        Assert.Equal("#333333", got.BorderColorHex);
    }

    [Fact]
    public void RepliesRoundTrip()
    {
        var pdf = BlankPdf();
        var ann = new Annotation
        {
            Kind = AnnotationKind.Note,
            PageIndex = 0,
            Bounds = new RectD(10, 10, 18, 18),
            Contents = "parent",
        };
        ann.Replies.Add(new AnnotationReply { Author = "tester", Text = "reply one" });
        var read = AnnotationCodec.Read(AnnotationCodec.Write(pdf, new[] { ann }));
        var got = Assert.Single(read);
        var reply = Assert.Single(got.Replies);
        Assert.Equal("tester", reply.Author);
        Assert.Equal("reply one", reply.Text);
    }

    [Fact]
    public void ArrowRoundTrips()
    {
        var pdf = BlankPdf();
        var ann = new Annotation
        {
            Kind = AnnotationKind.Line,
            PageIndex = 0,
            LineStart = new PointD(10, 10),
            LineEnd = new PointD(110, 60),
            Bounds = new RectD(6, 6, 108, 58),
            StrokeWidth = 2.5,
        };
        var read = AnnotationCodec.Read(AnnotationCodec.Write(pdf, new[] { ann }));
        var got = Assert.Single(read);
        Assert.Equal(AnnotationKind.Line, got.Kind);
        Assert.Equal(10, got.LineStart.X, 1);
        Assert.Equal(60, got.LineEnd.Y, 1);
        Assert.Equal(2.5, got.StrokeWidth, 1);
    }
}

public class PdfSecurityTests
{
    private static byte[] BlankPdf()
    {
        var doc = new PdfDocument();
        doc.AddPage(new PdfPage { Width = PdfSharp.Drawing.XUnit.FromPoint(595), Height = PdfSharp.Drawing.XUnit.FromPoint(842) });
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    [Fact]
    public void PlainPdfIsNotProtected()
    {
        Assert.False(PdfSecurity.IsPasswordProtected(BlankPdf()));
    }

    [Fact]
    public void EncryptDecryptRoundTrip()
    {
        var pdf = BlankPdf();
        var encrypted = PdfSecurity.Encrypt(pdf, "s3cret");
        Assert.True(PdfSecurity.IsPasswordProtected(encrypted));

        var decrypted = PdfSecurity.Decrypt(encrypted, "s3cret");
        Assert.False(PdfSecurity.IsPasswordProtected(decrypted));
    }

    [Fact]
    public void GarbageIsNotReportedAsProtected()
    {
        // A corrupt file must surface as "broken", not as a password prompt.
        Assert.False(PdfSecurity.IsPasswordProtected(new byte[] { 1, 2, 3, 4, 5 }));
    }
}
