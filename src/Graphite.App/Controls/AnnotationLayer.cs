using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Graphite.App.ViewModels;
using Graphite.Core;
using Graphite.Core.Annotations;

namespace Graphite.App.Controls;

/// <summary>
/// Interactive overlay for one page: draws annotations, search hits and text
/// selection, and handles the active tool's mouse input. DataContext = PageViewModel.
/// </summary>
public sealed class AnnotationLayer : FrameworkElement
{
    private PageViewModel? _page;
    private DocumentViewModel? _doc;

    private bool _dragging;
    private Point _start;                 // page points
    private Point _current;
    private readonly List<PointD> _inkPoints = new();
    private IReadOnlyList<RectD> _liveWordRects = Array.Empty<RectD>();

    // eraser
    private bool _erasing;
    private Point? _eraserHoverPage;       // page points; null when the cursor isn't over the page

    // moving/resizing a placed image
    private int _pendingCorner = -1;      // 0=TL 1=TR 2=BR 3=BL, -1 none
    private PendingImage? _resizingImage;
    private PendingImage? _movingImage;
    private Point _moveGrab;              // grab offset in page points, for images and annotations alike

    // moving/resizing a bounds-based annotation (text box, rectangle, ellipse, note)
    private AnnotationViewModel? _movingAnnotation;
    private AnnotationViewModel? _resizingAnnotation;

    public AnnotationLayer()
    {
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    // -------------------------------------------------------------- wiring

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        Detach();
        _page = e.NewValue as PageViewModel;
        _doc = _page?.Doc;
        if (_page != null)
        {
            _page.PropertyChanged += OnPagePropertyChanged;
            if (_doc != null)
            {
                _doc.AnnotationsVisualChanged += InvalidateVisualSafe;
                _doc.PropertyChanged += OnDocPropertyChanged;
            }
        }
        InvalidateVisual();
    }

    private void Detach()
    {
        if (_page != null) _page.PropertyChanged -= OnPagePropertyChanged;
        if (_doc != null)
        {
            _doc.AnnotationsVisualChanged -= InvalidateVisualSafe;
            _doc.PropertyChanged -= OnDocPropertyChanged;
        }
        _page = null;
        _doc = null;
    }

