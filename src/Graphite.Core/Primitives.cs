namespace Graphite.Core;

/// <summary>Point in PDF points (1/72"), top-left origin.</summary>
public readonly record struct PointD(double X, double Y);

/// <summary>Rectangle in PDF points (1/72"), top-left origin.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public static RectD FromCorners(double x1, double y1, double x2, double y2) =>
        new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    public bool IntersectsWith(RectD other) =>
        other.Left < Right && other.Right > Left && other.Top < Bottom && other.Bottom > Top;

    public bool Contains(PointD p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;

    public RectD Union(RectD other) =>
        FromCorners(Math.Min(Left, other.Left), Math.Min(Top, other.Top),
                    Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

    public RectD Inflate(double d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);
}

/// <summary>A word of extracted text with its bounding box (top-left origin, points).</summary>
public readonly record struct WordBox(string Text, RectD Box);

/// <summary>One search hit.</summary>
public sealed record SearchMatch(int PageIndex, string Snippet, IReadOnlyList<RectD> Rects);

/// <summary>Bookmark / outline tree node.</summary>
public sealed class OutlineNode
{
    public required string Title { get; init; }
    public int? PageIndex { get; init; }               // 0-based, null for URI-only nodes
    public List<OutlineNode> Children { get; } = new();
}

/// <summary>Raw rendered page bitmap, BGRA8888 premultiplied.</summary>
public sealed record RenderedPage(int Width, int Height, byte[] Bgra);
