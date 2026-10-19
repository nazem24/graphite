using UglyToad.PdfPig;
using UglyToad.PdfPig.Outline;

namespace Graphite.Core.Text;

/// <summary>Extracted text for a single page.</summary>
public sealed class PageText
{
    public required int PageIndex { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public List<WordBox> Words { get; } = new();

    /// <summary>All words joined with single spaces (for substring search).</summary>
    public string Joined { get; private set; } = "";
    private int[] _starts = Array.Empty<int>();

    public void RebuildJoined()
    {
        var sb = new System.Text.StringBuilder();
        _starts = new int[Words.Count];
        for (int i = 0; i < Words.Count; i++)
        {
            _starts[i] = sb.Length;
            sb.Append(Words[i].Text);
            if (i < Words.Count - 1) sb.Append(' ');
        }
        Joined = sb.ToString();
    }

    /// <summary>Word indices whose joined-text range overlaps [start, start+length).
    /// Word ranges are sorted and non-overlapping, so binary-search to the first
    /// candidate instead of scanning every word on the page for every match.</summary>
    public IEnumerable<int> WordsInRange(int start, int length)
    {
        int end = start + length;

        // First word whose end offset (start + text length) is past `start`.
        int lo = 0, hi = Words.Count - 1, first = Words.Count;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (_starts[mid] + Words[mid].Text.Length > start) { first = mid; hi = mid - 1; }
            else lo = mid + 1;
        }

        for (int i = first; i < Words.Count && _starts[i] < end; i++)
            yield return i;
    }
}

/// <summary>What the original text under an edit looks like, read from the PDF itself.</summary>
/// <param name="Text">The text in the region (lines separated by \n).</param>
/// <param name="Bounds">Union of the glyph boxes (top-left origin, points).</param>
/// <param name="FontSize">Point size of the original text.</param>
/// <param name="FontFamily">An installed family that resembles the original font.</param>
/// <param name="ColorHex">Fill colour of the original text, #RRGGBB.</param>
/// <param name="BaselineY">Baseline of the first line (top-left origin), or null when unknown.</param>
public sealed record TextStyleInfo(string Text, RectD Bounds, double FontSize, string FontFamily,
    bool Bold, bool Italic, string ColorHex, double? BaselineY, int LineCount);

/// <summary>
/// PdfPig-backed text layer: word geometry, search, outline, text selection.
/// OCR results can be layered over pages that have no native text.
/// </summary>
public sealed class TextIndex : IDisposable
{
    private PdfDocument? _doc;
    private readonly Dictionary<int, PageText> _pages = new();
    private readonly object _gate = new();
    public int PageCount { get; private set; }

    public TextIndex(byte[] pdfBytes) => Reload(pdfBytes);

    public void Reload(byte[] pdfBytes)
    {
        lock (_gate)
        {
            _doc?.Dispose();
            _pages.Clear();
            _doc = PdfDocument.Open(pdfBytes);
            PageCount = _doc.NumberOfPages;
        }
    }

