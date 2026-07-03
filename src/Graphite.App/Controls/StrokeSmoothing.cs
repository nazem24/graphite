using System.Windows;
using System.Windows.Media;

namespace Graphite.App.Controls;

/// <summary>
/// Turns a raw sequence of pointer samples into a smooth curve, instead of the jagged,
/// segmented look you get from connecting mouse/pen samples with straight lines.
/// Fits a Catmull-Rom spline through the points (it passes through every sampled point,
/// unlike a Bezier fit that would drift away from the input) and converts it to cubic
/// Bezier segments, which is what StreamGeometry can actually render.
/// </summary>
public static class StrokeSmoothing
{
    public static StreamGeometry ToSmoothGeometry(IReadOnlyList<Point> pts)
    {
        var geometry = new StreamGeometry();
        if (pts.Count == 0) return geometry;

        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(pts[0], false, false);

            if (pts.Count < 3)
            {
                for (int i = 1; i < pts.Count; i++)
                    ctx.LineTo(pts[i], true, true);
            }
            else
            {
                for (int i = 0; i < pts.Count - 1; i++)
                {
                    var p0 = pts[Math.Max(i - 1, 0)];
                    var p1 = pts[i];
                    var p2 = pts[i + 1];
                    var p3 = pts[Math.Min(i + 2, pts.Count - 1)];

                    // Standard Catmull-Rom -> Bezier control point conversion (tension 1/6).
                    var c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
                    var c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);

                    ctx.BezierTo(c1, c2, p2, true, true);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }
}
