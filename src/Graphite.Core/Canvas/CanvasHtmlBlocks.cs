using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Graphite.Core.Canvas;

/// <summary>What a block of a rendered Canvas page is.</summary>
public enum CanvasBlockKind { Paragraph, Heading, ListItem, Quote, Code, Rule, Image }

/// <summary>A run of text inside a block: one style, and the address it links to (if any).
/// A run whose text is a single line feed is a line break.</summary>
public sealed record CanvasRun(string Text, bool Bold = false, bool Italic = false, bool Underline = false,
    bool Code = false, string? Href = null)
{
    public bool IsBreak => Text == "\n";
}

/// <summary>One block of a Canvas page, reduced to what Graphite can lay out: paragraphs, headings, list items,
/// quotes, code, rules and pictures. The views turn these into WPF elements.</summary>
public sealed class CanvasBlock
{
    public CanvasBlockKind Kind { get; init; }

    /// <summary>Heading size (1 to 6), list nesting depth (0 and up) or quote depth (1 and up).</summary>
    public int Level { get; init; }

    /// <summary>The bullet or number in front of a list item ("•", "3.").</summary>
    public string Marker { get; init; } = "";

    public List<CanvasRun> Runs { get; } = new();

    /// <summary>For pictures: the absolute address, the description, and the address the picture links to.</summary>
    public string? ImageUrl { get; init; }
    public string? Alt { get; init; }
    public string? Href { get; init; }

    public string PlainText => string.Concat(Runs.Select(r => r.IsBreak ? " " : r.Text));
}

/// <summary>
/// Turns the HTML of a Canvas page (the start page of a course) into <see cref="CanvasBlock"/>s. It is not a
/// browser: it keeps the text, its structure, links, bold / italic and pictures, and drops colours, fonts and
/// layout. Scripts, styles and embedded frames are never run; a frame becomes a link to its address.
/// </summary>
public static class CanvasHtmlBlocks
{
    private static readonly Regex Token = new(
        @"<!--.*?-->|<(?<close>/?)(?<name>[a-zA-Z][a-zA-Z0-9]*)(?<attrs>(?:[^>""']|""[^""]*""|'[^']*')*)>",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Attr = new(
        @"(?<name>[\w:-]+)\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"[\s ]+", RegexOptions.Compiled);
    private static readonly Regex Bold = new(@"font-weight\s*:\s*(bold|[6-9]00)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Italic = new(@"font-style\s*:\s*italic", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Underline = new(@"text-decoration[^;]*underline", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> SkipContent = new(StringComparer.OrdinalIgnoreCase)
        { "script", "style", "head", "title", "noscript", "template", "svg", "iframe", "video", "audio", "object", "canvas", "select", "button" };

    private static readonly HashSet<string> BreaksBlock = new(StringComparer.OrdinalIgnoreCase)
    {
        "div", "section", "article", "header", "footer", "main", "nav", "aside", "figure", "figcaption", "table", "thead",
        "tbody", "tfoot", "tr", "td", "th", "caption", "dl", "dt", "dd", "center", "form", "fieldset", "address", "details", "summary",
    };

    private const int MaxBlocks = 600;

    public static List<CanvasBlock> Parse(string? html, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html)) return new List<CanvasBlock>();
        Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri);
        return new Builder(baseUri).Run(html);
    }

    private sealed class Builder
    {
        private readonly Uri? _base;
        private readonly List<CanvasBlock> _out = new();

        // the block being written
        private CanvasBlockKind _kind = CanvasBlockKind.Paragraph;
        private int _level;
        private string _marker = "";
        private readonly List<CanvasRun> _runs = new();

        // inline state
        private int _bold, _italic, _underline, _code, _pre, _quote;
        private readonly Stack<string?> _links = new();
        private readonly Stack<(bool Ordered, int Count)> _lists = new();
        private readonly Stack<(bool B, bool I, bool U)> _spans = new();

        private string? _skip;
        private int _skipDepth;

        public Builder(Uri? baseUri) => _base = baseUri;

        public List<CanvasBlock> Run(string html)
        {
            int pos = 0;
            foreach (Match m in Token.Matches(html))
            {
                if (m.Index > pos) Text(html.Substring(pos, m.Index - pos));
                pos = m.Index + m.Length;
                if (_out.Count >= MaxBlocks) break;
                if (!m.Groups["name"].Success) continue;   // a comment
                Tag(m.Groups["name"].Value.ToLowerInvariant(), m.Groups["close"].Length > 0, m.Groups["attrs"].Value);
            }
            if (pos < html.Length && _out.Count < MaxBlocks) Text(html.Substring(pos));
            Flush();
            return _out;
        }