    private void OnPagePropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PageViewModel.SearchRects) or nameof(PageViewModel.SelectionRects))
            InvalidateVisualSafe();
    }

    private void OnDocPropertyChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentViewModel.ActiveTool))
        {
            Cursor = _doc?.ActiveTool == ToolKind.Select ? Cursors.Arrow : Cursors.Cross;
            // Switching away from (or to) the eraser invalidates any stale size-preview circle.
            _eraserHoverPage = null;
            InvalidateVisualSafe();
        }
    }

    private void InvalidateVisualSafe()
    {
        if (Dispatcher.CheckAccess()) InvalidateVisual();
        else Dispatcher.BeginInvoke(InvalidateVisual);
    }

    private double Scale => _page == null || _page.WidthPt <= 0 ? 1 : ActualWidth / _page.WidthPt;

    private Point ToPage(Point px) => new(px.X / Scale, px.Y / Scale);

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_eraserHoverPage == null) return;
        _eraserHoverPage = null;
        InvalidateVisual();
    }

    // -------------------------------------------------------------- input

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_page == null || _doc == null) return;
        _start = _current = ToPage(e.GetPosition(this));
        var tool = _doc.ActiveTool;

        if (tool == ToolKind.Note)
        {
            var model = new Annotation
            {
                Kind = AnnotationKind.Note,
                PageIndex = _page.Index,
                Bounds = new RectD(_start.X, _start.Y, 18, 18),
                ColorHex = "#F2C744",
            };
            _doc.AddAnnotation(model);
            return;
        }

        if (tool == ToolKind.Eraser)
        {
            _doc.PushUndo();
            _erasing = true;
            CaptureMouse();
            EraseAt(_start);
            e.Handled = true;
            return;
        }

        if (tool == ToolKind.Select)
        {
            var posPx = e.GetPosition(this);
            double s = Scale;

            // A corner drag on the already-selected image resizes it.
            if (_doc.SelectedImage is { } selImg && selImg.PageIndex == _page.Index)
            {
                int corner = HitCorner(selImg.Rect, posPx, s);
                if (corner >= 0)
                {
                    _doc.PushUndo();
                    _resizingImage = selImg;
                    _pendingCorner = corner;
                    CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }

            // A corner drag on the already-selected bounds-based annotation resizes it.
            if (_doc.SelectedAnnotation is { } selAnn && selAnn.PageIndex == _page.Index &&
                IsBoundsMovable(selAnn.Model.Kind))
            {
                int corner = HitCorner(selAnn.Model.Bounds.Inflate(3), posPx, s);
                if (corner >= 0)
                {
                    _doc.PushUndo();
                    _resizingAnnotation = selAnn;
                    _pendingCorner = corner;
                    CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }

            // Images are drawn on top of everything else — hit-test them first.
            var hitImg = _doc.PlacedImages.LastOrDefault(im =>
                im.PageIndex == _page.Index && im.Rect.Inflate(3).Contains(new PointD(_start.X, _start.Y)));
            if (hitImg != null)
            {
                _doc.SelectedImage = hitImg;
                _doc.SelectedAnnotation = null;
                _doc.PushUndo();
                _movingImage = hitImg;
                _moveGrab = new Point(_start.X - hitImg.Rect.X, _start.Y - hitImg.Rect.Y);
                CaptureMouse();
                e.Handled = true;
                return;
            }

            // Then annotations. Bounds-based kinds (text boxes, shapes, notes) can be dragged.
            var hit = _doc.Annotations.LastOrDefault(a =>
                a.PageIndex == _page.Index && a.Model.Bounds.Inflate(3).Contains(new PointD(_start.X, _start.Y)));
            if (hit != null)
            {
                _doc.SelectedAnnotation = hit;
                _doc.SelectedImage = null;

                // Double-click a text box to edit its text/size/formatting in place.
                if (e.ClickCount == 2 && hit.Model.Kind == AnnotationKind.FreeText)
                {
                    _doc.RequestEditFreeText(hit);
                    e.Handled = true;
                    return;
                }

                if (IsBoundsMovable(hit.Model.Kind))
                {
                    _doc.PushUndo();
                    _movingAnnotation = hit;
                    _moveGrab = new Point(_start.X - hit.Model.Bounds.X, _start.Y - hit.Model.Bounds.Y);
                    CaptureMouse();
                    e.Handled = true;
                }
                return;
            }

            _doc.SelectedAnnotation = null;
            _doc.SelectedImage = null;
            _doc.ClearTextSelection();
        }

        _dragging = true;
        _inkPoints.Clear();
        if (tool == ToolKind.Ink || IsFreehandHighlight)
            _inkPoints.Add(new PointD(_start.X, _start.Y));
        CaptureMouse();
        e.Handled = true;
    }

    /// <summary>True while the Highlight tool is active and set to freehand-marker mode.</summary>
    private bool IsFreehandHighlight => _doc?.ActiveTool == ToolKind.Highlight && _doc.HighlightFreehand;

    /// <summary>Annotation kinds that render from Bounds directly (rather than quads/strokes/
    /// line endpoints), so a plain drag can reposition them.</summary>
    private static bool IsBoundsMovable(AnnotationKind kind) =>
        kind is AnnotationKind.FreeText or AnnotationKind.Square or AnnotationKind.Circle or AnnotationKind.Note;

    /// <summary>Whether the selection dash-rectangle makes sense for this annotation. Freehand
    /// strokes (ink, arrows, the freehand highlighter) get auto-selected the moment they're
    /// drawn, but their bounding box can't be dragged to resize them the way a shape or text
    /// box's can — so the box was pure visual noise sitting around every line you just drew.
    /// Shapes, text, notes and word-snapped highlights still show it since it doubles as a
    /// resize/move affordance for those.</summary>
    private static bool ShowsSelectionBox(Annotation a) =>
        a.Kind switch
        {
            AnnotationKind.Ink => false,
            AnnotationKind.Line => false,
            AnnotationKind.Highlight when a.IsFreehand => false,
            _ => true,
        };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_page == null || _doc == null) return;

        if (_doc.ActiveTool == ToolKind.Eraser)
        {
            var p = ToPage(e.GetPosition(this));
            _eraserHoverPage = p;
            if (_erasing) EraseAt(p);
            InvalidateVisual();
            return;
        }

        if (_resizingImage is { } ri)
        {
            var p = ToPage(e.GetPosition(this));
            var r = ri.Rect;
            // Anchor is the corner opposite the one being dragged.
            var (ax, ay) = _pendingCorner switch
            {
                0 => (r.Right, r.Bottom),
                1 => (r.Left, r.Bottom),
                2 => (r.Left, r.Top),
                _ => (r.Right, r.Top),
            };
            var nr = RectD.FromCorners(ax, ay, p.X, p.Y);
            if (nr.Width > 8 && nr.Height > 8) ri.Rect = nr;
            _doc.NotifyAnnotationChanged();
            return;
        }

        if (_movingImage is { } mi)
        {
            var p = ToPage(e.GetPosition(this));
            mi.Rect = new RectD(p.X - _moveGrab.X, p.Y - _moveGrab.Y, mi.Rect.Width, mi.Rect.Height);
            _doc.NotifyAnnotationChanged();
            return;
        }

        if (_resizingAnnotation is { } ra)
        {
            var p = ToPage(e.GetPosition(this));
            var r = ra.Model.Bounds;
            // Anchor is the corner opposite the one being dragged.
            var (ax, ay) = _pendingCorner switch
            {
                0 => (r.Right, r.Bottom),
                1 => (r.Left, r.Bottom),
                2 => (r.Left, r.Top),
                _ => (r.Right, r.Top),
            };
            var nr = RectD.FromCorners(ax, ay, p.X, p.Y);
            if (nr.Width > 12 && nr.Height > 12) ra.Model.Bounds = nr;
            _doc.NotifyAnnotationChanged();
            return;
        }

        if (_movingAnnotation is { } ma)
        {
            var p = ToPage(e.GetPosition(this));
            var b = ma.Model.Bounds;
            ma.Model.Bounds = new RectD(p.X - _moveGrab.X, p.Y - _moveGrab.Y, b.Width, b.Height);
            _doc.NotifyAnnotationChanged();
            return;
        }

        if (!_dragging) return;
        _current = ToPage(e.GetPosition(this));

        switch (_doc.ActiveTool)
        {
            case ToolKind.Ink:
            case ToolKind.Highlight when IsFreehandHighlight:
                AppendSmoothed(_inkPoints, new PointD(_current.X, _current.Y));
                break;
            case ToolKind.Highlight or ToolKind.Underline or ToolKind.StrikeOut or ToolKind.Select:
                _liveWordRects = _doc.Index
                    .WordsInRect(_page.Index, DragRect())
                    .Select(w => w.Box).ToList();
                break;
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_erasing)
        {
            _erasing = false;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (_resizingImage != null || _movingImage != null || _movingAnnotation != null || _resizingAnnotation != null)
        {
            _resizingImage = null;
            _movingImage = null;
            _movingAnnotation = null;
            _resizingAnnotation = null;
            _pendingCorner = -1;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
        if (!_dragging || _page == null || _doc == null) return;
        _dragging = false;
        ReleaseMouseCapture();
        _current = ToPage(e.GetPosition(this));
        var rect = DragRect();
        bool moved = rect.Width > 2 || rect.Height > 2;

        switch (_doc.ActiveTool)
        {
            case ToolKind.Select when moved:
                _doc.SetTextSelection(_page.Index, rect);
                break;

            case ToolKind.Highlight when IsFreehandHighlight && _inkPoints.Count >= 1:
                CreateFreehandHighlight();
                break;

            case ToolKind.Highlight or ToolKind.Underline or ToolKind.StrikeOut when moved:
                CreateTextMarkup(rect);
                break;

            case ToolKind.Ink when _inkPoints.Count >= 1:
                CreateInk();
                break;

            case ToolKind.Rect or ToolKind.Ellipse when moved:
                _doc.AddAnnotation(new Annotation
                {
                    Kind = _doc.ActiveTool == ToolKind.Rect ? AnnotationKind.Square : AnnotationKind.Circle,
                    PageIndex = _page.Index,
                    Bounds = rect,
                    ColorHex = _doc.ActiveColorHex,
                    StrokeWidth = _doc.ActiveStrokeWidth,
                });
                break;

            case ToolKind.EditText when moved:
                _doc.RequestEditText(_page.Index, rect);
                break;

            case ToolKind.PlaceImage when moved:
                _doc.RequestPlaceImage(_page.Index, rect);
                break;

            case ToolKind.Text:
                // Click gives a sensible default box; a drag defines it exactly.
                _doc.RequestFreeText(_page.Index,
                    moved ? rect : new RectD(_start.X, _start.Y, 180, 44));
                break;

            case ToolKind.Arrow when moved:
                _doc.AddAnnotation(new Annotation
                {
                    Kind = AnnotationKind.Line,
                    PageIndex = _page.Index,
                    LineStart = new PointD(_start.X, _start.Y),
                    LineEnd = new PointD(_current.X, _current.Y),
                    Bounds = rect.Inflate(4),
                    ColorHex = _doc.ActiveColorHex,
                    StrokeWidth = _doc.ActiveStrokeWidth,
                });
                break;

            case ToolKind.Signature:
                PlaceSignature(moved ? rect : new RectD(_start.X, _start.Y, 150, 0));
                break;
        }

        _liveWordRects = Array.Empty<RectD>();
        _inkPoints.Clear();
        InvalidateVisual();
    }

    private RectD DragRect() =>
        RectD.FromCorners(_start.X, _start.Y, _current.X, _current.Y);

    private void CreateTextMarkup(RectD rect)
    {
        var words = _doc!.Index.WordsInRect(_page!.Index, rect);
        if (words.Count == 0) return;

        // Merge word boxes on the same visual line into single quads.
        var quads = new List<RectD>();
        foreach (var w in words.OrderBy(w => w.Box.Top).ThenBy(w => w.Box.Left))
        {
            if (quads.Count > 0)
            {
                var last = quads[^1];
                bool sameLine = Math.Abs(last.Top - w.Box.Top) < Math.Max(last.Height, w.Box.Height) * 0.5;
                if (sameLine && w.Box.Left - last.Right < 40)
                {
                    quads[^1] = last.Union(w.Box);
                    continue;
                }
            }
            quads.Add(w.Box);
        }

        var bounds = quads.Aggregate(quads[0], (a, b) => a.Union(b));
        var kind = _doc.ActiveTool switch
        {
            ToolKind.Underline => AnnotationKind.Underline,
            ToolKind.StrikeOut => AnnotationKind.StrikeOut,
            _ => AnnotationKind.Highlight,
        };
        _doc.AddAnnotation(new Annotation
        {
            Kind = kind,
            PageIndex = _page.Index,
            Bounds = bounds,
            Quads = quads,
            ColorHex = _doc.ActiveColorHex,
            StrokeWidth = _doc.ActiveStrokeWidth,
        });
    }

    /// <summary>Stamp the saved signature (normalized strokes) into a page rect as ink.</summary>
    private void PlaceSignature(RectD target)
    {
        var sig = Services.ThemeService.GetSignature();
        if (sig.Count == 0) return;

        double aspect = sig.SelectMany(s => s).Select(p => p[1]).DefaultIfEmpty(0.35).Max();
        if (aspect <= 0) aspect = 0.35;

        double w = target.Width > 8 ? target.Width : 150;
        double h = target.Height > 8 ? target.Height : w * aspect;

        var strokes = sig.Select(s => s
            .Select(p => new PointD(target.X + p[0] * w, target.Y + p[1] / aspect * h))
            .ToList()).ToList();

        var ann = new Annotation
        {
            Kind = AnnotationKind.Ink,
            PageIndex = _page!.Index,
            Bounds = new RectD(target.X, target.Y, w, h).Inflate(2),
            ColorHex = _doc!.ActiveColorHex,
            StrokeWidth = Math.Max(1.0, w / 110.0),
            Contents = "Signature",
        };
        ann.Strokes.AddRange(strokes);
        _doc.AddAnnotation(ann);
        _doc.ActiveTool = ToolKind.Select;
    }

    /// <summary>Eraser radius in page points. Reuses the "line thickness" tool setting
    /// (x3, floor 6pt) so there's no separate size control to add to the toolbar.</summary>
    private double EraserRadius => Math.Max(6.0, (_doc?.ActiveStrokeWidth ?? 4.0) * 3.0);

    /// <summary>Erase whatever ink/freehand-highlighter passes within <see cref="EraserRadius"/>
    /// of <paramref name="pagePt"/>. Unlike a whole-annotation delete, this only removes the
    /// touched portion of each stroke: any run of points that survives becomes its own stroke,
    /// so drawing a line through the middle of a scribble splits it in two rather than deleting
    /// the whole thing. An annotation is removed outright only once every one of its strokes has
    /// been fully erased.</summary>
    private void EraseAt(Point pagePt)
    {
        if (_doc == null || _page == null) return;
        double r2 = EraserRadius * EraserRadius;
        var toRemove = new List<AnnotationViewModel>();

        foreach (var vm in _doc.Annotations.Where(a =>
                     a.PageIndex == _page.Index &&
                     (a.Model.Kind == AnnotationKind.Ink ||
                      (a.Model.Kind == AnnotationKind.Highlight && a.Model.IsFreehand))).ToList())
        {
            var model = vm.Model;
            var newStrokes = new List<List<PointD>>();
            bool changed = false;

            foreach (var stroke in model.Strokes)
            {
                List<PointD>? run = null;
                foreach (var p in stroke)
                {
                    double dx = p.X - pagePt.X, dy = p.Y - pagePt.Y;
                    if (dx * dx + dy * dy <= r2)
                    {
                        changed = true;
                        if (run is { Count: >= 2 }) newStrokes.Add(run);
                        run = null;
                    }
                    else
                    {
                        (run ??= new List<PointD>()).Add(p);
                    }
                }
                if (run is { Count: >= 2 }) newStrokes.Add(run);
                else if (run != null) changed = true; // a leftover single point isn't a visible stroke
            }

            if (!changed) continue;

            if (newStrokes.Count == 0)
            {
                toRemove.Add(vm);
                continue;
            }

            model.Strokes = newStrokes;
            var allPts = newStrokes.SelectMany(s => s).ToList();
            double minX = allPts.Min(p => p.X), minY = allPts.Min(p => p.Y);
            double maxX = allPts.Max(p => p.X), maxY = allPts.Max(p => p.Y);
            model.Bounds = new RectD(minX, minY, maxX - minX, maxY - minY).Inflate(2);
            model.Modified = DateTime.Now;
        }

        if (toRemove.Count == 0)
        {
            _doc.NotifyAnnotationChanged();
            return;
        }

        foreach (var vm in toRemove)
        {
            if (_doc.SelectedAnnotation == vm) _doc.SelectedAnnotation = null;
            _doc.Annotations.Remove(vm);
        }
        _doc.NotifyAnnotationChanged();
    }

    /// <summary>Appends a raw pointer sample to an in-progress ink/freehand-highlight stroke,
    /// gating out oversampled near-duplicate points and blending toward the new sample rather
    /// than jumping straight to it. <see cref="StrokeSmoothing"/> already fits a curve through
    /// every recorded point, so cleaning up the input here — not the curve fit — is what turns a
    /// shaky mouse trace into a fluid-looking stroke.</summary>
    private static void AppendSmoothed(List<PointD> pts, PointD raw)
    {
        const double minSpacing = 1.2;  // page points
        const double smoothing = 0.55;  // 0 = raw input, 1 = frozen

        if (pts.Count == 0) { pts.Add(raw); return; }

        var last = pts[^1];
        double dx = raw.X - last.X, dy = raw.Y - last.Y;
        if (dx * dx + dy * dy < minSpacing * minSpacing) return;

        pts.Add(new PointD(
            last.X + (raw.X - last.X) * (1 - smoothing),
            last.Y + (raw.Y - last.Y) * (1 - smoothing)));
    }

    private void CreateInk()
    {
        var pts = _inkPoints.ToList();
        // A simple tap (dotting an i) yields a single point — make it a visible dot.
        if (pts.Count == 1)
            pts.Add(new PointD(pts[0].X + 0.3, pts[0].Y + 0.3));
        double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y);
        double maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
        _doc!.AddAnnotation(new Annotation
        {
            Kind = AnnotationKind.Ink,
            PageIndex = _page!.Index,
            Bounds = new RectD(minX, minY, maxX - minX, maxY - minY).Inflate(2),
            Strokes = { pts },
            ColorHex = "#3E6DB5",
            StrokeWidth = 1.8,
        });
    }

    /// <summary>A freehand marker stroke — like Ink, but wide and translucent, and it
    /// snaps to nothing: it just follows the cursor, like a real highlighter pen.</summary>
    private void CreateFreehandHighlight()
    {
        var pts = _inkPoints.ToList();
        if (pts.Count == 1)
            pts.Add(new PointD(pts[0].X + 0.3, pts[0].Y + 0.3));
        double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y);
        double maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
        double w = DocumentViewModel.FreehandHighlightWidth;
        _doc!.AddAnnotation(new Annotation
        {
            Kind = AnnotationKind.Highlight,
            IsFreehand = true,
            PageIndex = _page!.Index,
            Bounds = new RectD(minX, minY, maxX - minX, maxY - minY).Inflate(w / 2),
            Strokes = { pts },
            ColorHex = _doc.ActiveColorHex,
            StrokeWidth = w,
            Opacity = DocumentViewModel.FreehandHighlightOpacity,
        });
    }

    // -------------------------------------------------------------- painting

    protected override void OnRender(DrawingContext dc)
    {
        // Transparent hit-test surface over the whole page.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_page == null || _doc == null) return;
        double s = Scale;

        // search hits
        if (_page.SearchRects is { Count: > 0 } search)
        {
            var brush = Frozen((Color)FindResource("App.SearchHighlightColor"), 0.35);
            foreach (var r in search)
                dc.DrawRoundedRectangle(brush, null, ToRect(r.Inflate(1), s), 2, 2);
        }

        // text selection
        if (_page.SelectionRects is { Count: > 0 } sel)
        {
            var brush = Frozen((Color)FindResource("App.SelectionColor"), 0.30);
            foreach (var r in sel)
                dc.DrawRectangle(brush, null, ToRect(r, s));
        }

        // annotations
        foreach (var vm in _doc.Annotations)
        {
            if (vm.PageIndex != _page.Index) continue;
            DrawAnnotation(dc, vm.Model, s);
            if (_doc.SelectedAnnotation == vm && ShowsSelectionBox(vm.Model))
            {
                var selRect = ToRect(vm.Model.Bounds.Inflate(3), s);
                dc.DrawRectangle(null, SelectionDashPen, selRect);

                // Bounds-based annotations (text boxes, shapes, notes) get corner handles to resize.
                if (IsBoundsMovable(vm.Model.Kind))
                {
                    foreach (var c in Corners(selRect))
                        dc.DrawRectangle(Brushes.White, HandlePen, new Rect(c.X - 4, c.Y - 4, 8, 8));
                }
            }
        }

        DrawInProgress(dc, s);
        DrawImages(dc, s);
        DrawEraserCursor(dc, s);
    }

    /// <summary>Shows the eraser's reach as a circle centered on the cursor, so its size
    /// (driven by the line-thickness menu) is visible before and while erasing.</summary>
    private void DrawEraserCursor(DrawingContext dc, double s)
    {
        if (_doc?.ActiveTool != ToolKind.Eraser || _eraserHoverPage is not { } p) return;
        double radius = EraserRadius * s;
        var center = new Point(p.X * s, p.Y * s);
        dc.DrawEllipse(EraserFillBrush, EraserRingPen, center, radius, radius);
    }

    /// <summary>Placed images — drawn on top since they're opaque bitmaps. The selected
    /// one gets a dashed outline and corner handles for moving/resizing.</summary>
    private void DrawImages(DrawingContext dc, double s)
    {
        if (_doc == null) return;
        foreach (var img in _doc.PlacedImages)
        {
            if (img.PageIndex != _page!.Index) continue;
            var r = ToRect(img.Rect, s);
            dc.DrawImage(img.Bitmap, r);

            if (_doc.SelectedImage != img) continue;
            dc.DrawRectangle(null, ImageDashPen, r);

            foreach (var c in Corners(r))
                dc.DrawRectangle(Brushes.White, HandlePen, new Rect(c.X - 4, c.Y - 4, 8, 8));
        }
    }

    private static Point[] Corners(Rect r) => new[]
    {
        new Point(r.Left, r.Top), new Point(r.Right, r.Top),
        new Point(r.Right, r.Bottom), new Point(r.Left, r.Bottom),
    };

    private int HitCorner(RectD rect, Point posPx, double s)
    {
        var r = ToRect(rect, s);
        var corners = Corners(r);
        for (int i = 0; i < 4; i++)
            if (Math.Abs(posPx.X - corners[i].X) <= 7 && Math.Abs(posPx.Y - corners[i].Y) <= 7)
                return i;
        return -1;
    }

    private void DrawAnnotation(DrawingContext dc, Annotation a, double s)
    {
        switch (a.Kind)
        {
            case AnnotationKind.Highlight when a.IsFreehand:
                var fhp = FrozenPen(new Pen(Brush(a.ColorHex, a.Opacity), Math.Max(4.0, a.StrokeWidth * s))
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
                foreach (var stroke in a.Strokes) dc.DrawGeometry(null, fhp, Polyline(stroke, s));
                break;

            case AnnotationKind.Highlight:
                var hb = Brush(a.ColorHex, 0.35 * a.Opacity);
                foreach (var q in a.Quads) dc.DrawRectangle(hb, null, ToRect(q, s));
                break;

            case AnnotationKind.Underline:
                var up = FrozenPen(new Pen(Brush(a.ColorHex), Math.Max(1.0, a.StrokeWidth * s)));
                foreach (var q in a.Quads)
                    dc.DrawLine(up, new Point(q.Left * s, q.Bottom * s), new Point(q.Right * s, q.Bottom * s));
                break;

            case AnnotationKind.StrikeOut:
                var sp = FrozenPen(new Pen(Brush(a.ColorHex), Math.Max(1.0, a.StrokeWidth * s)));
                foreach (var q in a.Quads)
                {
                    double midY = (q.Top + q.Bottom) / 2 * s;
                    dc.DrawLine(sp, new Point(q.Left * s, midY), new Point(q.Right * s, midY));
                }
                break;

            case AnnotationKind.Ink:
                var ip = FrozenPen(new Pen(Brush(a.ColorHex, a.Opacity), a.StrokeWidth * s)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
                foreach (var stroke in a.Strokes)
                    dc.DrawGeometry(null, ip, Polyline(stroke, s));
                break;

            case AnnotationKind.Square:
                dc.DrawRectangle(null, FrozenPen(new Pen(Brush(a.ColorHex), a.StrokeWidth * s)), ToRect(a.Bounds, s));
                break;

            case AnnotationKind.Circle:
                var r = ToRect(a.Bounds, s);
                dc.DrawEllipse(null, FrozenPen(new Pen(Brush(a.ColorHex), a.StrokeWidth * s)),
                    new Point(r.X + r.Width / 2, r.Y + r.Height / 2), r.Width / 2, r.Height / 2);
                break;

            case AnnotationKind.FreeText:
                var ftRect = ToRect(a.Bounds, s);
                if (!string.IsNullOrEmpty(a.FillColorHex))
                    dc.DrawRectangle(Brush(a.FillColorHex), null, ftRect);
                if (!string.IsNullOrEmpty(a.BorderColorHex))
                    dc.DrawRectangle(null, FrozenPen(new Pen(Brush(a.BorderColorHex),
                        Math.Max(1.0, a.StrokeWidth * s))), ftRect);
                if (!string.IsNullOrEmpty(a.Contents))
                {
                    var typeface = new Typeface(new FontFamily(a.FontFamily),
                        a.Italic ? FontStyles.Italic : FontStyles.Normal,
                        a.Bold ? FontWeights.Bold : FontWeights.Normal,
                        FontStretches.Normal);
                    var ft = new FormattedText(a.Contents,
                        System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        typeface, a.FontSize * s,
                        Brush(a.ColorHex, a.Opacity),
                        VisualTreeHelper.GetDpi(this).PixelsPerDip)
                    {
                        MaxTextWidth = Math.Max(10, a.Bounds.Width * s),
                        MaxTextHeight = Math.Max(10, a.Bounds.Height * s),
                        Trimming = TextTrimming.None,
                    };
                    if (a.Underline)
                        ft.SetTextDecorations(TextDecorations.Underline);
                    dc.DrawText(ft, new Point(a.Bounds.X * s, a.Bounds.Y * s));
                }
                break;

            case AnnotationKind.Line:
                DrawArrow(dc, FrozenPen(new Pen(Brush(a.ColorHex, a.Opacity), a.StrokeWidth * s)
                    { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }),
                    new Point(a.LineStart.X * s, a.LineStart.Y * s),
                    new Point(a.LineEnd.X * s, a.LineEnd.Y * s), s);
                break;

            case AnnotationKind.Note:
                var nr = ToRect(a.Bounds, s);
                dc.DrawRoundedRectangle(Brush(a.ColorHex), NoteOutlinePen, nr, 3 * s, 3 * s);
                var lp = FrozenPen(new Pen(Frozen(Color.FromArgb(200, 60, 60, 60)), Math.Max(1, s)));
                double inset = nr.Width * 0.22;
                dc.DrawLine(lp, new Point(nr.X + inset, nr.Y + nr.Height * 0.38), new Point(nr.Right - inset, nr.Y + nr.Height * 0.38));
                dc.DrawLine(lp, new Point(nr.X + inset, nr.Y + nr.Height * 0.62), new Point(nr.Right - inset, nr.Y + nr.Height * 0.62));
                break;
        }
    }

    private void DrawInProgress(DrawingContext dc, double s)
    {
        if (!_dragging || _doc == null) return;
        var tool = _doc.ActiveTool;
        var rect = ToRect(DragRect(), s);

        switch (tool)
        {
            case ToolKind.Highlight when IsFreehandHighlight && _inkPoints.Count > 1:
                var hip = FrozenPen(new Pen(
                    Brush(_doc.ActiveColorHex, DocumentViewModel.FreehandHighlightOpacity),
                    Math.Max(4.0, DocumentViewModel.FreehandHighlightWidth * s))
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
                dc.DrawGeometry(null, hip, Polyline(_inkPoints, s));
                break;

            case ToolKind.Highlight or ToolKind.Underline or ToolKind.StrikeOut or ToolKind.Select:
                foreach (var r in _liveWordRects) dc.DrawRectangle(LiveWordBrush, null, ToRect(r, s));
                dc.DrawRectangle(null, LiveWordPen, rect);
                break;

            case ToolKind.Ink when _inkPoints.Count > 1:
                var ip = FrozenPen(new Pen(Brush(_doc.ActiveColorHex), _doc.ActiveStrokeWidth * s)
                { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
                dc.DrawGeometry(null, ip, Polyline(_inkPoints, s));
                break;

            case ToolKind.Rect or ToolKind.EditText or ToolKind.PlaceImage
                or ToolKind.Text or ToolKind.Signature:
                dc.DrawRectangle(null, DashPen(), rect);
                break;

            case ToolKind.Arrow:
                DrawArrow(dc,
                    FrozenPen(new Pen(Brush(_doc.ActiveColorHex), _doc.ActiveStrokeWidth * s)
                        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }),
                    new Point(_start.X * s, _start.Y * s),
                    new Point(_current.X * s, _current.Y * s), s);
                break;

            case ToolKind.Ellipse:
                dc.DrawEllipse(null, DashPen(),
                    new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), rect.Width / 2, rect.Height / 2);
                break;
        }
    }

    private static void DrawArrow(DrawingContext dc, Pen pen, Point from, Point to, double s)
    {
        dc.DrawLine(pen, from, to);
        var v = to - from;
        if (v.Length < 0.01) return;
        v.Normalize();
        double head = Math.Max(8, pen.Thickness * 3.5) + 2 * s;
        var left = new Vector(
            v.X * Math.Cos(2.65) - v.Y * Math.Sin(2.65),
            v.X * Math.Sin(2.65) + v.Y * Math.Cos(2.65));
        var right = new Vector(
            v.X * Math.Cos(-2.65) - v.Y * Math.Sin(-2.65),
            v.X * Math.Sin(-2.65) + v.Y * Math.Cos(-2.65));
        dc.DrawLine(pen, to, to + left * head);
        dc.DrawLine(pen, to, to + right * head);
    }

    private static Pen DashPen() => CachedDashPen;

    private static Rect ToRect(RectD r, double s) => new(r.X * s, r.Y * s, r.Width * s, r.Height * s);

    // -------------------------------------------------------------- frozen resources
    // OnRender runs on every mouse-move while drawing/erasing; frozen brushes and pens
    // skip WPF's per-use change tracking and can be shared across renders, so all the
    // freezables below are created once (or cached per color) instead of per frame.

    private static readonly Dictionary<(string Hex, double Opacity), SolidColorBrush> BrushCache = new();

    private static SolidColorBrush Brush(string hex, double opacity = 1.0)
    {
        var key = (hex, opacity);
        if (BrushCache.TryGetValue(key, out var cached)) return cached;
        var b = new SolidColorBrush(ParseColor(hex)) { Opacity = opacity };
        b.Freeze();
        BrushCache[key] = b;
        return b;
    }

    private static SolidColorBrush Frozen(Color c, double opacity = 1.0)
    {
        var b = new SolidColorBrush(c) { Opacity = opacity };
        b.Freeze();
        return b;
    }

    private static Pen FrozenPen(Pen p) { p.Freeze(); return p; }

    private static readonly Pen CachedDashPen = FrozenPen(
        new Pen(Frozen(Color.FromArgb(180, 130, 130, 130)), 1) { DashStyle = DashStyles.Dash });

    private static readonly Pen SelectionDashPen = FrozenPen(
        new Pen(Frozen(Color.FromArgb(180, 128, 128, 128)), 1) { DashStyle = DashStyles.Dash });

    private static readonly Pen ImageDashPen = FrozenPen(
        new Pen(Frozen(Color.FromArgb(220, 90, 122, 153)), 1.4) { DashStyle = DashStyles.Dash });

    private static readonly Pen HandlePen = FrozenPen(
        new Pen(Frozen(Color.FromArgb(255, 90, 122, 153)), 1.2));

    private static readonly Pen EraserRingPen = FrozenPen(
        new Pen(Frozen(Color.FromArgb(170, 90, 90, 90)), 1));

    private static readonly SolidColorBrush EraserFillBrush = Frozen(Color.FromArgb(40, 110, 110, 110));
    private static readonly SolidColorBrush LiveWordBrush = Frozen(Color.FromArgb(70, 120, 140, 160));
    private static readonly Pen LiveWordPen = FrozenPen(
        new Pen(Frozen(Color.FromArgb(120, 120, 140, 160)), 1));

    private static readonly Pen NoteOutlinePen = FrozenPen(
        new Pen(Frozen(Color.FromArgb(160, 60, 60, 60)), 1));

    private static StreamGeometry Polyline(IReadOnlyList<PointD> pts, double s)
    {
        var scaled = new Point[pts.Count];
        for (int i = 0; i < pts.Count; i++)
            scaled[i] = new Point(pts[i].X * s, pts[i].Y * s);
        return StrokeSmoothing.ToSmoothGeometry(scaled);
    }

    private static readonly Dictionary<string, Color> ColorCache = new();

    private static Color ParseColor(string hex)
    {
        if (ColorCache.TryGetValue(hex, out var cached)) return cached;
        Color c;
        try { c = (Color)ColorConverter.ConvertFromString(hex); }
        catch { c = Colors.Gray; }
        ColorCache[hex] = c;
        return c;
    }
}
