using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Graphite.Core;
using Graphite.Core.Annotations;
using Graphite.Core.Editing;
using Graphite.Core.Ocr;
using Graphite.Core.Rendering;
using Graphite.Core.Text;

namespace Graphite.App.ViewModels;

public enum ToolKind
{
    Select, Highlight, Underline, StrikeOut, Ink, Eraser, Rect, Ellipse, Note, EditText, PlaceImage,
    Text, Arrow, Signature, Lasso
}

public enum PageLayout
{
    Continuous, Single, Spread
}

/// <summary>Filter chips above the Markup panel's cards.</summary>
public enum MarkupFilter
{
    All, Highlights, Notes, Ink
}

public enum PageOpKind { None, Rotate, Delete, Insert, Move }

/// <summary>What a structural page operation did, so the view can animate it (the page
/// list itself is rebuilt from scratch afterwards). Index = first affected page; Extra =
/// rotation delta (±90) or the move's destination index; Count is filled in by the document.</summary>
public readonly record struct PageOpHint(PageOpKind Kind, int Index, int Count = 1, int Extra = 0);

/// <summary>An image placed on a page. It stays a movable/resizable overlay object —
/// selectable and draggable via the Select tool — until the document is saved or a
/// structural page operation runs, at which point it's baked into the PDF content
/// (the same "operations are pure" convention annotations already follow).</summary>
public sealed class PendingImage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required int PageIndex { get; init; }
    public required string Path { get; init; }
    public required ImageSource Bitmap { get; init; }
    public RectD Rect { get; set; }

    public PendingImage Clone() => new()
    {
        Id = Id,
        PageIndex = PageIndex,
        Path = Path,
        Bitmap = Bitmap,
        Rect = Rect,
    };
}

public partial class DocumentViewModel : ObservableObject, IDisposable
{
    private byte[] _bytes;

    public PdfRenderer Renderer { get; }
    public TextIndex Index { get; }

    public ObservableCollection<PageViewModel> Pages { get; } = new();

    /// <summary>What the viewer shows: all pages (continuous) or just the current page (single).</summary>
    public ObservableCollection<PageViewModel> VisiblePages { get; } = new();
    public ObservableCollection<OutlineNode> Outline { get; } = new();
    public ObservableCollection<AnnotationViewModel> Annotations { get; } = new();
    public ObservableCollection<SearchMatch> SearchResults { get; } = new();

    /// <summary>Images placed but not yet baked into the PDF — see <see cref="PendingImage"/>.</summary>
    public ObservableCollection<PendingImage> PlacedImages { get; } = new();

    [ObservableProperty] private string? filePath;
    [ObservableProperty] private double zoom = 1.15;
    [ObservableProperty] private PageLayout layout = PageLayout.Continuous;
    [ObservableProperty] private bool invertPages;
    [ObservableProperty] private int currentPageIndex;
    [ObservableProperty] private ToolKind activeTool = ToolKind.Select;
    [ObservableProperty] private bool isDirty;

    /// <summary>True for the tab that is currently shown. Every open document keeps its own
    /// viewer alive (collapsed while inactive) so switching tabs is a visibility flip rather
    /// than a rebuild of the whole page list; the window binds visibility to this.</summary>
    [ObservableProperty] private bool isActive;

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string busyText = "";
    [ObservableProperty] private AnnotationViewModel? selectedAnnotation;
    [ObservableProperty] private string searchQuery = "";
    [ObservableProperty] private int currentMatchIndex = -1;
    [ObservableProperty] private string selectedText = "";
    [ObservableProperty] private PendingImage? selectedImage;

    // Per-tool style (color + stroke width + opacity), editable from the tool options popover.
    [ObservableProperty] private string activeColorHex = "#F2C744";
    [ObservableProperty] private double activeStrokeWidth = 1.5;

    /// <summary>0..1 opacity applied to new markup (stored per tool, like colour and width).</summary>
    [ObservableProperty] private double activeOpacity = 1.0;

    /// <summary>Remembered opacity per tool. The freehand marker is keyed apart from the
    /// text highlight because they share <see cref="ToolKind.Highlight"/>.</summary>
    private readonly Dictionary<string, double> _toolOpacities = new()
    {
        ["Marker"] = FreehandHighlightOpacity,
    };

    private string OpacityKey() =>
        ActiveTool == ToolKind.Highlight && HighlightFreehand ? "Marker" : ActiveTool.ToString();

    private void LoadToolOpacity() =>
        ActiveOpacity = _toolOpacities.TryGetValue(OpacityKey(), out var o) ? o : 1.0;

    partial void OnActiveOpacityChanged(double value)
    {
        _toolOpacities[OpacityKey()] = value;
        OnPropertyChanged(nameof(ActiveOpacityPercent));
    }

    /// <summary>Opacity as a whole percent for the slider read-out.</summary>
    public string ActiveOpacityPercent => $"{Math.Round(ActiveOpacity * 100)}%";

    // ---- what the tool options popover shows for the active tool ----

    public string ToolTitle => ActiveTool switch
    {
        ToolKind.Select => "Select",
        ToolKind.Highlight => HighlightFreehand ? "Marker" : "Highlight",
        ToolKind.Underline => "Underline",
        ToolKind.StrikeOut => "Strikethrough",
        ToolKind.Ink => "Pen",
        ToolKind.Eraser => "Eraser",
        ToolKind.Rect => "Rectangle",
        ToolKind.Ellipse => "Ellipse",
        ToolKind.Text => "Text box",
        ToolKind.Arrow => "Arrow",
        ToolKind.Signature => "Signature",
        ToolKind.Lasso => "Lasso",
        ToolKind.EditText => "Edit page text",
        ToolKind.PlaceImage => "Insert image",
        _ => "Markup",
    };