        // ------------------------------------------------------------- text

        private void Text(string raw)
        {
            if (_skip != null || raw.Length == 0) return;
            string text = WebUtility.HtmlDecode(raw);
            if (_pre > 0)
            {
                text = text.Replace("\r\n", "\n").Replace(' ', ' ');
                if (text.Length > 0) AddRun(text);
                return;
            }
            text = Spaces.Replace(text, " ");
            if (text == " " && _runs.Count == 0) return;
            // no double space across two runs, none at the start of a block
            if (text.StartsWith(' ') && (_runs.Count == 0 || EndsWithSpaceOrBreak())) text = text.TrimStart();
            if (text.Length > 0) AddRun(text);
        }

        private bool EndsWithSpaceOrBreak() =>
            _runs.Count > 0 && (_runs[^1].IsBreak || _runs[^1].Text.EndsWith(' '));

        private void AddRun(string text) =>
            _runs.Add(new CanvasRun(text, _bold > 0, _italic > 0, _underline > 0, _code > 0 || _pre > 0,
                _links.Count > 0 ? _links.Peek() : null));

        // ------------------------------------------------------------- blocks

        private void Begin(CanvasBlockKind kind, int level = 0, string marker = "")
        {
            Flush();
            _kind = kind;
            _level = level;
            _marker = marker;
        }

        private void Flush()
        {
            if (_runs.Count > 0)
            {
                // trim the ends: no space before the first word, no breaks or space after the last
                while (_runs.Count > 0 && _runs[^1].IsBreak && _pre == 0) _runs.RemoveAt(_runs.Count - 1);
                if (_runs.Count > 0 && _pre == 0)
                {
                    var last = _runs[^1];
                    _runs[^1] = last with { Text = last.Text.TrimEnd() };
                    var first = _runs[0];
                    _runs[0] = first with { Text = first.Text.TrimStart() };
                }
                _runs.RemoveAll(r => r.Text.Length == 0);
                bool hasContent = _runs.Any(r => !r.IsBreak && (_kind == CanvasBlockKind.Code || r.Text.Trim().Length > 0));
                if (hasContent)
                {
                    var kind = _kind == CanvasBlockKind.Paragraph && _quote > 0 ? CanvasBlockKind.Quote : _kind;
                    int level = kind == CanvasBlockKind.Quote ? _quote : _level;
                    var block = new CanvasBlock { Kind = kind, Level = level, Marker = _marker };
                    block.Runs.AddRange(_runs);
                    if (kind == CanvasBlockKind.Code && block.Runs.Count > 0)
                    {
                        var l = block.Runs[^1];
                        block.Runs[^1] = l with { Text = l.Text.TrimEnd('\n', '\r') };
                    }
                    _out.Add(block);
                }
            }
            _runs.Clear();
            _kind = CanvasBlockKind.Paragraph;
            _level = 0;
            _marker = "";
        }

        // ------------------------------------------------------------- tags

