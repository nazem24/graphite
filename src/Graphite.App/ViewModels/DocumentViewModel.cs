using System.Collections.ObjectModel;
using System.IO;
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
    Text, Arrow, Signature
}

public enum PageLayout
{
    Continuous, Single, Spread
}

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

public partial class DocumentViewModel : ObservableObject
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
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string busyText = "";
    [ObservableProperty] private AnnotationViewModel? selectedAnnotation;
    [ObservableProperty] private string searchQuery = "";
    [ObservableProperty] private int currentMatchIndex = -1;
    [ObservableProperty] private string selectedText = "";
    [ObservableProperty] private PendingImage? selectedImage;

    // Per-tool style (color + stroke width), editable from the toolbar.
    [ObservableProperty] private string activeColorHex = "#F2C744";
    [ObservableProperty] private double activeStrokeWidth = 1.5;

    /// <summary>Highlight tool sub-mode: false = drag over text to snap-highlight words,
    /// true = draw a freehand translucent marker stroke anywhere on the page.</summary>
    [ObservableProperty] private bool highlightFreehand;

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

    private static void TrimStack(List<(byte[] Bytes, List<Annotation> Annots, List<PendingImage> Images)> stack)
    {
        long total = 0;
        foreach (var s in stack) total += s.Bytes.Length;
        while (stack.Count > 1 && (stack.Count > MaxUndo || total > MaxUndoBudgetBytes))
        {
            total -= stack[0].Bytes.Length;
            stack.RemoveAt(0);
        }
    }

    /// <summary>Capture the current state; call BEFORE any mutation.</summary>
    public void PushUndo()
    {
        _undo.Add((_bytes, Annotations.Select(a => a.Model.Clone()).ToList(), CloneImages()));
        TrimStack(_undo);
        _redo.Clear();
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
                Pages.Clear();
                for (int i = 0; i < Renderer.PageCount; i++)
                    Pages.Add(new PageViewModel(this, i));
                SearchResults.Clear();
                CurrentMatchIndex = -1;
                CurrentPageIndex = Math.Clamp(CurrentPageIndex, 0, Pages.Count - 1);
                RefreshVisiblePages();
                OnPropertyChanged(nameof(PageStatus));
                PagesReset?.Invoke();
            }
            Annotations.Clear();
            foreach (var a in annots) Annotations.Add(new AnnotationViewModel(this, a.Clone()));
            SelectedAnnotation = null;
            PlacedImages.Clear();
            foreach (var img in images) PlacedImages.Add(img.Clone());
            SelectedImage = null;
            IsDirty = true;
            AnnotationsVisualChanged?.Invoke();
        }
        finally { IsBusy = false; }
    }

    public string Title => Path.GetFileName(FilePath) ?? "Untitled";
    public string PageStatus => Pages.Count == 0 ? "" : $"{CurrentPageIndex + 1} / {Pages.Count}";
    public string MatchStatus => SearchResults.Count == 0 ? "No results"
        : $"{CurrentMatchIndex + 1} of {SearchResults.Count}";

    public event Action<int>? ScrollToPageRequested;
    public event Action? PagesReset;
    public event Action? AnnotationsVisualChanged;
    public event Action? ZoomChangedEvent;
    public event Action<int, RectD>? EditTextRequested;
    public event Action<int, RectD>? PlaceImageRequested;
    public event Action<int, RectD>? FreeTextRequested;
    public event Action<AnnotationViewModel>? EditFreeTextRequested;

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
        RefreshVisiblePages();
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

    public static Task<DocumentViewModel> FromBytesAsync(byte[] bytes, string? path)
    {
        return Task.Run(() =>
        {
            var renderer = new PdfRenderer(bytes);
            var index = new TextIndex(bytes);
            var annotations = AnnotationCodec.Read(bytes);
            var outline = index.GetOutline();
            return new DocumentViewModel(bytes, path, renderer, index, annotations, outline);
        });
    }

    // ------------------------------------------------------------- zoom / nav

    partial void OnZoomChanged(double value)
    {
        foreach (var p in Pages) p.OnZoomChanged();
        ZoomChangedEvent?.Invoke();
    }

    partial void OnCurrentPageIndexChanged(int value)
    {
        OnPropertyChanged(nameof(PageStatus));
        if (!IsContinuous && !VisiblePages.Any(p => p.Index == value)) RefreshVisiblePages();
        EvictFarPages(value);
    }

    /// <summary>How many pages on either side of the current page stay rendered at
    /// full size. Pages outside this window have their bitmap dropped — the
    /// ListBox container recreates them (and re-renders) automatically once they
    /// scroll back into view. Without this, a long document keeps every page it
    /// has ever displayed as a full-resolution bitmap for as long as it's open.</summary>
    private const int RenderBufferPages = 4;

    private void EvictFarPages(int centerIndex)
    {
        if (Pages.Count <= RenderBufferPages * 2 + 1) return; // small doc — nothing to gain
        int lo = centerIndex - RenderBufferPages;
        int hi = centerIndex + RenderBufferPages;
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
        int lo = CurrentPageIndex - RenderBufferPages;
        int hi = CurrentPageIndex + RenderBufferPages;
        foreach (var p in Pages)
        {
            p.InvalidateBitmaps();
            if (p.Index >= lo && p.Index <= hi)
                _ = p.EnsureRenderedAsync(force: true);
            else
                p.EvictFullImage();
            _ = p.EnsureThumbnailAsync();
        }
    }

    partial void OnFilePathChanged(string? value) => OnPropertyChanged(nameof(Title));

    partial void OnActiveToolChanged(ToolKind value)
    {
        if (_toolColors.TryGetValue(value, out var color)) ActiveColorHex = color;
        if (_toolWidths.TryGetValue(value, out var width)) ActiveStrokeWidth = width;
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

    // Reading history (Alt+Left / Alt+Right).
    private readonly List<int> _histBack = new();
    private readonly List<int> _histForward = new();

    public void GoToPage(int pageIndex, bool recordHistory = true)
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
        GoToPage(target, recordHistory: false);
    }

    public void NavigateForward()
    {
        if (_histForward.Count == 0) return;
        _histBack.Add(CurrentPageIndex);
        int target = _histForward[^1];
        _histForward.RemoveAt(_histForward.Count - 1);
        GoToPage(target, recordHistory: false);
    }

    // ------------------------------------------------------------- operations

    /// <summary>
    /// Run a structural operation (bytes -> bytes). Current annotations are baked
    /// into the PDF first so they travel with their pages, then re-read afterwards.
    /// </summary>
    public async Task ApplyOperationAsync(string busyMessage, Func<byte[], byte[]> operation)
    {
        IsBusy = true;
        BusyText = busyMessage;
        PushUndo();
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

            _bytes = newBytes;

            Annotations.Clear();
            foreach (var a in newAnnots) Annotations.Add(new AnnotationViewModel(this, a));
            SelectedAnnotation = null;
            PlacedImages.Clear();
            SelectedImage = null;

            Outline.Clear();
            foreach (var n in outline) Outline.Add(n);

            Pages.Clear();
            for (int i = 0; i < Renderer.PageCount; i++)
                Pages.Add(new PageViewModel(this, i));

            SearchResults.Clear();
            CurrentMatchIndex = -1;
            CurrentPageIndex = Math.Clamp(CurrentPageIndex, 0, Pages.Count - 1);
            RefreshVisiblePages();
            IsDirty = true;
            OnPropertyChanged(nameof(PageStatus));
            PagesReset?.Invoke();
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

    public async Task SaveAsync(string? path = null)
    {
        path ??= FilePath ?? throw new InvalidOperationException("No file path — use Save As.");
        IsBusy = true;
        BusyText = "Saving…";
        try
        {
            byte[] output = await Task.Run(GetBytesWithAnnotations);
            await File.WriteAllBytesAsync(path, output);
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
    internal void RequestFreeText(int pageIndex, RectD area) => FreeTextRequested?.Invoke(pageIndex, area);
    internal void RequestEditFreeText(AnnotationViewModel vm) => EditFreeTextRequested?.Invoke(vm);

    // ------------------------------------------------------------- search

    public async Task RunSearchAsync()
    {
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
            var results = await Task.Run(() => Index.Search(query));
            foreach (var r in results) SearchResults.Add(r);
            foreach (var g in results.GroupBy(r => r.PageIndex))
                if (g.Key < Pages.Count)
                    Pages[g.Key].SearchRects = g.SelectMany(r => r.Rects).ToList();
            if (SearchResults.Count > 0) GoToMatch(0);
            OnPropertyChanged(nameof(MatchStatus));
        }
        finally { IsBusy = false; }
    }

    public void GoToMatch(int index)
    {
        if (SearchResults.Count == 0) return;
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
            byte[] working = _bytes;
            await Task.Run(() =>
            {
                using var ocr = new OcrEngine();
                for (int p = 0; p < Pages.Count; p++)
                {
                    if (Index.HasText(p)) continue;
                    BusyText = $"OCR — page {p + 1} of {Pages.Count}…";

                    byte[] png = Renderer.RenderEncoded(p, 300.0 / 72.0);
                    var result = ocr.RecognizeImage(png);
                    var (w, h) = Renderer.PageSizes[p];
                    var words = OcrEngine.ToPageSpace(result, w, h).ToList();
                    if (words.Count == 0) continue;

                    Index.SetOcrWords(p, words, w, h);
                    working = SearchablePdfWriter.AddTextLayer(working, p, words);
                    recognized++;
                }
            });

            if (recognized > 0)
            {
                _bytes = working;
                IsDirty = true;
            }
            return recognized;
        }
        finally { IsBusy = false; }
    }
}