    public PageText GetPage(int pageIndex)
    {
        lock (_gate)
        {
            if (_pages.TryGetValue(pageIndex, out var cached)) return cached;

            // Disposed (document closed while a search/selection was still running) or out
            // of range: behave like a page without text instead of throwing.
            if (_doc == null || pageIndex < 0 || pageIndex >= PageCount)
                return new PageText { PageIndex = pageIndex, Width = 0, Height = 0 };

            PageText pt;
            try
            {
                var page = _doc.GetPage(pageIndex + 1);
                pt = new PageText { PageIndex = pageIndex, Width = page.Width, Height = page.Height };
                foreach (var w in page.GetWords())
                {
                    var bb = w.BoundingBox;
                    // PdfPig: bottom-left origin -> convert to top-left origin.
                    var rect = new RectD(bb.Left, page.Height - bb.Top, bb.Width, bb.Height);
                    if (!string.IsNullOrWhiteSpace(w.Text))
                        pt.Words.Add(new WordBox(w.Text, rect));
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One malformed page (broken font, bad content stream) used to abort the
                // whole search / selection; treat it as a page without a text layer instead.
                System.Diagnostics.Debug.WriteLine($"Text extraction failed on page {pageIndex + 1}: {ex.Message}");
                pt = new PageText { PageIndex = pageIndex, Width = 0, Height = 0 };
            }
            pt.RebuildJoined();
            _pages[pageIndex] = pt;
            return pt;
        }
    }

    /// <summary>Replace a page's words with OCR output (top-left origin, points).</summary>
    public void SetOcrWords(int pageIndex, IEnumerable<WordBox> words, double width, double height)
    {
        lock (_gate)
        {
            var pt = new PageText { PageIndex = pageIndex, Width = width, Height = height };
            pt.Words.AddRange(words);
            pt.RebuildJoined();
            _pages[pageIndex] = pt;
        }
    }

    public IReadOnlyList<SearchMatch> Search(string query, CancellationToken ct = default, IProgress<int>? progress = null)
    {
        var results = new List<SearchMatch>();
        if (string.IsNullOrWhiteSpace(query)) return results;
        query = NormalizeWs(query);

        for (int p = 0; p < PageCount; p++)
        {
            ct.ThrowIfCancellationRequested();
            var pt = GetPage(p);
            int from = 0;
            while (true)
            {
                int idx = pt.Joined.IndexOf(query, from, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;

                var rects = pt.WordsInRange(idx, query.Length).Select(i => pt.Words[i].Box).ToList();
                if (rects.Count > 0)
                {
                    int s = Math.Max(0, idx - 32);
                    int e = Math.Min(pt.Joined.Length, idx + query.Length + 32);
                    string snippet = (s > 0 ? "…" : "") + pt.Joined[s..e] + (e < pt.Joined.Length ? "…" : "");
                    results.Add(new SearchMatch(p, snippet, rects));
                }
                from = idx + Math.Max(1, query.Length);
            }
            progress?.Report(p + 1);
        }
        return results;
    }

    /// <summary>Words intersecting a selection rectangle (for copy / markup snapping).</summary>
    public IReadOnlyList<WordBox> WordsInRect(int pageIndex, RectD rect)
    {
        var pt = GetPage(pageIndex);
        return pt.Words.Where(w => w.Box.IntersectsWith(rect)).ToList();
    }

    public string TextInRect(int pageIndex, RectD rect) =>
        string.Join(" ", WordsInRect(pageIndex, rect).Select(w => w.Text));


    // ------------------------------------------------------------------ text editing support

    /// <summary>The run of text on one line under a point (for click-to-edit): the clicked word and its
    /// neighbours on the same line, up to the first wide gap such as a column break. With
    /// <paramref name="wordOnly"/> just the clicked word. Null when the point is not on text.</summary>
    public RectD? LineAt(int pageIndex, PointD point, bool wordOnly = false)
    {
        var words = GetPage(pageIndex).Words;
        int hit = -1;
        double best = double.MaxValue;
        for (int i = 0; i < words.Count; i++)
        {
            var b = words[i].Box.Inflate(2);
            if (!b.Contains(point)) continue;
            double d = Math.Abs(point.X - (words[i].Box.X + words[i].Box.Width / 2)) +
                       Math.Abs(point.Y - (words[i].Box.Y + words[i].Box.Height / 2));
            if (d < best) { best = d; hit = i; }
        }
        if (hit < 0) return null;

        var seed = words[hit].Box;
        if (wordOnly) return seed;

        // Words on the same visual line: they overlap the seed vertically by at least half.
        var line = words
            .Where(w => VerticalOverlap(w.Box, seed) >= 0.5 * Math.Min(w.Box.Height, seed.Height))
            .Select(w => w.Box)
            .OrderBy(b => b.Left)
            .ToList();

        double maxGap = Math.Max(seed.Height, 6) * 1.0;
        var run = seed;
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var b in line)
            {
                if (b.Left < run.Left - 0.01 && run.Left - b.Right <= maxGap && b.Right <= run.Left + 0.01)
                { run = run.Union(b); grew = true; }
                else if (b.Right > run.Right + 0.01 && b.Left - run.Right <= maxGap && b.Left >= run.Right - 0.01)
                { run = run.Union(b); grew = true; }
            }
        }
        return run;
    }

    /// <summary>Snap a dragged rectangle to the words it covers (a word counts when most of it is inside).
    /// Null when no word is covered, so the caller can keep the raw rectangle.</summary>
    public RectD? SnapToWords(int pageIndex, RectD rect)
    {
        RectD? union = null;
        foreach (var w in GetPage(pageIndex).Words)
        {
            double ox = Math.Min(w.Box.Right, rect.Right) - Math.Max(w.Box.Left, rect.Left);
            double oy = Math.Min(w.Box.Bottom, rect.Bottom) - Math.Max(w.Box.Top, rect.Top);
            if (ox <= 0 || oy <= 0) continue;
            double share = ox * oy / Math.Max(0.01, w.Box.Width * w.Box.Height);
            if (share < 0.4) continue;
            union = union is { } u ? u.Union(w.Box) : w.Box;
        }
        return union;
    }

    /// <summary>The text of the words in a rectangle in reading order, lines separated by \n.</summary>
    public string TextInRectLines(int pageIndex, RectD rect)
    {
        // Words whose centre is inside, so a neighbouring line that merely touches the box is left out.
        var inner = rect.Inflate(1);
        var words = GetPage(pageIndex).Words
            .Where(w => inner.Contains(new PointD(w.Box.X + w.Box.Width / 2, w.Box.Y + w.Box.Height / 2)))
            .OrderBy(w => w.Box.Top + w.Box.Height / 2).ThenBy(w => w.Box.Left).ToList();
        var sb = new System.Text.StringBuilder();
        WordBox? prev = null;
        foreach (var w in words)
        {
            if (prev is { } p)
            {
                bool newLine = w.Box.Top + w.Box.Height / 2 > p.Box.Bottom - p.Box.Height * 0.25;
                sb.Append(newLine ? '\n' : ' ');
            }
            sb.Append(w.Text);
            prev = w;
        }
        return sb.ToString();
    }

