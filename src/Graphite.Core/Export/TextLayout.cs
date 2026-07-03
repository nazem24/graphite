using Graphite.Core.Text;

namespace Graphite.Core.Export;

/// <summary>Reconstructs lines/paragraphs/columns from positioned words. Shared by Word/Excel export.</summary>
public static class TextLayout
{
    public sealed record Line(double Top, double Height, List<WordBox> Words)
    {
        public string Text => string.Join(" ", Words.Select(w => w.Text));
    }

    /// <summary>Cluster words into visual lines by vertical overlap.</summary>
    public static List<Line> GetLines(PageText page)
    {
        var lines = new List<Line>();
        foreach (var w in page.Words.OrderBy(w => w.Box.Top).ThenBy(w => w.Box.Left))
        {
            var line = lines.LastOrDefault(l =>
                Math.Abs((l.Top + l.Height / 2) - (w.Box.Top + w.Box.Height / 2)) < Math.Max(l.Height, w.Box.Height) * 0.6);
            if (line == null)
            {
                lines.Add(new Line(w.Box.Top, w.Box.Height, new List<WordBox> { w }));
            }
            else
            {
                line.Words.Add(w);
            }
        }
        foreach (var l in lines) l.Words.Sort((a, b) => a.Box.Left.CompareTo(b.Box.Left));
        return lines.OrderBy(l => l.Top).ToList();
    }

    /// <summary>Group lines into paragraphs: a vertical gap &gt; 0.9 line-height starts a new paragraph.</summary>
    public static List<List<Line>> GetParagraphs(List<Line> lines)
    {
        var paragraphs = new List<List<Line>>();
        List<Line>? current = null;
        Line? prev = null;
        foreach (var line in lines)
        {
            bool newPara = prev == null ||
                line.Top - (prev.Top + prev.Height) > Math.Max(prev.Height, line.Height) * 0.9;
            if (newPara) { current = new List<Line>(); paragraphs.Add(current); }
            current!.Add(line);
            prev = line;
        }
        return paragraphs;
    }

    /// <summary>Split a line into cells wherever the horizontal gap exceeds <paramref name="gapPt"/>.</summary>
    public static List<string> SplitIntoCells(Line line, double gapPt = 14)
    {
        var cells = new List<string>();
        var sb = new System.Text.StringBuilder();
        WordBox? prev = null;
        foreach (var w in line.Words)
        {
            if (prev != null && w.Box.Left - prev.Value.Box.Right > gapPt)
            {
                cells.Add(sb.ToString());
                sb.Clear();
            }
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(w.Text);
            prev = w;
        }
        if (sb.Length > 0) cells.Add(sb.ToString());
        return cells;
    }
}
