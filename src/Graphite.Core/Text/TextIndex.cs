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

            var page = _doc!.GetPage(pageIndex + 1);
            var pt = new PageText { PageIndex = pageIndex, Width = page.Width, Height = page.Height };
            foreach (var w in page.GetWords())
            {
                var bb = w.BoundingBox;
                // PdfPig: bottom-left origin -> convert to top-left origin.
                var rect = new RectD(bb.Left, page.Height - bb.Top, bb.Width, bb.Height);
                if (!string.IsNullOrWhiteSpace(w.Text))
                    pt.Words.Add(new WordBox(w.Text, rect));
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
        lock (_gate) { _doc?.Dispose(); _doc = null; }
    }
}