        private void Tag(string name, bool close, string attrText)
        {
            if (_skip != null)
            {
                if (name == _skip)
                {
                    if (close) { if (--_skipDepth <= 0) _skip = null; }
                    else if (!attrText.TrimEnd().EndsWith('/')) _skipDepth++;
                }
                return;
            }

            if (SkipContent.Contains(name))
            {
                if (close) return;
                if (name is "iframe" or "video" or "audio" or "object")
                {
                    var a = Attrs(attrText);
                    string? src = Resolve(a.GetValueOrDefault("src"));
                    if (src != null)
                    {
                        Flush();
                        string label = a.TryGetValue("title", out var t) && t.Trim().Length > 0
                            ? t.Trim()
                            : "Embedded content (" + (Uri.TryCreate(src, UriKind.Absolute, out var u) ? u.Host : "open") + ")";
                        _runs.Add(new CanvasRun(label, Href: src));
                        Flush();
                    }
                }
                if (!attrText.TrimEnd().EndsWith('/')) { _skip = name; _skipDepth = 1; }
                return;
            }

            switch (name)
            {
                case "br":
                    if (_runs.Count > 0 || _pre > 0) _runs.Add(new CanvasRun("\n"));
                    return;
                case "hr":
                    Flush();
                    _out.Add(new CanvasBlock { Kind = CanvasBlockKind.Rule });
                    return;
                case "img":
                    if (!close) Image(attrText);
                    return;
                case "a":
                    if (close) { if (_links.Count > 0) _links.Pop(); }
                    else _links.Push(Resolve(Attrs(attrText).GetValueOrDefault("href")));
                    return;
                case "p":
                    if (close) Flush(); else Begin(CanvasBlockKind.Paragraph);
                    return;
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                    if (close) Flush(); else Begin(CanvasBlockKind.Heading, name[1] - '0');
                    return;
                case "ul": case "ol":
                    Flush();
                    if (close) { if (_lists.Count > 0) _lists.Pop(); }
                    else
                    {
                        int start = int.TryParse(Attrs(attrText).GetValueOrDefault("start"), out int s) ? s - 1 : 0;
                        _lists.Push((name == "ol", start));
                    }
                    return;
                case "li":
                    if (close) { Flush(); return; }
                    ListItem();
                    return;
                case "blockquote":
                    Flush();
                    if (close) _quote = Math.Max(0, _quote - 1); else _quote++;
                    return;
                case "pre":
                    if (close) { Flush(); _pre = Math.Max(0, _pre - 1); }
                    else { Begin(CanvasBlockKind.Code); _pre++; }
                    return;
                case "strong": case "b":
                    _bold = close ? Math.Max(0, _bold - 1) : _bold + 1;
                    return;
                case "em": case "i": case "cite": case "dfn":
                    _italic = close ? Math.Max(0, _italic - 1) : _italic + 1;
                    return;
                case "u": case "ins":
                    _underline = close ? Math.Max(0, _underline - 1) : _underline + 1;
                    return;
                case "code": case "kbd": case "samp": case "tt":
                    _code = close ? Math.Max(0, _code - 1) : _code + 1;
                    return;
                case "span": case "font":
                    Span(close, attrText);
                    return;
            }

            if (BreaksBlock.Contains(name)) Flush();
        }

        private void ListItem()
        {
            int depth = Math.Max(0, _lists.Count - 1);
            string marker = "•";
            if (_lists.Count > 0)
            {
                var (ordered, count) = _lists.Pop();
                count++;
                _lists.Push((ordered, count));
                marker = ordered ? count + "." : depth == 0 ? "•" : depth == 1 ? "◦" : "▪";
            }
            Begin(CanvasBlockKind.ListItem, depth, marker);
        }

        /// <summary>A span (or font) that sets bold / italic / underline in its style attribute.</summary>
        private void Span(bool close, string attrText)
        {
            if (close)
            {
                if (_spans.Count == 0) return;
                var (b, i, u) = _spans.Pop();
                if (b) _bold = Math.Max(0, _bold - 1);
                if (i) _italic = Math.Max(0, _italic - 1);
                if (u) _underline = Math.Max(0, _underline - 1);
                return;
            }
            string style = Attrs(attrText).GetValueOrDefault("style") ?? "";
            bool sb = Bold.IsMatch(style), si = Italic.IsMatch(style), su = Underline.IsMatch(style);
            if (sb) _bold++;
            if (si) _italic++;
            if (su) _underline++;
            _spans.Push((sb, si, su));
        }

        private void Image(string attrText)
        {
            var a = Attrs(attrText);
            string? src = Resolve(a.GetValueOrDefault("src"));
            if (src == null) return;
            string? href = _links.Count > 0 ? _links.Peek() : null;
            Flush();
            _out.Add(new CanvasBlock { Kind = CanvasBlockKind.Image, ImageUrl = src, Alt = a.GetValueOrDefault("alt")?.Trim(), Href = href });
        }

        // ------------------------------------------------------------- helpers

        private static Dictionary<string, string> Attrs(string text)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Attr.Matches(text)) d[m.Groups["name"].Value] = WebUtility.HtmlDecode(m.Groups["v"].Value);
            return d;
        }

        /// <summary>An absolute http(s) or mailto address, resolved against the Canvas address; null for anything else
        /// (scripts, in-page anchors, empty).</summary>
        private string? Resolve(string? href)
        {
            if (string.IsNullOrWhiteSpace(href) || href.TrimStart().StartsWith('#')) return null;
            Uri? abs;
            if (_base != null) Uri.TryCreate(_base, href.Trim(), out abs);
            else Uri.TryCreate(href.Trim(), UriKind.Absolute, out abs);
            if (abs == null) return null;
            if (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps) return abs.AbsoluteUri;
            return abs.Scheme == Uri.UriSchemeMailto ? abs.AbsoluteUri : null;
        }
    }
}
