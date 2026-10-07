using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Graphite.App.ViewModels;

namespace Graphite.App.Views;

/// <summary>
/// Pinch / Ctrl+wheel zoom.
///
/// Changing <see cref="DocumentViewModel.Zoom"/> resizes every page, which re-lays-out the
/// virtualizing list and re-estimates its extent. Doing that on every touch-move or wheel
/// tick was both slow (a layout pass per frame) and unstable: the panel only estimates the
/// size of pages it hasn't built, so the offset it reports drifted and the view landed on a
/// different page mid-gesture.
///
/// Instead, while a gesture is in flight the already-rendered pages are scaled and panned on
/// the GPU (a render transform on the list's items presenter — no layout at all). When the
/// gesture settles the real zoom is applied once, anchored on the pinch centre, and the page
/// under the anchor is pinned to its exact position against the real page containers.
/// </summary>
public partial class MainWindow
{
    private const double MinZoom = 0.25, MaxZoom = 6;

    private sealed class ZoomPreview
    {
        public required ListBox List { get; init; }
        public required ScrollViewer Scroller { get; init; }
        public required DocumentViewModel Doc { get; init; }
        public required FrameworkElement Surface { get; init; }
        public required ScaleTransform Scale { get; init; }
        public required TranslateTransform Pan { get; init; }
        /// <summary>The point the zoom is anchored on, in ScrollViewer (viewport) coordinates.</summary>
        public required Point Anchor { get; init; }
        public required double StartZoom { get; init; }
        /// <summary>Wheel zoom eases toward its target; a pinch follows the fingers directly.</summary>
        public required bool Eased { get; init; }

        public double Factor = 1;        // what is on screen
        public double TargetFactor = 1;  // where the wheel wants it
        public Vector Shift;             // two-finger drag since the gesture began (viewport DIPs)
    }

    private ZoomPreview? _zp;
    private DispatcherTimer? _zpTimer;
    private bool _zpRendering;
    private long _zpLast;

    private ZoomPreview? BeginZoomPreview(ListBox lb, DocumentViewModel doc, Point anchor, bool eased)
    {
        var sv = FindScrollViewer(lb);
        if (sv == null || sv.ViewportWidth <= 0 || sv.ViewportHeight <= 0) return null;
        if (FindDescendant<ItemsPresenter>(sv) is not { } surface ||
            (surface.RenderTransform is { } existing && !existing.Value.IsIdentity))
            return null;

        StopScroller(lb);
        _pins.Remove(doc);

        anchor = new Point(Math.Clamp(anchor.X, 0, sv.ViewportWidth), Math.Clamp(anchor.Y, 0, sv.ViewportHeight));
        Point local = sv.TranslatePoint(anchor, surface);

        var scale = new ScaleTransform(1, 1, local.X, local.Y);
        var pan = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(pan);
        surface.RenderTransform = group;

        return _zp = new ZoomPreview
        {
            List = lb, Scroller = sv, Doc = doc, Surface = surface, Scale = scale, Pan = pan,
            Anchor = anchor, StartZoom = doc.Zoom, Eased = eased,
        };
    }

    private static void ApplyZoomPreview(ZoomPreview zp)
    {
        double f = Math.Clamp(zp.Factor, MinZoom / zp.StartZoom, MaxZoom / zp.StartZoom);
        zp.Scale.ScaleX = f;
        zp.Scale.ScaleY = f;
        zp.Pan.X = zp.Shift.X;
        zp.Pan.Y = zp.Shift.Y;
    }

    private void StopZoomPreviewTimers()
    {
        _zpTimer?.Stop();
        if (_zpRendering)
        {
            CompositionTarget.Rendering -= ZoomPreviewStep;
            _zpRendering = false;
        }
    }

    /// <summary>Drop the live preview without applying it (the zoom changed some other way,
    /// or the tab went away mid-gesture).</summary>
    private void CancelZoomPreview()
    {
        var zp = _zp;
        if (zp == null) return;
        _zp = null;
        StopZoomPreviewTimers();
        zp.Surface.RenderTransform = Transform.Identity;
    }

    /// <summary>Apply the previewed zoom for real: one layout pass, anchored on the gesture.</summary>
    private void CommitZoomPreview()
    {
        var zp = _zp;
        if (zp == null) return;
        _zp = null;
        StopZoomPreviewTimers();

        double factor = zp.Eased ? zp.TargetFactor : zp.Factor;
        zp.Surface.RenderTransform = Transform.Identity;

        var doc = zp.Doc;
        var lb = zp.List;
        var sv = zp.Scroller;
        if (!ReferenceEquals(lb.DataContext, doc) || !lb.IsVisible) return;

        double newZoom = Math.Clamp(zp.StartZoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - doc.Zoom) > 1e-4)
        {
            _zoomAnchor = zp.Anchor; // content under the gesture centre stays put…
            try { doc.Zoom = newZoom; }
            finally { _zoomAnchor = null; }
        }

