using System.Windows;
using System.Windows.Media;

namespace Graphite.App.Controls;

/// <summary>
/// Turns a raw sequence of pointer samples into a smooth curve, instead of the jagged,
/// segmented look you get from connecting pen samples with straight lines.
///
/// The fit is a quadratic-Bezier-through-midpoints chain: every segment ends at the
/// midpoint between two samples and uses the sample itself as its control point. The
/// curve therefore hugs the input — it never overshoots into loops or spikes the way a
/// Catmull-Rom spline through every (jittery) sample does, which is what made quick
/// direction changes while writing letters look "over-corrected".
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

            switch (pts.Count)
            {
                case 1:
                    break;
                case 2:
                    ctx.LineTo(pts[1], true, true);
                    break;
                default:
                    // First segment: straight to the first midpoint so the stroke starts
                    // exactly under the pen tip (no lag at stroke start).
                    var mid = Midpoint(pts[0], pts[1]);
                    ctx.LineTo(mid, true, true);

                    // Middle segments: curve sample -> midpoint, with the sample as the
                    // control point. Consecutive segments share tangents at the midpoints,
                    // so the stroke stays C1-smooth without passing through every sample.
                    for (int i = 1; i < pts.Count - 1; i++)
                    {
                        var next = Midpoint(pts[i], pts[i + 1]);
                        ctx.QuadraticBezierTo(pts[i], next, true, true);
                    }

                    // Last segment: land exactly on the final sample so the stroke ends
                    // where the pen lifted.
                    ctx.LineTo(pts[^1], true, true);
                    break;
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point Midpoint(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);
}
