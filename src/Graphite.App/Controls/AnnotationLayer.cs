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

    // lasso tool: draw a loop around markup to grab it, then drag it or recolour/delete it
    private readonly List<PointD> _lassoPoints = new();
    private bool _lassoDrawing;
    private bool _lassoMoving;
    private const double ChipRadius = 10;   // the × delete chip on a lasso selection

    // The undo snapshot for a drag gesture (erase / move / resize) is pushed lazily on
    // the first actual change — a click that starts a gesture but never modifies
    // anything must not leave a no-op entry on the undo stack.
    private bool _gestureUndoPushed;

    private void EnsureGestureUndo()
    {
        if (_gestureUndoPushed) return;
        _gestureUndoPushed = true;
        _doc?.PushUndo();
    }

    public AnnotationLayer()
    {
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();

        // Windows' pen gesture layer is what makes WPF handwriting feel "off": press-and-hold
        // waits to see whether a touch is a long press, flicks swallow fast strokes as
        // gestures, and tap/touch feedback draws ripples. None of that is wanted while inking.
        Stylus.SetIsPressAndHoldEnabled(this, false);
        Stylus.SetIsFlicksEnabled(this, false);
        Stylus.SetIsTapFeedbackEnabled(this, false);
        Stylus.SetIsTouchFeedbackEnabled(this, false);

        AddVisualChild(_liveInkVisual);
    }

    // -------------------------------------------------------------- live ink

    // The stroke being written lives in its own visual, so every pen sample repaints just
    // that one stroke instead of re-running OnRender for every annotation, search hit and
    // text box on the page. That repaint was the main source of lag on busy pages.
    private readonly DrawingVisual _liveInkVisual = new();

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _liveInkVisual : throw new ArgumentOutOfRangeException(nameof(index));

    private void UpdateLiveInk()
    {
        using var dc = _liveInkVisual.RenderOpen();
        if (_doc == null) return;
        double s = Scale;

        if (_lassoDrawing)
        {
            if (_lassoPoints.Count >= 2) DrawLassoLoop(dc, s);
            return;
        }
        if (!_dragging || _inkPoints.Count < 2) return;

        if (_doc.ActiveTool == ToolKind.Ink)
        {
            var pen = FrozenPen(new Pen(Brush(_doc.ActiveColorHex), _doc.ActiveStrokeWidth * s)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
            dc.DrawGeometry(null, pen, Polyline(_inkPoints, s));
        }
        else if (IsFreehandHighlight)
        {
            var pen = FrozenPen(new Pen(
                Brush(_doc.ActiveColorHex, DocumentViewModel.FreehandHighlightOpacity),
                Math.Max(4.0, DocumentViewModel.FreehandHighlightWidth * s))
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
            dc.DrawGeometry(null, pen, Polyline(_inkPoints, s));
        }
    }

    private void ClearLiveInk() => _liveInkVisual.RenderOpen().Close();

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
        _lassoDrawing = false;
        _lassoMoving = false;
        _lassoPoints.Clear();
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
            if (_lassoDrawing)
            {
                _lassoDrawing = false;
                _lassoPoints.Clear();
                ClearLiveInk();
            }
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

    /// <summary>The capture was taken away mid-gesture — a second finger started a pinch, a
    /// dialog opened, the window lost focus. Abandon whatever was in progress instead of
    /// leaving a half-drawn stroke or a drag that never ends. (Normal gestures clear their
    /// flags before releasing the capture, so they never land here.)</summary>
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        bool any = _lassoDrawing || _lassoMoving || _dragging || _erasing ||
                   _resizingImage != null || _movingImage != null ||
                   _movingAnnotation != null || _resizingAnnotation != null;
        if (!any) return;

        _lassoDrawing = false;
        _lassoMoving = false;
        _lassoPoints.Clear();
        _dragging = false;
        _erasing = false;
        _resizingImage = null;
        _movingImage = null;
        _movingAnnotation = null;
        _resizingAnnotation = null;
        _pendingCorner = -1;
        _inkPoints.Clear();
        _liveWordRects = Array.Empty<RectD>();
        ClearLiveInk();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_page == null || _doc == null) return;
        _start = _current = ToPage(e.GetPosition(this));
        var tool = _doc.ActiveTool;

        if (tool == ToolKind.Lasso)
        {
            BeginLasso(e);
            return;
        }

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
            _gestureUndoPushed = false;
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
                    _gestureUndoPushed = false;
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
                    _gestureUndoPushed = false;
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
                _gestureUndoPushed = false;
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
                    _gestureUndoPushed = false;
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

        if (_doc.ActiveTool == ToolKind.Lasso)
        {
            OnLassoMove(e);
            return;
        }

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
            if (nr.Width > 8 && nr.Height > 8)
            {
                EnsureGestureUndo();
                ri.Rect = nr;
                _doc.NotifyAnnotationChanged();
            }
            return;
        }

        if (_movingImage is { } mi)
        {
            var p = ToPage(e.GetPosition(this));
            EnsureGestureUndo();
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
            if (nr.Width > 12 && nr.Height > 12)
            {
                EnsureGestureUndo();
                ra.Model.Bounds = nr;
                _doc.NotifyAnnotationChanged();
            }
            return;
        }

        if (_movingAnnotation is { } ma)
        {
            var p = ToPage(e.GetPosition(this));
            var b = ma.Model.Bounds;
            EnsureGestureUndo();
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
                foreach (var sample in InkSamples(e))
                    AppendSmoothed(_inkPoints, sample);
                UpdateLiveInk();
                return;   // only the live stroke changed — skip the full-layer repaint
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
        if (_lassoMoving)
        {
            _lassoMoving = false;
            ReleaseMouseCapture();
            e.Handled = true;
            InvalidateVisual();
            return;
        }
        if (_lassoDrawing)
        {
            FinishLasso();
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }
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
        ClearLiveInk();
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
                // Click gives a sensible default box; a drag defines it exactly. Typing
                // starts immediately, inline on the page — no dialog. Double-click the
                // text (with the Select tool) to open the full formatting popup.
                _doc.StartInlineFreeText(_page.Index,
                    moved ? rect : new RectD(_start.X, _start.Y, 180, 14));
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
        var toUpdate = new List<(Annotation Model, List<List<PointD>> Strokes, RectD Bounds)>();

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

            var allPts = newStrokes.SelectMany(s => s).ToList();
            double minX = allPts.Min(p => p.X), minY = allPts.Min(p => p.Y);
            double maxX = allPts.Max(p => p.X), maxY = allPts.Max(p => p.Y);
            toUpdate.Add((model, newStrokes, new RectD(minX, minY, maxX - minX, maxY - minY).Inflate(2)));
        }

        // Dragging the eraser over empty space must NOT mark the document dirty.
        if (toRemove.Count == 0 && toUpdate.Count == 0) return;

        // Snapshot BEFORE the first mutation of this drag (undo pushes are idempotent
        // for the rest of the gesture).
        EnsureGestureUndo();

        foreach (var (model, strokes, bounds) in toUpdate)
        {
            model.Strokes = strokes;
            model.Bounds = bounds;
            model.Modified = DateTime.Now;
        }
        foreach (var vm in toRemove)
        {
            if (_doc.SelectedAnnotation == vm) _doc.SelectedAnnotation = null;
            _doc.Annotations.Remove(vm);
        }
        _doc.NotifyAnnotationChanged();
    }

    /// <summary>All pointer samples carried by this move event, oldest first. A pen
    /// reports at a much higher rate than WPF dispatches mouse moves; the extra samples
    /// travel coalesced inside the event and are recoverable through the stylus device.
    /// Using only GetPosition throws away most of the pen's actual path, which forced
    /// the curve fit to guess around corners — the main reason handwriting felt wrong.
    /// Falls back to the single event position for mouse input.</summary>
    private IEnumerable<PointD> InkSamples(MouseEventArgs e)
    {
        if (e.StylusDevice is { } stylus)
        {
            var pts = stylus.GetStylusPoints(this);
            if (pts.Count > 0)
            {
                foreach (var sp in pts)
                {
                    var page = ToPage(new Point(sp.X, sp.Y));
                    yield return new PointD(page.X, page.Y);
                }
                yield break;
            }
        }
        yield return new PointD(_current.X, _current.Y);
    }

    /// <summary>Appends a raw pointer sample to an in-progress ink/freehand-highlight stroke,
    /// gating out oversampled near-duplicate points and applying only a whisper of low-pass
    /// filtering. The old values (1.2 pt spacing, 0.55 blend) made the stroke trail the pen
    /// tip by several samples and then "catch up" at stroke end — that lag-then-snap is what
    /// felt like overcorrection when writing letters. <see cref="StrokeSmoothing"/> handles
    /// the visible smoothing, so this filter only needs to take the edge off sensor jitter.</summary>
    private static void AppendSmoothed(List<PointD> pts, PointD raw)
    {
        const double minSpacing = 0.35;  // page points

        if (pts.Count == 0) { pts.Add(raw); return; }

        var last = pts[^1];
        double dx = raw.X - last.X, dy = raw.Y - last.Y;
        double dist2 = dx * dx + dy * dy;
        if (dist2 < minSpacing * minSpacing) return;

        // Speed-adaptive low-pass: tiny, slow movements (where sensor jitter and hand
        // tremor dominate) are smoothed firmly, while quick strokes pass almost raw so the
        // line never trails the pen tip. 0 = raw input, 1 = frozen.
        double dist = Math.Sqrt(dist2);
        double smoothing = 0.42 / (1.0 + dist / 1.4);

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

    // -------------------------------------------------------------- lasso

    private enum LassoHit { None, Body, Delete }

    private void BeginLasso(MouseButtonEventArgs e)
    {
        var posPx = e.GetPosition(this);
        switch (HitLasso(posPx))
        {
            case LassoHit.Delete:
                _doc!.DeleteLassoSelection();
                e.Handled = true;
                return;

            case LassoHit.Body:
                // Grab the whole selection: it follows the pointer, keeping the grab offset.
                var b = _doc!.LassoBounds()!.Value;
                _moveGrab = new Point(_start.X - b.X, _start.Y - b.Y);
                _gestureUndoPushed = false;
                _lassoMoving = true;
                CaptureMouse();
                e.Handled = true;
                return;
        }

        // Anywhere else starts a fresh loop and drops the old selection.
        _doc!.ClearLasso();
        _lassoPoints.Clear();
        _lassoPoints.Add(new PointD(_start.X, _start.Y));
        _lassoDrawing = true;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnLassoMove(MouseEventArgs e)
    {
        var posPx = e.GetPosition(this);
        var p = ToPage(posPx);

        if (_lassoDrawing)
        {
            var last = _lassoPoints[^1];
            double dx = p.X - last.X, dy = p.Y - last.Y;
            if (dx * dx + dy * dy >= 0.7 * 0.7)
            {
                _lassoPoints.Add(new PointD(p.X, p.Y));
                UpdateLiveInk();
            }
            return;
        }

        if (_lassoMoving)
        {
            if (_doc!.LassoBounds() is not { } b) return;
            double dx = p.X - _moveGrab.X - b.X;
            double dy = p.Y - _moveGrab.Y - b.Y;

            // Keep the selection on the page.
            double minDx = -b.Left, maxDx = _page!.WidthPt - b.Right;
            double minDy = -b.Top, maxDy = _page.HeightPt - b.Bottom;
            if (minDx <= maxDx) dx = Math.Clamp(dx, minDx, maxDx);
            if (minDy <= maxDy) dy = Math.Clamp(dy, minDy, maxDy);

            if (Math.Abs(dx) < 1e-6 && Math.Abs(dy) < 1e-6) return;
            EnsureGestureUndo();
            MoveLasso(dx, dy);
            _doc.NotifyAnnotationChanged();
            return;
        }

        Cursor = HitLasso(posPx) switch
        {
            LassoHit.Delete => Cursors.Hand,
            LassoHit.Body => Cursors.SizeAll,
            _ => Cursors.Cross,
        };
    }

    private void MoveLasso(double dx, double dy)
    {
        foreach (var vm in _doc!.LassoAnnotations)
        {
            var m = vm.Model;
            m.Bounds = new RectD(m.Bounds.X + dx, m.Bounds.Y + dy, m.Bounds.Width, m.Bounds.Height);
            if (m.Quads.Count > 0)
                m.Quads = m.Quads.Select(q => new RectD(q.X + dx, q.Y + dy, q.Width, q.Height)).ToList();
            if (m.Strokes.Count > 0)
                m.Strokes = m.Strokes
                    .Select(st => st.Select(pt => new PointD(pt.X + dx, pt.Y + dy)).ToList())
                    .ToList();
            if (m.Kind == AnnotationKind.Line)
            {
                m.LineStart = new PointD(m.LineStart.X + dx, m.LineStart.Y + dy);
                m.LineEnd = new PointD(m.LineEnd.X + dx, m.LineEnd.Y + dy);
            }
            m.Modified = DateTime.Now;
        }
        foreach (var im in _doc.LassoImages)
            im.Rect = new RectD(im.Rect.X + dx, im.Rect.Y + dy, im.Rect.Width, im.Rect.Height);
    }

    private LassoHit HitLasso(Point posPx)
    {
        if (_doc is not { HasLassoSelection: true } || _page == null || _doc.LassoPage != _page.Index ||
            _doc.LassoBounds() is not { } b)
            return LassoHit.None;
        var r = ToRect(b.Inflate(4), Scale);
        if ((posPx - ChipCentre(r)).Length <= ChipRadius + 2) return LassoHit.Delete;
        return r.Contains(posPx) ? LassoHit.Body : LassoHit.None;
    }

    private Point ChipCentre(Rect r) => new(
        Math.Clamp(r.Right, ChipRadius, Math.Max(ChipRadius, ActualWidth - ChipRadius)),
        Math.Clamp(r.Top, ChipRadius, Math.Max(ChipRadius, ActualHeight - ChipRadius)));

    /// <summary>The loop is closed: everything whose outline lies mostly (≥ 50 % of its sample
    /// points) inside it becomes the selection. A stray click or tiny scribble selects nothing.</summary>
    private void FinishLasso()
    {
        _lassoDrawing = false;
        ClearLiveInk();
        var poly = _lassoPoints.ToList();
        _lassoPoints.Clear();
        if (_doc == null || _page == null) return;

        if (poly.Count < 3)
        {
            InvalidateVisual();
            return;
        }
        double minX = poly.Min(p => p.X), maxX = poly.Max(p => p.X);
        double minY = poly.Min(p => p.Y), maxY = poly.Max(p => p.Y);
        if (maxX - minX < 4 && maxY - minY < 4)
        {
            InvalidateVisual();
            return;
        }

        bool Captured(List<PointD> samples)
        {
            if (samples.Count == 0) return false;
            int inside = 0;
            foreach (var pt in samples)
                if (pt.X >= minX && pt.X <= maxX && pt.Y >= minY && pt.Y <= maxY && InPolygon(poly, pt.X, pt.Y))
                    inside++;
            return inside * 2 >= samples.Count;
        }

        var annotations = _doc.Annotations
            .Where(a => a.PageIndex == _page.Index && Captured(LassoSamples(a.Model)))
            .ToList();
        var images = _doc.PlacedImages
            .Where(i => i.PageIndex == _page.Index && Captured(RectSamples(i.Rect)))
            .ToList();
        _doc.SetLassoSelection(_page.Index, annotations, images);
        InvalidateVisual();
    }

    /// <summary>Points that stand for an annotation when testing it against the lasso loop.</summary>
    private static List<PointD> LassoSamples(Annotation a)
    {
        var pts = new List<PointD>();
        switch (a.Kind)
        {
            case AnnotationKind.Ink:
            case AnnotationKind.Highlight when a.IsFreehand:
                foreach (var stroke in a.Strokes)
                {
                    int step = Math.Max(1, stroke.Count / 60);
                    for (int i = 0; i < stroke.Count; i += step) pts.Add(stroke[i]);
                }
                if (pts.Count > 0) return pts;
                break;

            case AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut
                when a.Quads.Count > 0:
                foreach (var q in a.Quads)
                    pts.Add(new PointD(q.X + q.Width / 2, q.Y + q.Height / 2));
                return pts;

            case AnnotationKind.Line:
                pts.Add(a.LineStart);
                pts.Add(a.LineEnd);
                pts.Add(new PointD((a.LineStart.X + a.LineEnd.X) / 2, (a.LineStart.Y + a.LineEnd.Y) / 2));
                return pts;
        }
        return RectSamples(a.Bounds);
    }

    private static List<PointD> RectSamples(RectD r) => new()
    {
        new PointD(r.Left, r.Top), new PointD(r.Right, r.Top),
        new PointD(r.Right, r.Bottom), new PointD(r.Left, r.Bottom),
        new PointD(r.X + r.Width / 2, r.Y + r.Height / 2),
    };

    /// <summary>Even-odd point-in-polygon (the loop may cross itself).</summary>
    private static bool InPolygon(List<PointD> poly, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            PointD a = poly[i], b = poly[j];
            if ((a.Y > y) != (b.Y > y) && x < (b.X - a.X) * (y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private void DrawLassoLoop(DrawingContext dc, double s)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(_lassoPoints[0].X * s, _lassoPoints[0].Y * s), true, true);
            var rest = new List<Point>(_lassoPoints.Count - 1);
            for (int i = 1; i < _lassoPoints.Count; i++)
                rest.Add(new Point(_lassoPoints[i].X * s, _lassoPoints[i].Y * s));
            ctx.PolyLineTo(rest, true, true);
        }
        dc.DrawGeometry(LassoLoopFill, LassoLoopPen, g);
    }

    /// <summary>Dashed box around the selection plus a small × chip to delete it.</summary>
    private void DrawLassoSelection(DrawingContext dc, double s)
    {
        if (_doc is not { HasLassoSelection: true } || _page == null || _doc.LassoPage != _page.Index) return;
        // Markup removed by other means (sidebar, undo) must not linger in the selection.
        _doc.LassoAnnotations.RemoveAll(a => !_doc.Annotations.Contains(a));
        _doc.LassoImages.RemoveAll(i => !_doc.PlacedImages.Contains(i));
        if (_doc.LassoBounds() is not { } b) return;

        var r = ToRect(b.Inflate(4), s);
        dc.DrawRoundedRectangle(LassoSelectionFill, ImageDashPen, r, 3, 3);

        var c = ChipCentre(r);
        dc.DrawEllipse(ChipFill, null, c, ChipRadius, ChipRadius);
        const double k = 3.4;
        dc.DrawLine(ChipXPen, new Point(c.X - k, c.Y - k), new Point(c.X + k, c.Y + k));
        dc.DrawLine(ChipXPen, new Point(c.X - k, c.Y + k), new Point(c.X + k, c.Y - k));
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
            var brush = Brush(((Color)FindResource("App.SearchHighlightColor")).ToString(), 0.35);
            foreach (var r in search)
                dc.DrawRoundedRectangle(brush, null, ToRect(r.Inflate(1), s), 2, 2);
        }

        // text selection
        if (_page.SelectionRects is { Count: > 0 } sel)
        {
            var brush = Brush(((Color)FindResource("App.SelectionColor")).ToString(), 0.30);
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
        DrawLassoSelection(dc, s);
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
            case ToolKind.Highlight or ToolKind.Underline or ToolKind.StrikeOut or ToolKind.Select:
                foreach (var r in _liveWordRects) dc.DrawRectangle(LiveWordBrush, null, ToRect(r, s));
                dc.DrawRectangle(null, LiveWordPen, rect);
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

    private static readonly Pen LassoLoopPen = FrozenPen(
        new Pen(Frozen(Color.FromArgb(235, 90, 122, 153)), 1.3)
        { DashStyle = DashStyles.Dash, LineJoin = PenLineJoin.Round });

    private static readonly SolidColorBrush LassoLoopFill = Frozen(Color.FromArgb(38, 90, 122, 153));
    private static readonly SolidColorBrush LassoSelectionFill = Frozen(Color.FromArgb(20, 90, 122, 153));
    private static readonly SolidColorBrush ChipFill = Frozen(Color.FromArgb(235, 52, 56, 64));
    private static readonly Pen ChipXPen = FrozenPen(
        new Pen(Brushes.White, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });

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
        // Every repaint of a page (selection change, eraser hover, dragging a shape) used to
        // re-smooth and rebuild the geometry of every ink stroke on it — on a page of
        // handwriting that's thousands of Bezier segments per mouse-move. Stroke point lists
        // are never edited in place (erasing and undo create new lists), so the geometry can
        // be cached per list and reused until the list grows (live ink) or the zoom changes.
        if (GeometryCache.TryGetValue(pts, out var hit) && hit.Scale == s && hit.Count == pts.Count &&
            (pts.Count == 0 || (hit.First == pts[0] && hit.Last == pts[^1])))
            return hit.Geometry;

        var scaled = new Point[pts.Count];
        for (int i = 0; i < pts.Count; i++)
            scaled[i] = new Point(pts[i].X * s, pts[i].Y * s);
        var geometry = StrokeSmoothing.ToSmoothGeometry(scaled);
        GeometryCache.AddOrUpdate(pts, new CachedGeometry(s, pts.Count,
            pts.Count > 0 ? pts[0] : default, pts.Count > 0 ? pts[^1] : default, geometry));
        return geometry;
    }

    private sealed record CachedGeometry(double Scale, int Count, PointD First, PointD Last, StreamGeometry Geometry);

    // Weakly keyed: entries vanish together with the stroke lists they belong to.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyList<PointD>, CachedGeometry>
        GeometryCache = new();

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