        if (zp.Shift.LengthSquared > 0.25)
        {
            // …then the two-finger drag carries it to where the fingers ended up.
            _trackingSuspended++;
            try
            {
                sv.ScrollToHorizontalOffset(Math.Max(0, sv.HorizontalOffset - zp.Shift.X));
                sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset - zp.Shift.Y));
                lb.UpdateLayout();
            }
            finally { _trackingSuspended--; }
        }
        SyncFromViewport(lb, doc);
    }

    /// <summary>Ctrl+wheel (and a precision-touchpad pinch, which arrives as Ctrl+wheel).</summary>
    private void WheelZoomStep(ListBox lb, DocumentViewModel doc, Point pointer, double factor)
    {
        // A pointer that has travelled starts a new gesture so the anchor follows it.
        if (_zp != null && (!ReferenceEquals(_zp.List, lb) || !_zp.Eased || (pointer - _zp.Anchor).Length > 4))
            CommitZoomPreview();

        var zp = _zp ?? BeginZoomPreview(lb, doc, pointer, eased: true);
        if (zp == null)
        {
            // No surface to transform (viewer not built yet): zoom directly.
            _zoomAnchor = pointer;
            try { doc.Zoom = Math.Clamp(doc.Zoom * factor, MinZoom, MaxZoom); }
            finally { _zoomAnchor = null; }
            return;
        }

        zp.TargetFactor = Math.Clamp(zp.TargetFactor * factor, MinZoom / zp.StartZoom, MaxZoom / zp.StartZoom);
        if (!_zpRendering)
        {
            _zpRendering = true;
            _zpLast = System.Diagnostics.Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += ZoomPreviewStep;
        }

        _zpTimer ??= CreateZoomCommitTimer();
        _zpTimer.Stop();
        _zpTimer.Start();
    }

    private DispatcherTimer CreateZoomCommitTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CommitZoomPreview();
        };
        return timer;
    }

    /// <summary>Ease the on-screen scale toward the wheel's target, in log space so zooming
    /// in and out feel the same, at the monitor's own frame rate.</summary>
    private void ZoomPreviewStep(object? sender, EventArgs e)
    {
        var zp = _zp;
        if (zp == null || !zp.Eased)
        {
            CompositionTarget.Rendering -= ZoomPreviewStep;
            _zpRendering = false;
            return;
        }

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = Math.Clamp((now - _zpLast) / (double)System.Diagnostics.Stopwatch.Frequency, 0.001, 0.05);
        _zpLast = now;

        double current = Math.Log(zp.Factor), target = Math.Log(zp.TargetFactor);
        current += (target - current) * (1 - Math.Exp(-24 * dt));
        bool arrived = Math.Abs(target - current) < 0.0004;
        zp.Factor = arrived ? zp.TargetFactor : Math.Exp(current);
        ApplyZoomPreview(zp);

        if (arrived)
        {
            CompositionTarget.Rendering -= ZoomPreviewStep;
            _zpRendering = false;
        }
    }

    // ------------------------------------------------------------- anchoring

    /// <summary>Which page is under a viewport row, and how far into it (in page points),
    /// read from the real page containers rather than the panel's size estimates.</summary>
    private static bool TryAnchorFromContainers(ListBox lb, ScrollViewer sv, double y, double zoom,
        out int page, out double innerPt)
    {
        page = -1;
        innerPt = 0;
        if (FindDescendant<VirtualizingPanel>(sv) is not { } panel) return false;

        double best = double.MaxValue;
        int count = VisualTreeHelper.GetChildrenCount(panel);
        for (int c = 0; c < count; c++)
        {
            if (VisualTreeHelper.GetChild(panel, c) is not FrameworkElement fe || !fe.IsVisible || fe.ActualHeight <= 0)
                continue;
            int idx = lb.ItemContainerGenerator.IndexFromContainer(fe);
            if (idx < 0) continue;

            double top;
            try { top = fe.TransformToAncestor(sv).Transform(new Point(0, 0)).Y; }
            catch (InvalidOperationException) { continue; }
            double bottom = top + fe.ActualHeight;

            double distance = y < top ? top - y : y >= bottom ? y - bottom : 0;
            if (distance < best)
            {
                best = distance;
                page = idx;
                innerPt = (y - top - PageGap / 2) / zoom;
                if (distance == 0) break;
            }
        }
        return page >= 0;
    }

    /// <summary>After a zoom, put the anchored point of <paramref name="page"/> back at
    /// <paramref name="viewportY"/>. The panel's offsets are estimates for pages it hasn't
    /// built, so this realizes the page if needed and nudges against its real position until
    /// it sits where it should — without it a big or mixed-size document could land on a
    /// different page after zooming.</summary>
    private static void PinAnchor(ListBox lb, ScrollViewer sv, int page, double innerPt, double zoom, double viewportY)
    {
        double desired = viewportY - PageGap / 2 - innerPt * zoom;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var item = lb.ItemContainerGenerator.ContainerFromIndex(page) as FrameworkElement;
            if (item is not { ActualHeight: > 0 })
            {
                if (page < 0 || page >= lb.Items.Count) return;
                lb.ScrollIntoView(lb.Items[page]); // realizes the page wherever the estimate put us
                lb.UpdateLayout();
                item = lb.ItemContainerGenerator.ContainerFromIndex(page) as FrameworkElement;
                if (item is not { ActualHeight: > 0 }) return;
            }

            double top;
            try { top = item.TransformToAncestor(sv).Transform(new Point(0, 0)).Y; }
            catch (InvalidOperationException) { return; }

            double delta = top - desired;
            if (Math.Abs(delta) <= 0.75) return;

            double before = sv.VerticalOffset;
            sv.ScrollToVerticalOffset(Math.Max(0, before + delta));
            lb.UpdateLayout();
            if (Math.Abs(sv.VerticalOffset - before) < 0.5) return; // hit the end of the document
        }
    }
}