    /// <summary>Read the font size, colour, family and baseline of the original text in a region, so an edit
    /// can be typeset to match. Null when the page has no native glyphs there (scanned / OCR pages).</summary>
    public TextStyleInfo? ProbeStyle(int pageIndex, RectD area)
    {
        lock (_gate)
        {
            if (_doc == null || pageIndex < 0 || pageIndex >= PageCount) return null;
            try
            {
                var page = _doc.GetPage(pageIndex + 1);
                var letters = new List<UglyToad.PdfPig.Content.Letter>();
                var bounds = new List<RectD>();
                foreach (var l in page.Letters)
                {
                    if (string.IsNullOrWhiteSpace(l.Value)) continue;
                    var g = l.BoundingBox;
                    var box = new RectD(g.Left, page.Height - g.Top, g.Width, g.Height);
                    var center = new PointD(box.X + box.Width / 2, box.Y + box.Height / 2);
                    if (!area.Inflate(1).Contains(center)) continue;
                    letters.Add(l);
                    bounds.Add(box);
                }
                if (letters.Count == 0) return null;

                double size = Median(letters.Select(l => l.PointSize > 1 ? l.PointSize : 0).Where(v => v > 0).ToList());
                if (size <= 0) size = Median(bounds.Select(b => b.Height * 0.85).ToList());

                string fontName = letters.GroupBy(l => l.FontName ?? "").OrderByDescending(g => g.Count()).First().Key;
                string lower = fontName.ToLowerInvariant();
                bool bold = lower.Contains("bold") || lower.Contains("black") || lower.Contains("heavy") || lower.Contains("-bd");
                bool italic = lower.Contains("italic") || lower.Contains("oblique") || lower.EndsWith("-it") || lower.Contains("ital");

                string color = letters
                    .Select(l => ColorToHex(l.Color))
                    .GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;

                var baselines = letters.Select(l => page.Height - l.StartBaseLine.Y).OrderBy(y => y).ToList();
                var lines = new List<double>();
                foreach (double y in baselines)
                    if (lines.Count == 0 || y - lines[^1] > Math.Max(2, size * 0.5)) lines.Add(y);

                var union = bounds.Aggregate((a, b) => a.Union(b));
                return new TextStyleInfo(
                    TextInRectLines(pageIndex, area), union, size, MapFamily(fontName), bold, italic, color,
                    lines.Count > 0 ? lines[0] : null, Math.Max(1, lines.Count));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                System.Diagnostics.Debug.WriteLine($"Style probe failed on page {pageIndex + 1}: {ex.Message}");
                return null;
            }
        }
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[values.Count / 2];
    }

    private static string ColorToHex(UglyToad.PdfPig.Graphics.Colors.IColor? color)
    {
        if (color == null) return "#1A1A1A";
        var (r, g, b) = color.ToRGBValues();
        static int C(double v) => (int)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return $"#{C(r):X2}{C(g):X2}{C(b):X2}";
    }

    /// <summary>An installed family that looks like the PDF's font (the fonts Graphite can typeset with).</summary>
    private static string MapFamily(string pdfFontName)
    {
        string n = System.Text.RegularExpressions.Regex.Replace(pdfFontName ?? "", "^[A-Z]{6}\\+", "").ToLowerInvariant();
        if (n.Contains("courier") || n.Contains("mono") || n.Contains("consolas")) return "Courier New";
        if (n.Contains("georgia")) return "Georgia";
        if (n.Contains("verdana")) return "Verdana";
        if (n.Contains("tahoma")) return "Tahoma";
        if (n.Contains("calibri") || n.Contains("carlito")) return "Calibri";
        if (n.Contains("segoe")) return "Segoe UI";
        if (n.Contains("sans") || n.Contains("arial") || n.Contains("helvetica")) return "Arial";
        if (n.Contains("times") || n.Contains("serif") || n.Contains("minion") || n.Contains("garamond") ||
            n.Contains("palatino") || n.Contains("cambria") || n.Contains("charter") || n.Contains("century") ||
            n.Contains("book") || n.Contains("roman")) return "Times New Roman";
        return "Arial";
    }

    private static double VerticalOverlap(RectD a, RectD b) =>
        Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);

    /// <summary>Whether a page has any native (or OCR-provided) text.</summary>
    public bool HasText(int pageIndex) => GetPage(pageIndex).Words.Count > 0;

    public IReadOnlyList<OutlineNode> GetOutline()
    {
        lock (_gate)
        {
            var roots = new List<OutlineNode>();
            if (_doc != null && _doc.TryGetBookmarks(out var bookmarks))
                foreach (var n in bookmarks.Roots)
                    roots.Add(Convert(n));
            return roots;
        }
    }

    private static OutlineNode Convert(BookmarkNode node)
    {
        int? pageIndex = node is DocumentBookmarkNode d ? d.PageNumber - 1 : null;
        var result = new OutlineNode { Title = node.Title, PageIndex = pageIndex };
        foreach (var c in node.Children) result.Children.Add(Convert(c));
        return result;
    }

    private static string NormalizeWs(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"\s+", " ");

    public void Dispose()
    {
        lock (_gate) { _doc?.Dispose(); _doc = null; _pages.Clear(); PageCount = 0; }
    }
}