    /// <summary>Single-letter shortcut shown next to the tool name ("" = none).</summary>
    public string ToolKey => ActiveTool switch
    {
        ToolKind.Select => "V",
        ToolKind.Highlight => HighlightFreehand ? "M" : "H",
        ToolKind.Underline => "U",
        ToolKind.StrikeOut => "S",
        ToolKind.Ink => "P",
        ToolKind.Eraser => "E",
        ToolKind.Rect => "R",
        ToolKind.Ellipse => "O",
        ToolKind.Text => "T",
        ToolKind.Arrow => "A",
        ToolKind.Lasso => "L",
        _ => "",
    };

    public bool ShowsColor => ActiveTool is not (ToolKind.Select or ToolKind.Eraser or ToolKind.EditText
        or ToolKind.PlaceImage or ToolKind.Note);
    public bool ShowsThickness => ActiveTool is ToolKind.Underline or ToolKind.StrikeOut or ToolKind.Ink
        or ToolKind.Eraser or ToolKind.Rect or ToolKind.Ellipse or ToolKind.Arrow;
    public bool ShowsOpacity => ActiveTool is ToolKind.Highlight or ToolKind.Underline or ToolKind.StrikeOut
        or ToolKind.Ink or ToolKind.Rect or ToolKind.Ellipse or ToolKind.Arrow;
    public bool ShowsSnap => ActiveTool == ToolKind.Highlight;
    public bool HasToolOptions => ShowsColor || ShowsThickness || ShowsOpacity;

    private void NotifyToolOptions()
    {
        OnPropertyChanged(nameof(ToolTitle));
        OnPropertyChanged(nameof(ToolKey));
        OnPropertyChanged(nameof(ShowsColor));
        OnPropertyChanged(nameof(ShowsThickness));
        OnPropertyChanged(nameof(ShowsOpacity));
        OnPropertyChanged(nameof(ShowsSnap));
        OnPropertyChanged(nameof(HasToolOptions));
    }

    // ---- Markup panel: filter chips + cards grouped by page ----

    [ObservableProperty] private MarkupFilter markupFilter = MarkupFilter.All;

    private ICollectionView? _markupView;

    /// <summary>The annotations as the Markup panel lists them: sorted and grouped by page,
    /// narrowed by <see cref="MarkupFilter"/>.</summary>
    public ICollectionView MarkupView => _markupView ??= BuildMarkupView();

