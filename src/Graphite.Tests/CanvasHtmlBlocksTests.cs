using Graphite.Core.Canvas;
using Xunit;

namespace Graphite.Tests;

public class CanvasHtmlBlocksTests
{
    private const string Base = "https://learning.unisg.ch";

    [Fact]
    public void ParagraphKeepsStyleAndResolvesLinks()
    {
        var blocks = CanvasHtmlBlocks.Parse(
            """<p>Welcome to <strong>Accounting</strong> &amp; <em>Control</em>. See the <a href="/courses/1/pages/syllabus">syllabus</a>.</p>""", Base);
        var p = Assert.Single(blocks);
        Assert.Equal(CanvasBlockKind.Paragraph, p.Kind);
        Assert.Equal("Welcome to Accounting & Control. See the syllabus.", p.PlainText);
        Assert.Contains(p.Runs, r => r.Text == "Accounting" && r.Bold && !r.Italic);
        Assert.Contains(p.Runs, r => r.Text == "Control" && r.Italic);
        var link = Assert.Single(p.Runs, r => r.Href != null);
        Assert.Equal("syllabus", link.Text);
        Assert.Equal("https://learning.unisg.ch/courses/1/pages/syllabus", link.Href);
    }

    [Fact]
    public void HeadingsAndNestedLists()
    {
        var blocks = CanvasHtmlBlocks.Parse(
            """
            <h2>Schedule</h2>
            <ol><li>First</li><li>Second<ul><li>Sub a</li><li>Sub b</li></ul></li><li>Third</li></ol>
            <ul><li>Plain</li></ul>
            """, Base);
        Assert.Equal(CanvasBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(2, blocks[0].Level);
        var items = blocks.Where(b => b.Kind == CanvasBlockKind.ListItem).ToList();
        Assert.Equal(new[] { "First", "Second", "Sub a", "Sub b", "Third", "Plain" }, items.Select(i => i.PlainText).ToArray());
        Assert.Equal(new[] { "1.", "2.", "◦", "◦", "3.", "•" }, items.Select(i => i.Marker).ToArray());
        Assert.Equal(new[] { 0, 0, 1, 1, 0, 0 }, items.Select(i => i.Level).ToArray());
    }

    [Fact]
    public void ScriptsStylesAndCommentsAreDropped()
    {
        var blocks = CanvasHtmlBlocks.Parse(
            """<style>p {color:red}</style><!-- hidden --><script>alert(1)</script><div>Hello&nbsp;<b>there</b></div><p>  </p>""", Base);
        var p = Assert.Single(blocks);
        Assert.Equal("Hello there", p.PlainText);
    }

    [Fact]
    public void ImagesAndFramesBecomeBlocksAndLinks()
    {
        var blocks = CanvasHtmlBlocks.Parse(
            """
            <p><img src="/courses/1/files/9/preview" alt="Banner"></p>
            <p><a href="https://example.org/x"><img src="https://example.org/i.png"></a></p>
            <iframe src="https://www.youtube.com/embed/abc" title="Intro video"></iframe>
            <img src="data:image/png;base64,AAAA">
            """, Base);
        var images = blocks.Where(b => b.Kind == CanvasBlockKind.Image).ToList();
        Assert.Equal(2, images.Count);
        Assert.Equal("https://learning.unisg.ch/courses/1/files/9/preview", images[0].ImageUrl);
        Assert.Equal("Banner", images[0].Alt);
        Assert.Equal("https://example.org/x", images[1].Href);
        var frame = Assert.Single(blocks, b => b.Kind == CanvasBlockKind.Paragraph);
        Assert.Equal("Intro video", frame.PlainText);
        Assert.Equal("https://www.youtube.com/embed/abc", frame.Runs[0].Href);
    }

    [Fact]
    public void QuotesCodeRulesAndBreaks()
    {
        var blocks = CanvasHtmlBlocks.Parse(
            "<blockquote><p>Quoted words</p></blockquote><pre>line 1\nline 2</pre><hr><p>One<br>Two<br></p>", Base);
        Assert.Equal(CanvasBlockKind.Quote, blocks[0].Kind);
        Assert.Equal(1, blocks[0].Level);
        Assert.Equal(CanvasBlockKind.Code, blocks[1].Kind);
        Assert.Equal("line 1\nline 2", blocks[1].PlainText);
        Assert.Equal(CanvasBlockKind.Rule, blocks[2].Kind);
        var p = blocks[3];
        Assert.Equal(3, p.Runs.Count);
        Assert.True(p.Runs[1].IsBreak);
    }

    [Fact]
    public void StyleAttributeOnSpanSetsBold()
    {
        var blocks = CanvasHtmlBlocks.Parse("""<p>a <span style="font-weight: bold">b</span> c</p>""", Base);
        var p = Assert.Single(blocks);
        Assert.Contains(p.Runs, r => r.Text == "b" && r.Bold);
        Assert.DoesNotContain(p.Runs, r => r.Text.Contains('c') && r.Bold);
    }

    [Fact]
    public void LinkAddressesMatchTheLinkRows()
    {
        const string html = """<p><a class="instructure_file_link" href="/courses/1/files/555?wrap=1" data-api-endpoint="https://learning.unisg.ch/api/v1/courses/1/files/555" data-api-returntype="File">Slides.pdf</a> and <a href="https://example.org/a#top">Other</a></p>""";
        var rows = CanvasHtml.ExtractLinks(html, Base);
        var run = CanvasHtmlBlocks.Parse(html, Base).Single().Runs.Where(r => r.Href != null).ToList();
        Assert.Equal(rows.Select(r => r.Url).ToArray(), run.Select(r => r.Href!).ToArray());
    }

    [Fact]
    public void EmptyAndHostileInputGivesNothingOrText()
    {
        Assert.Empty(CanvasHtmlBlocks.Parse(null, Base));
        Assert.Empty(CanvasHtmlBlocks.Parse("   ", Base));
        var blocks = CanvasHtmlBlocks.Parse("""<p><a href="javascript:alert(1)">x</a> <a href="#top">y</a></p>""", Base);
        Assert.All(blocks.SelectMany(b => b.Runs), r => Assert.Null(r.Href));
        // broken markup does not throw
        Assert.NotNull(CanvasHtmlBlocks.Parse("<p><b>unclosed <i>tags </p></b></i> < & >", Base));
    }

    [Fact]
    public void SignatureIgnoresFetchTimeButNotContent()
    {
        var a = new CanvasCourseData { FetchedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Front = new CanvasFrontPage { Body = "<p>x</p>" } };
        var b = new CanvasCourseData { FetchedUtc = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), Front = new CanvasFrontPage { Body = "<p>x</p>" } };
        var c = new CanvasCourseData { Front = new CanvasFrontPage { Body = "<p>y</p>" } };
        Assert.Equal(a.Signature(), b.Signature());
        Assert.NotEqual(a.Signature(), c.Signature());
    }
}