    private ICollectionView BuildMarkupView()
    {
        var view = CollectionViewSource.GetDefaultView(Annotations);
        view.SortDescriptions.Add(new SortDescription(nameof(AnnotationViewModel.PageIndex), ListSortDirection.Ascending));
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AnnotationViewModel.PageLabel)));
        view.Filter = o => o is AnnotationViewModel a && MatchesMarkupFilter(a, MarkupFilter);
        return view;
    }

    private static bool MatchesMarkupFilter(AnnotationViewModel a, MarkupFilter f) =>
        f == MarkupFilter.All || a.Category == f;

    partial void OnMarkupFilterChanged(MarkupFilter value) => _markupView?.Refresh();

    public int MarkupCountAll => Annotations.Count;
    public int MarkupCountHighlights => Annotations.Count(a => a.Category == MarkupFilter.Highlights);
    public int MarkupCountNotes => Annotations.Count(a => a.Category == MarkupFilter.Notes);
    public int MarkupCountInk => Annotations.Count(a => a.Category == MarkupFilter.Ink);

    private void RefreshMarkupCounts()
    {
        OnPropertyChanged(nameof(MarkupCountAll));
        OnPropertyChanged(nameof(MarkupCountHighlights));
        OnPropertyChanged(nameof(MarkupCountNotes));
        OnPropertyChanged(nameof(MarkupCountInk));
    }

    /// <summary>Highlight tool sub-mode: false = drag over text to snap-highlight words,
    /// true = draw a freehand translucent marker stroke anywhere on the page.</summary>
    [ObservableProperty] private bool highlightFreehand;

    /// <summary>Toolbar checked state for the text-snapping Highlight button. Setting it
    /// to true activates the Highlight tool in text mode (false is ignored — the tool
    /// group unchecks buttons that way).</summary>
    public bool IsTextHighlightActive
    {
        get => ActiveTool == ToolKind.Highlight && !HighlightFreehand;
        set { if (value) { HighlightFreehand = false; ActiveTool = ToolKind.Highlight; } }
    }

    /// <summary>Toolbar checked state for the freehand Marker button (lives with the pen
    /// and eraser). Setting it to true activates the Highlight tool in freehand mode.</summary>
    public bool IsFreehandMarkerActive
    {
        get => ActiveTool == ToolKind.Highlight && HighlightFreehand;
        set { if (value) { HighlightFreehand = true; ActiveTool = ToolKind.Highlight; } }
    }

    /// <summary>Stroke width used for freehand highlighter strokes (not user-adjustable
    /// via the line-thickness control, which is for underline/strike/drawing/shapes).</summary>
    public const double FreehandHighlightWidth = 14.0;
    public const double FreehandHighlightOpacity = 0.42;

    private readonly Dictionary<ToolKind, string> _toolColors = new()
    {
        [ToolKind.Highlight] = "#F2C744",
        [ToolKind.Underline] = "#3E6DB5",
        [ToolKind.StrikeOut] = "#C24444",
        [ToolKind.Ink] = "#3E6DB5",
        [ToolKind.Rect] = "#C24444",
        [ToolKind.Ellipse] = "#C24444",
        [ToolKind.Text] = "#1B1B1A",
        [ToolKind.Arrow] = "#C24444",
        [ToolKind.Signature] = "#1B3A6B",
    };

    private readonly Dictionary<ToolKind, double> _toolWidths = new()
    {
        [ToolKind.Underline] = 1.5,
        [ToolKind.StrikeOut] = 1.5,
        [ToolKind.Ink] = 1.8,
        // Reused as the eraser's radius (x3, floor 6pt — see AnnotationLayer.EraserRadius),
        // so the same "line thickness" menu also sizes the eraser.
        [ToolKind.Eraser] = 4.0,
        [ToolKind.Rect] = 1.6,
        [ToolKind.Ellipse] = 1.6,
        [ToolKind.Arrow] = 2.0,
        [ToolKind.Signature] = 1.6,
    };

    // ------------------------------------------------------------- undo / redo

    private readonly List<(byte[] Bytes, List<Annotation> Annots, List<PendingImage> Images)> _undo = new();
    private readonly List<(byte[] Bytes, List<Annotation> Annots, List<PendingImage> Images)> _redo = new();
    private const int MaxUndo = 30;

    /// <summary>Hard cap on how much memory undo + redo snapshots may hold together, on
    /// top of the step-count cap above. Each snapshot holds a full copy of the document's
    /// bytes, so for large PDFs 30 steps alone could mean many hundreds of MB resident;
    /// this trims the oldest steps first so history depth degrades gracefully with file size.</summary>
    private const long MaxUndoBudgetBytes = 256L * 1024 * 1024;

    private List<PendingImage> CloneImages() => PlacedImages.Select(i => i.Clone()).ToList();

    private void TrimStack(List<(byte[] Bytes, List<Annotation> Annots, List<PendingImage> Images)> stack)
    {
        while (stack.Count > MaxUndo) stack.RemoveAt(0);
        // Annotation-only edits don't touch the PDF bytes, so most snapshots share the very
        // same byte[] as their neighbours (and as the live document). Budget by the distinct
        // buffers actually held — counting every snapshot's Bytes.Length charged a 50 MB PDF
        // 50 MB per pen stroke and threw away undo history long before any memory was at stake.
        while (stack.Count > 1 && DistinctUndoBytes() > MaxUndoBudgetBytes)
            stack.RemoveAt(0);
    }

    private long DistinctUndoBytes()
    {
        var seen = new HashSet<byte[]>(ReferenceEqualityComparer.Instance) { _bytes };
        long total = 0;
        foreach (var snap in _undo.Concat(_redo))
            if (seen.Add(snap.Bytes)) total += snap.Bytes.Length;
        return total;
    }

    /// <summary>Capture the current state; call BEFORE any mutation.</summary>
    public void PushUndo()
    {
        _undo.Add((_bytes, Annotations.Select(a => a.Model.Clone()).ToList(), CloneImages()));
        TrimStack(_undo);
        _redo.Clear();
    }

    // Coalescing: continuous edits (typing in a comment box) should produce ONE undo
    // step per burst, not one per keystroke.
    private (string Key, DateTime When)? _lastCoalescedPush;

    /// <summary>Push an undo snapshot, but skip if the same <paramref name="key"/> was
    /// pushed within <paramref name="windowSeconds"/>. The first edit of a burst always
    /// captures the pre-edit state; later edits in the burst ride on that snapshot.</summary>
    public void PushUndoCoalesced(string key, double windowSeconds = 1.5)
    {
        if (_lastCoalescedPush is { } last && last.Key == key &&
            (DateTime.Now - last.When).TotalSeconds < windowSeconds)
        {
            _lastCoalescedPush = (key, DateTime.Now);
            return;
        }
        PushUndo();
        _lastCoalescedPush = (key, DateTime.Now);
    }

    public async Task UndoAsync()
    {
        if (_undo.Count == 0) return;
        _redo.Add((_bytes, Annotations.Select(a => a.Model.Clone()).ToList(), CloneImages()));
        TrimStack(_redo);
        var snap = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        await RestoreSnapshotAsync(snap.Bytes, snap.Annots, snap.Images, "Undoing…");
    }

    public async Task RedoAsync()
    {
        if (_redo.Count == 0) return;
        _undo.Add((_bytes, Annotations.Select(a => a.Model.Clone()).ToList(), CloneImages()));
        TrimStack(_undo);
        var snap = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        await RestoreSnapshotAsync(snap.Bytes, snap.Annots, snap.Images, "Redoing…");
    }

    private async Task RestoreSnapshotAsync(byte[] bytes, List<Annotation> annots, List<PendingImage> images, string busy)
    {
        IsBusy = true;
        BusyText = busy;
        try
        {
            if (!ReferenceEquals(bytes, _bytes))
            {
                var outline = await Task.Run(() =>
                {
                    Renderer.Reload(bytes);
                    Index.Reload(bytes);
                    return Index.GetOutline();
                });
                _bytes = bytes;
                Outline.Clear();
                foreach (var n in outline) Outline.Add(n);
                RebuildPages();
                SearchResults.Clear();
                CurrentMatchIndex = -1;
                CurrentPageIndex = Math.Clamp(CurrentPageIndex, 0, Pages.Count - 1);
                RefreshVisiblePages();
                OnPropertyChanged(nameof(PageStatus));
                PagesReset?.Invoke();
            }
            ClearLasso();
            Annotations.Clear();
            foreach (var a in annots) Annotations.Add(new AnnotationViewModel(this, a.Clone()));
            SelectedAnnotation = null;
            PlacedImages.Clear();
            foreach (var img in images) PlacedImages.Add(img.Clone());
            SelectedImage = null;
            IsDirty = true;
            AnnotationsVisualChanged?.Invoke();
            StateRestored?.Invoke();
        }
        finally { IsBusy = false; }
    }

    public string Title => Path.GetFileName(FilePath) ?? "Untitled";
    public string PageStatus => Pages.Count == 0 ? "" : $"{CurrentPageIndex + 1} / {Pages.Count}";
    public string MatchStatus => SearchResults.Count == 0 ? "No results"
        : $"{CurrentMatchIndex + 1} of {SearchResults.Count}";

    /// <summary>Font size for quick inline text (Text tool click → type immediately).</summary>
    public const double QuickTextSize = 8;

    public event Action<int>? ScrollToPageRequested;
    public event Action? PagesReset;
    public event Action? AnnotationsVisualChanged;
    public event Action? ZoomChangedEvent;
    public event Action<int, RectD>? EditTextRequested;
    public event Action<int, RectD>? PlaceImageRequested;
    public event Action<AnnotationViewModel>? InlineEditRequested;
    public event Action<AnnotationViewModel>? EditFreeTextRequested;

    // Motion hooks — the window turns these into animations.
    public event Action<PageOpHint>? PageOperationApplied;
    public event Action? StateRestored;                 // undo / redo finished
    public event Action? Saved;                         // a save completed
    public event Action<int>? PageFlashRequested;       // "you just landed here" highlight
    public event Action<int>? OcrPageStarted;
    public event Action<int>? OcrPageRecognized;

    /// <summary>When the current search match last changed (lets a page that is realized a
    /// moment later still play the match pulse).</summary>
    public DateTime LastMatchJumpUtc { get; private set; }

    public bool IsContinuous => Layout == PageLayout.Continuous;

    private DocumentViewModel(byte[] bytes, string? path, PdfRenderer renderer, TextIndex index,
        List<Annotation> annotations, IReadOnlyList<OutlineNode> outline)
    {
        _bytes = bytes;
        filePath = path;
        Renderer = renderer;
        Index = index;
        for (int i = 0; i < renderer.PageCount; i++)
            Pages.Add(new PageViewModel(this, i));
        foreach (var node in outline) Outline.Add(node);
        foreach (var a in annotations) Annotations.Add(new AnnotationViewModel(this, a));
        Annotations.CollectionChanged += (_, _) => RefreshMarkupCounts();
        RefreshVisiblePages();
    }

    /// <summary>Replace every page view-model after the PDF changed structurally. The old
    /// ones are evicted first so renders they still have queued are skipped, not run.</summary>
    private void RebuildPages()
    {
        foreach (var p in Pages) p.EvictFullImage();
        Pages.Clear();
        for (int i = 0; i < Renderer.PageCount; i++)
            Pages.Add(new PageViewModel(this, i));
        _viewFirst = _viewLast = -1;
    }

    private void RefreshVisiblePages()
    {
        VisiblePages.Clear();
        if (Pages.Count == 0) return;
        int cur = Math.Clamp(CurrentPageIndex, 0, Pages.Count - 1);
        switch (Layout)
        {
            case PageLayout.Continuous:
                foreach (var p in Pages) VisiblePages.Add(p);
                break;
            case PageLayout.Single:
                VisiblePages.Add(Pages[cur]);
                break;
            case PageLayout.Spread:
                int first = cur - cur % 2;
                VisiblePages.Add(Pages[first]);
                if (first + 1 < Pages.Count) VisiblePages.Add(Pages[first + 1]);
                break;
        }
    }

    public static async Task<DocumentViewModel> LoadAsync(string path)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path);
        return await FromBytesAsync(bytes, path);
    }

    public static async Task<DocumentViewModel> FromBytesAsync(byte[] bytes, string? path)
    {
        // PDFium (rendering), PdfPig (text) and PdfSharp (annotations) each parse the file on
        // their own and share nothing, so they read it side by side instead of one after the
        // other: opening takes as long as the slowest of them, not the sum of all three.
        var rendererTask = Task.Run(() => new PdfRenderer(bytes));
        var indexTask = Task.Run(() => new TextIndex(bytes));
        var annotationsTask = Task.Run(() => AnnotationCodec.Read(bytes));
        try
        {
            await Task.WhenAll(rendererTask, indexTask, annotationsTask).ConfigureAwait(false);
        }
        catch
        {
            // WhenAll only throws once every reader has finished, so this is safe to inspect.
            if (indexTask.IsCompletedSuccessfully) indexTask.Result.Dispose();
            throw;
        }

        var index = indexTask.Result;
        var outline = index.GetOutline();
        return new DocumentViewModel(bytes, path, rendererTask.Result, index, annotationsTask.Result, outline);
    }

    // ------------------------------------------------------------- render priority

    private int _pageRendersInFlight;

    /// <summary>True while pages that are on screen are still waiting for / running through
    /// PDFium. Sidebar thumbnails hold back meanwhile so the pages you are reading come first.</summary>
    public bool IsRenderingPages => Volatile.Read(ref _pageRendersInFlight) > 0;

    internal void PageRenderStarted() => Interlocked.Increment(ref _pageRendersInFlight);
    internal void PageRenderFinished() => Interlocked.Decrement(ref _pageRendersInFlight);

    // ------------------------------------------------------------- zoom / nav

    private double _appliedZoom = 1.15;

    /// <summary>Raised after every zoom change with (old, new) so the view can keep the
    /// content under the cursor / pinch centre / viewport centre where it was.</summary>
    public event Action<double, double>? ZoomApplied;

    partial void OnZoomChanged(double value)
    {
        double old = _appliedZoom;
        _appliedZoom = value;
        foreach (var p in Pages) p.OnZoomChanged();
        ZoomApplied?.Invoke(old, value);
        ZoomChangedEvent?.Invoke();
    }

    partial void OnCurrentPageIndexChanged(int value)
    {
        OnPropertyChanged(nameof(PageStatus));
        if (!IsContinuous && !VisiblePages.Any(p => p.Index == value)) RefreshVisiblePages();
        EvictFarPages(value);
    }

    /// <summary>Pages kept rendered beyond the visible ones (each side). Pages outside the
    /// window have their bitmap dropped and re-render on demand once they come back into
    /// view. Without this, a long document keeps every page it has ever displayed as a
    /// full-resolution bitmap for as long as it's open.</summary>
    private const int RenderBufferPages = 2;

    // Range of pages currently on screen in continuous layout (-1 = not yet known).
    private int _viewFirst = -1, _viewLast = -1;

    /// <summary>Called by the viewer as it scrolls: renders whatever is on screen and drops
    /// bitmaps for pages that are now far away. Driving eviction from the real viewport
    /// (rather than a fixed window around the "current" page) fixes zoomed-out views,
    /// where more pages are visible than the old ±4 window kept — those pages were evicted
    /// while still on screen and stayed blank.</summary>
    public void UpdateViewport(int first, int last)
    {
        if (Pages.Count == 0) return;
        first = Math.Clamp(first, 0, Pages.Count - 1);
        last = Math.Clamp(last, first, Pages.Count - 1);
        if (first == _viewFirst && last == _viewLast) return;
        _viewFirst = first;
        _viewLast = last;
        // Only fill in pages that have no bitmap at all; re-rendering at a new zoom level is
        // left to the window's debounce timer, so a Ctrl+wheel zoom (which shifts the visible
        // range on every notch) doesn't re-render the screen once per notch.
        for (int i = first; i <= last; i++)
            if (Pages[i].Image == null) _ = Pages[i].EnsureRenderedAsync();
        EvictFarPages(CurrentPageIndex);
    }

    /// <summary>Whether a page is inside the range the viewer currently shows (or the range
    /// isn't known yet). Pages that are only realized as scroll cache use this to let the
    /// on-screen pages take the PDFium lock first.</summary>
    public bool IsPageOnScreen(int index) =>
        !IsContinuous || _viewFirst < 0 || (index >= _viewFirst && index <= _viewLast);

    /// <summary>Pages that are actually shown right now: the on-screen range in continuous
    /// layout, the displayed page(s) otherwise.</summary>
    private bool IsShownPage(int index)
    {
        if (IsContinuous && _viewFirst >= 0) return index >= _viewFirst && index <= _viewLast;
        if (IsContinuous) return index >= CurrentPageIndex && index <= CurrentPageIndex + 1;
        return VisiblePages.Any(p => p.Index == index);
    }

    /// <summary>The tab went to the background: drop every full-size page bitmap (thumbnails
    /// stay). The shown range is remembered so <see cref="RenderShownPages"/> can bring the
    /// visible pages straight back when the tab is shown again.</summary>
    public void ReleaseBitmaps()
    {
        foreach (var p in Pages) p.EvictFullImage();
    }

    /// <summary>The tab was just shown again: re-render any on-screen page whose bitmap was
    /// trimmed while it was in the background.</summary>
    public void RenderShownPages()
    {
        foreach (var p in Pages)
            if (p.Image == null && IsShownPage(p.Index)) _ = p.EnsureRenderedAsync();
    }

    private (int Lo, int Hi) KeepRange(int centerIndex)
    {
        if (IsContinuous && _viewFirst >= 0)
        {
            // Keep as many pages again as are visible on each side (the panel's own cache
            // realizes about one viewport up and down), but at least RenderBufferPages.
            int margin = Math.Max(RenderBufferPages, _viewLast - _viewFirst + 1);
            return (_viewFirst - margin, _viewLast + margin);
        }
        return (centerIndex - RenderBufferPages, centerIndex + RenderBufferPages + 1);
    }

    private void EvictFarPages(int centerIndex)
    {
        var (lo, hi) = KeepRange(centerIndex);
        foreach (var p in Pages)
            if (p.Index < lo || p.Index > hi) p.EvictFullImage();
    }

    partial void OnLayoutChanged(PageLayout value)
    {
        OnPropertyChanged(nameof(IsContinuous));
        RefreshVisiblePages();
    }

    partial void OnInvertPagesChanged(bool value)
    {
        Renderer.Invert = value;
        RerenderAllPages();
    }

    /// <summary>Invalidate every page's bitmaps, but only force-render the pages near the
    /// current one; the rest are evicted and re-render on demand as they scroll into view
    /// (same recovery path as <see cref="EvictFarPages"/>). Force-rendering the whole
    /// document made toggling invert on a long PDF queue hundreds of full-size renders.
    /// Thumbnails are cheap (140 px) and stay eagerly refreshed so the sidebar never
    /// shows stale panes.</summary>
    private void RerenderAllPages()
    {
        var (lo, hi) = KeepRange(CurrentPageIndex);
        foreach (var p in Pages)
        {
            p.InvalidateBitmaps();
            if (p.Index >= lo && p.Index <= hi)
                _ = p.EnsureRenderedAsync(force: true);
            else
                p.EvictFullImage();
        }
    }

    partial void OnFilePathChanged(string? value) => OnPropertyChanged(nameof(Title));

    partial void OnActiveToolChanged(ToolKind value)
    {
        if (value != ToolKind.Lasso) ClearLasso();
        if (_toolColors.TryGetValue(value, out var color)) ActiveColorHex = color;
        if (_toolWidths.TryGetValue(value, out var width)) ActiveStrokeWidth = width;
        LoadToolOpacity();
        OnPropertyChanged(nameof(IsTextHighlightActive));
        OnPropertyChanged(nameof(IsFreehandMarkerActive));
        NotifyToolOptions();
    }

    partial void OnHighlightFreehandChanged(bool value)
    {
        LoadToolOpacity();
        OnPropertyChanged(nameof(IsTextHighlightActive));
        OnPropertyChanged(nameof(IsFreehandMarkerActive));
        NotifyToolOptions();
    }

    partial void OnActiveColorHexChanged(string value) => _toolColors[ActiveTool] = value;
    partial void OnActiveStrokeWidthChanged(double value) => _toolWidths[ActiveTool] = value;

    partial void OnSelectedImageChanged(PendingImage? value) => AnnotationsVisualChanged?.Invoke();

    /// <summary>Place a new image; it stays selected and movable/resizable via the Select tool.</summary>
    public void AddImage(PendingImage image)
    {
        PushUndo();
        PlacedImages.Add(image);
        SelectedImage = image;
        IsDirty = true;
        AnnotationsVisualChanged?.Invoke();
    }

    public void RemoveImage(PendingImage image)
    {
        PushUndo();
        PlacedImages.Remove(image);
        if (SelectedImage == image) SelectedImage = null;
        IsDirty = true;
        AnnotationsVisualChanged?.Invoke();
    }

    partial void OnSelectedAnnotationChanged(AnnotationViewModel? value) => AnnotationsVisualChanged?.Invoke();

    partial void OnSelectedAnnotationChanged(AnnotationViewModel? oldValue, AnnotationViewModel? newValue)
    {
        oldValue?.RefreshSelection();
        newValue?.RefreshSelection();
    }

    // Reading history (Alt+Left / Alt+Right).
    private readonly List<int> _histBack = new();
    private readonly List<int> _histForward = new();

    public void GoToPage(int pageIndex, bool recordHistory = true, bool flash = false)
    {
        pageIndex = Math.Clamp(pageIndex, 0, Pages.Count - 1);
        if (recordHistory && pageIndex != CurrentPageIndex)
        {
            _histBack.Add(CurrentPageIndex);
            if (_histBack.Count > 100) _histBack.RemoveAt(0);
            _histForward.Clear();
        }
        CurrentPageIndex = pageIndex;
        ScrollToPageRequested?.Invoke(pageIndex);
        if (flash) PageFlashRequested?.Invoke(pageIndex);
    }

    /// <summary>Previous/next page — steps by two in spread layout.</summary>
    public void StepPage(int direction) =>
        GoToPage(CurrentPageIndex + direction * (Layout == PageLayout.Spread ? 2 : 1));

    public void NavigateBack()
    {
        if (_histBack.Count == 0) return;
        _histForward.Add(CurrentPageIndex);
        int target = _histBack[^1];
        _histBack.RemoveAt(_histBack.Count - 1);
        GoToPage(target, recordHistory: false, flash: true);
    }

    public void NavigateForward()
    {
        if (_histForward.Count == 0) return;
        _histBack.Add(CurrentPageIndex);
        int target = _histForward[^1];
        _histForward.RemoveAt(_histForward.Count - 1);
        GoToPage(target, recordHistory: false, flash: true);
    }

    // ------------------------------------------------------------- operations

    /// <summary>
    /// Run a structural operation (bytes -> bytes). Current annotations are baked
    /// into the PDF first so they travel with their pages, then re-read afterwards.
    /// </summary>
    public async Task ApplyOperationAsync(string busyMessage, Func<byte[], byte[]> operation,
        PageOpHint hint = default)
    {
        IsBusy = true;
        BusyText = busyMessage;
        int pagesBefore = Pages.Count;
        try
        {
            var models = Annotations.Select(a => a.Model.Clone()).ToList();
            var images = CloneImages();
            var (newBytes, newAnnots, outline) = await Task.Run(() =>
            {
                byte[] baked = AnnotationCodec.Write(_bytes, models);
                foreach (var img in images)
                    baked = ContentEditor.PlaceImage(baked, img.PageIndex, img.Rect, img.Path);
                byte[] result = operation(baked);
                Renderer.Reload(result);
                Index.Reload(result);
                return (result, AnnotationCodec.Read(result), Index.GetOutline());
            });

            // Push the undo snapshot only AFTER the operation succeeded — pushing before
            // left a no-op entry on the stack whenever the operation threw.
            PushUndo();

            _bytes = newBytes;

            ClearLasso();
            Annotations.Clear();
            foreach (var a in newAnnots) Annotations.Add(new AnnotationViewModel(this, a));
            SelectedAnnotation = null;
            PlacedImages.Clear();
            SelectedImage = null;

            Outline.Clear();
            foreach (var n in outline) Outline.Add(n);

            RebuildPages();

            SearchResults.Clear();
            CurrentMatchIndex = -1;
            CurrentPageIndex = Math.Clamp(CurrentPageIndex, 0, Pages.Count - 1);
            RefreshVisiblePages();
            IsDirty = true;
            OnPropertyChanged(nameof(PageStatus));
            PagesReset?.Invoke();

            if (hint.Kind != PageOpKind.None)
                PageOperationApplied?.Invoke(hint with { Count = Math.Max(1, Math.Abs(Pages.Count - pagesBefore)) });
        }
        finally { IsBusy = false; }
    }

    /// <summary>Current document bytes with all annotations and placed images baked in
    /// (for save/export). Non-destructive — doesn't touch the live, still-editable state.</summary>
    public byte[] GetBytesWithAnnotations()
    {
        var models = Annotations.Select(a => a.Model).ToList();
        byte[] result = AnnotationCodec.Write(_bytes, models);
        foreach (var img in PlacedImages)
            result = ContentEditor.PlaceImage(result, img.PageIndex, img.Rect, img.Path);
        return result;
    }

    /// <summary>Password of the encrypted file this document was opened from, if any.
    /// Saving re-encrypts with the same password so protection is never silently dropped.</summary>
    public string? SourcePassword { get; set; }

    public async Task SaveAsync(string? path = null)
    {
        path ??= FilePath ?? throw new InvalidOperationException("No file path — use Save As.");
        IsBusy = true;
        BusyText = "Saving…";
        try
        {
            byte[] output = await Task.Run(GetBytesWithAnnotations);
            // The file on disk keeps its password protection; the in-memory working
            // copy stays decrypted so the renderer/index/operations keep working.
            byte[] fileBytes = SourcePassword is { } pw
                ? await Task.Run(() => Graphite.Core.Pdf.PdfSecurity.Encrypt(output, pw))
                : output;

            // Atomic write: replace the original only once the new file is fully on disk,
            // so a crash mid-save can't leave a truncated PDF behind.
            string tmp = path + ".graphite-tmp";
            await File.WriteAllBytesAsync(tmp, fileBytes);
            try { File.Move(tmp, path, overwrite: true); }
            catch
            {
                try { File.Delete(tmp); } catch { /* best effort */ }
                throw;
            }
            _bytes = output;

            // Placed images are now permanent page content — re-render so they stay
            // visible, then drop them from the movable overlay.
            if (PlacedImages.Count > 0)
            {
                await Task.Run(() => Renderer.Reload(output));
                RerenderAllPages();
                PlacedImages.Clear();
                SelectedImage = null;
            }

            FilePath = path;
            IsDirty = false;
            OnPropertyChanged(nameof(Title));
            Saved?.Invoke();
        }
        finally { IsBusy = false; }
    }

    // ------------------------------------------------------------- annotations

    public void AddAnnotation(Annotation model)
    {
        PushUndo();
        var vm = new AnnotationViewModel(this, model);
        // keep the list ordered by page
        int at = 0;
        while (at < Annotations.Count && Annotations[at].PageIndex <= model.PageIndex) at++;
        Annotations.Insert(at, vm);
        SelectedAnnotation = vm;
        IsDirty = true;
        AnnotationsVisualChanged?.Invoke();
    }

    public void RemoveAnnotation(AnnotationViewModel vm)
    {
        PushUndo();
        Annotations.Remove(vm);
        if (SelectedAnnotation == vm) SelectedAnnotation = null;
        IsDirty = true;
        AnnotationsVisualChanged?.Invoke();
    }

    internal void NotifyAnnotationChanged()
    {
        IsDirty = true;
        AnnotationsVisualChanged?.Invoke();
    }

    internal void RequestEditText(int pageIndex, RectD area) => EditTextRequested?.Invoke(pageIndex, area);
    internal void RequestPlaceImage(int pageIndex, RectD area) => PlaceImageRequested?.Invoke(pageIndex, area);
    internal void RequestEditFreeText(AnnotationViewModel vm) => EditFreeTextRequested?.Invoke(vm);

    /// <summary>Text tool: create an empty text box and immediately edit it inline on
    /// the page (no dialog). Double-clicking the text later opens the full dialog.</summary>
    public void StartInlineFreeText(int pageIndex, RectD area)
    {
        var model = new Annotation
        {
            Kind = AnnotationKind.FreeText,
            PageIndex = pageIndex,
            Bounds = area,
            Contents = "",
            ColorHex = ActiveColorHex,
            FontSize = QuickTextSize,
        };
        AddAnnotation(model);
        var vm = Annotations.First(a => ReferenceEquals(a.Model, model));
        InlineEditRequested?.Invoke(vm);
    }

    /// <summary>Remove an annotation without an undo snapshot or dirty flag — used to
    /// back out of an inline text edit that was cancelled or left empty.</summary>
    public void DiscardAnnotation(AnnotationViewModel vm)
    {
        Annotations.Remove(vm);
        if (SelectedAnnotation == vm) SelectedAnnotation = null;
        AnnotationsVisualChanged?.Invoke();
    }

    // ------------------------------------------------------------- lasso selection

    /// <summary>Markup grabbed with the Lasso tool. A lasso lives on a single page; the
    /// page's annotation layer draws the selection and drags it around.</summary>
    public List<AnnotationViewModel> LassoAnnotations { get; } = new();
    public List<PendingImage> LassoImages { get; } = new();
    public int LassoPage { get; private set; } = -1;

    public bool HasLassoSelection => LassoAnnotations.Count + LassoImages.Count > 0;

    public void SetLassoSelection(int pageIndex, IEnumerable<AnnotationViewModel> annotations,
        IEnumerable<PendingImage> images)
    {
        LassoAnnotations.Clear();
        LassoAnnotations.AddRange(annotations);
        LassoImages.Clear();
        LassoImages.AddRange(images);
        LassoPage = HasLassoSelection ? pageIndex : -1;
        AnnotationsVisualChanged?.Invoke();
    }

    public void ClearLasso()
    {
        if (!HasLassoSelection && LassoPage < 0) return;
        LassoAnnotations.Clear();
        LassoImages.Clear();
        LassoPage = -1;
        AnnotationsVisualChanged?.Invoke();
    }

    /// <summary>Union of everything in the lasso selection, in page points.</summary>
    public RectD? LassoBounds()
    {
        RectD? union = null;
        foreach (var a in LassoAnnotations)
            union = union is { } u ? u.Union(a.Model.Bounds) : a.Model.Bounds;
        foreach (var i in LassoImages)
            union = union is { } u ? u.Union(i.Rect) : i.Rect;
        return union;
    }

    public void DeleteLassoSelection()
    {
        if (!HasLassoSelection) return;
        PushUndo();
        foreach (var a in LassoAnnotations)
        {
            Annotations.Remove(a);
            if (SelectedAnnotation == a) SelectedAnnotation = null;
        }
        foreach (var i in LassoImages)
        {
            PlacedImages.Remove(i);
            if (SelectedImage == i) SelectedImage = null;
        }
        LassoAnnotations.Clear();
        LassoImages.Clear();
        LassoPage = -1;
        IsDirty = true;
        AnnotationsVisualChanged?.Invoke();
    }

    /// <summary>Recolour everything in the lasso selection (the toolbar colour menu while the
    /// Lasso tool is active). Returns false when there was nothing to recolour.</summary>
    public bool RecolorLassoSelection(string hex)
    {
        if (LassoAnnotations.Count == 0) return false;
        PushUndo();
        foreach (var a in LassoAnnotations)
        {
            a.Model.ColorHex = hex;
            a.Model.Modified = DateTime.Now;
        }
        NotifyAnnotationChanged();
        return true;
    }

    // ------------------------------------------------------------- search

    private CancellationTokenSource? _searchCts;

    public async Task RunSearchAsync()
    {
        // Cancel any search still running — overlapping searches race their results
        // into SearchResults otherwise.
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        var cts = _searchCts = new CancellationTokenSource();

        string query = SearchQuery;
        SearchResults.Clear();
        CurrentMatchIndex = -1;
        foreach (var p in Pages) p.SearchRects = null;
        OnPropertyChanged(nameof(MatchStatus));
        if (string.IsNullOrWhiteSpace(query)) return;

        IsBusy = true;
        BusyText = "Searching…";
        try
        {
            var results = await Task.Run(() => Index.Search(query, cts.Token), cts.Token);
            foreach (var r in results) SearchResults.Add(r);
            foreach (var g in results.GroupBy(r => r.PageIndex))
                if (g.Key < Pages.Count)
                    Pages[g.Key].SearchRects = g.SelectMany(r => r.Rects).ToList();
            if (SearchResults.Count > 0) GoToMatch(0);
            OnPropertyChanged(nameof(MatchStatus));
        }
        catch (OperationCanceledException) { /* superseded by a newer search */ }
        finally { IsBusy = false; }
    }

    public void GoToMatch(int index)
    {
        if (SearchResults.Count == 0) return;
        LastMatchJumpUtc = DateTime.UtcNow;
        CurrentMatchIndex = ((index % SearchResults.Count) + SearchResults.Count) % SearchResults.Count;
        GoToPage(SearchResults[CurrentMatchIndex].PageIndex);
        OnPropertyChanged(nameof(MatchStatus));
    }

    public void NextMatch() => GoToMatch(CurrentMatchIndex + 1);
    public void PrevMatch() => GoToMatch(CurrentMatchIndex - 1);

    // ------------------------------------------------------------- selection

    public void SetTextSelection(int pageIndex, RectD rect)
    {
        var words = Index.WordsInRect(pageIndex, rect);
        foreach (var p in Pages)
            p.SelectionRects = p.Index == pageIndex && words.Count > 0
                ? words.Select(w => w.Box).ToList()
                : null;
        SelectedText = string.Join(" ", words.Select(w => w.Text));
    }

    public void ClearTextSelection()
    {
        foreach (var p in Pages) p.SelectionRects = null;
        SelectedText = "";
    }

    // ------------------------------------------------------------- OCR

    /// <summary>OCR pages that have no text layer; makes them searchable/selectable and
    /// bakes an invisible text layer into the PDF so it stays searchable after save.</summary>
    public async Task<int> RunOcrAsync()
    {
        if (!OcrEngine.IsAvailable())
            throw new InvalidOperationException(
                "OCR language data not found.\n\nRun tools\\get-tessdata.ps1 (or place eng.traineddata " +
                $"in {OcrEngine.DefaultDataPath}) and try again.");

        IsBusy = true;
        int recognized = 0;
        try
        {
            // Progress<T> marshals to the UI thread (captured SynchronizationContext),
            // so BusyText is never set from a background thread.
            var busy = new Progress<string>(t => BusyText = t);
            var started = new Progress<int>(p => OcrPageStarted?.Invoke(p));
            var recognizedPage = new Progress<int>(p => OcrPageRecognized?.Invoke(p));
            var textLayers = new Dictionary<int, IReadOnlyList<WordBox>>();
            await Task.Run(() =>
            {
                using var ocr = new OcrEngine();
                for (int p = 0; p < Pages.Count; p++)
                {
                    if (Index.HasText(p)) continue;
                    ((IProgress<string>)busy).Report($"OCR — page {p + 1} of {Pages.Count}…");
                    ((IProgress<int>)started).Report(p);

                    byte[] png = Renderer.RenderEncoded(p, 300.0 / 72.0);
                    var result = ocr.RecognizeImage(png);
                    var (w, h) = Renderer.PageSizes[p];
                    var words = OcrEngine.ToPageSpace(result, w, h).ToList();
                    if (words.Count == 0) continue;

                    Index.SetOcrWords(p, words, w, h);
                    textLayers[p] = words;
                    recognized++;
                    ((IProgress<int>)recognizedPage).Report(p);
                }
            });

            if (recognized > 0)
            {
                // Bake all recognized pages' text layers in a single open/save pass —
                // per-page baking re-serialized the whole document once per page (O(n²)).
                BusyText = "Writing text layer…";
                _bytes = await Task.Run(() => SearchablePdfWriter.AddTextLayers(_bytes, textLayers));
                IsDirty = true;
            }
            return recognized;
        }
        finally { IsBusy = false; }
    }

    // ------------------------------------------------------------- lifetime

    private bool _disposed;

    /// <summary>Release everything a closed document holds: cancel a running search, drop
    /// undo/redo snapshots (each can be a full copy of the PDF), page bitmaps and the text
    /// index. Safe to call more than once.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        _undo.Clear();
        _redo.Clear();
        foreach (var p in Pages) p.EvictFullImage();
        PlacedImages.Clear();
        Index.Dispose();
    }
}
