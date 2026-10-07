using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Graphite.App.Interop;
using Graphite.App.Services;
using Graphite.App.ViewModels;
using Graphite.Core;
using Graphite.Core.Annotations;
using Graphite.Core.Editing;
using Microsoft.Win32;

namespace Graphite.App.Views;

public partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    private readonly Dictionary<DocumentViewModel, ListBox> _viewers = new();
    private readonly Dictionary<DocumentViewModel, double> _scrollOffsets = new();
    private readonly Dictionary<ListBox, SmoothScroller> _scrollers = new();
    private readonly HashSet<DocumentViewModel> _wired = new();
    private readonly DispatcherTimer _zoomTimer;
    // Each open document has its own inspector, so these are tracked per document.
    private readonly Dictionary<DocumentViewModel, TextBox> _searchBoxes = new();
    private readonly Dictionary<DocumentViewModel, RadioButton> _searchRadios = new();
    private bool _syncingThumbs;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = ViewModel;
        ViewModel.Documents.CollectionChanged += Documents_CollectionChanged;

        _zoomTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _zoomTimer.Tick += (_, _) =>
        {
            _zoomTimer.Stop();
            if (ViewModel.SelectedDocument is { } doc)
                foreach (var p in doc.Pages.Where(p => p.Image != null))
                    _ = p.EnsureRenderedAsync();
        };

        Loaded += (_, _) => Backdrop.Apply(this, ThemeService.IsDark);
        Loaded += (_, _) => PageViewModel.DeviceScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        // WindowStyle="None" + WindowChrome doesn't clamp maximize to the work area —
        // without this hook the maximized window covers the taskbar.
        Loaded += (_, _) => MaximizeWorkArea.Hook(this);
        Closing += MainWindow_Closing;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.PasteImageRequested += () =>
        {
            if (ViewModel.SelectedDocument is { } d && ClipboardHelper.Try(Clipboard.ContainsImage))
                PasteImageFromClipboard(d);
        };

        InitMotion();
        StateChanged += (_, _) => UpdateMaximizeRestoreIcon();
        Loaded += (_, _) => UpdateMaximizeRestoreIcon();

        // Quiet background update check shortly after launch — it only speaks up
        // when a newer release actually exists.
        Loaded += async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            _ = ViewModel.CheckForUpdatesAsync(silent: true);
        };
    }

    // ------------------------------------------------------------- custom caption buttons

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.MinimizeWindow(this);

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        SystemCommands.CloseWindow(this);

    private void UpdateMaximizeRestoreIcon()
    {
        bool maximized = WindowState == WindowState.Maximized;
        MaximizeRestoreIcon.Data = (Geometry)FindResource(maximized ? "Icon.WinRestore" : "Icon.WinMaximize");
        MaximizeRestoreButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    /// <summary>Moving to a monitor with different scaling: re-render the visible pages at
    /// the new pixel density (the zoom timer re-renders every page that has a bitmap).</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        PageViewModel.DeviceScale = newDpi.DpiScaleX;
        _zoomTimer.Stop();
        _zoomTimer.Start();
    }

    // ------------------------------------------------------------- fullscreen

    private WindowState _preFullscreenState = WindowState.Normal;
    private bool _preFullscreenSidebar, _preFullscreenInspector;

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsFullscreen))
            ApplyFullscreen(ViewModel.IsFullscreen);
        else if (e.PropertyName == nameof(MainViewModel.SelectedDocument))
            OnSelectedDocumentSwitched();
        else if (e.PropertyName == nameof(MainViewModel.ShowSidebar))
            AnimatePanels(true);
        else if (e.PropertyName == nameof(MainViewModel.ShowInspector))
            AnimatePanels(false);
        else if (e.PropertyName == nameof(MainViewModel.IsPaletteOpen) && ViewModel.IsPaletteOpen)
        {
            StaggerPalette();
            Dispatcher.BeginInvoke(() =>
            {
                PaletteBox.Focus();
                PaletteBox.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    private void ApplyFullscreen(bool on)
    {
        ApplyFullscreenChrome(on);
        if (on)
        {
            _preFullscreenState = WindowState;
            _preFullscreenSidebar = ViewModel.ShowSidebar;
            _preFullscreenInspector = ViewModel.ShowInspector;
            ViewModel.ShowSidebar = false;
            ViewModel.ShowInspector = false;
            // WindowStyle stays None (custom chrome) — fullscreen just drops the resize
            // border and covers the taskbar. Lift the work-area clamp so it really can.
            MaximizeWorkArea.AllowCoverTaskbar = true;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal; // force a state change so the taskbar is covered
            WindowState = WindowState.Maximized;
        }
        else
        {
            MaximizeWorkArea.AllowCoverTaskbar = false;
            ResizeMode = ResizeMode.CanResize;
            WindowState = _preFullscreenState;
            ViewModel.ShowSidebar = _preFullscreenSidebar;
            ViewModel.ShowInspector = _preFullscreenInspector;
        }
    }

    // ------------------------------------------------------------- lifecycle

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var dirty = ViewModel.Documents.Where(d => d.IsDirty).ToList();
        if (dirty.Count == 0) return;
        var answer = MessageDialog.Show(this,
            dirty.Count == 1
                ? "1 document has unsaved changes. Close anyway?"
                : $"{dirty.Count} documents have unsaved changes. Close anyway?",
            "Graphite", DialogButtons.YesNo, DialogIcon.Warning);
        if (answer != MessageBoxResult.Yes) e.Cancel = true;
    }

    private void Documents_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Drop closed documents from the tracking maps — otherwise the window keeps a
        // strong reference to every closed DocumentViewModel (its PDF bytes and rendered
        // page bitmaps included) for the app's lifetime.
        if (e.OldItems != null)
            foreach (DocumentViewModel doc in e.OldItems)
            {
                if (_zp != null && ReferenceEquals(_zp.Doc, doc)) CancelZoomPreview();
                _wired.Remove(doc);
                _viewers.Remove(doc);
                _searchBoxes.Remove(doc);
                _searchRadios.Remove(doc);
                ForgetTab(doc);
                _scrollOffsets.Remove(doc);
                _pins.Remove(doc);
                _lastShownPage.Remove(doc);
                _lastLayout.Remove(doc);
                _ocrScanning.Remove(doc);
                doc.PropertyChanged -= Doc_PropertyChanged;
                PruneInlineEditors(doc);
            }

        if (e.NewItems == null) return;
        foreach (DocumentViewModel doc in e.NewItems)
        {
            if (!_wired.Add(doc)) continue;
            doc.ScrollToPageRequested += i => ScrollToPage(doc, i);
            doc.ZoomChangedEvent += () => { _zoomTimer.Stop(); _zoomTimer.Start(); };
            doc.ZoomApplied += (oldZoom, newZoom) => OnZoomApplied(doc, oldZoom, newZoom);
            doc.PagesReset += () =>
            {
                PruneInlineEditors(doc);
                Dispatcher.BeginInvoke(() => ScrollToPage(doc, doc.CurrentPageIndex, instant: true));
            };
            doc.EditTextRequested += (page, rect) => OnEditTextRequested(doc, page, rect);
            doc.PlaceImageRequested += (page, rect) => OnPlaceImageRequested(doc, page, rect);
            doc.InlineEditRequested += vm => OnInlineEditRequested(doc, vm);
            doc.EditFreeTextRequested += vm => OnEditFreeTextRequested(doc, vm);
            doc.PropertyChanged += Doc_PropertyChanged;
            WireMotion(doc);
        }
    }

    private void Doc_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is DocumentViewModel changed)
            MotionOnDocPropertyChanged(changed, e.PropertyName);

        // Picking the Signature tool with no saved signature opens the drawing pad first.
        if (e.PropertyName == nameof(DocumentViewModel.ActiveTool) &&
            sender is DocumentViewModel { ActiveTool: ToolKind.Signature } doc &&
            !SignatureDialog.EnsureSignature(this))
            doc.ActiveTool = ToolKind.Select;
    }

    // ------------------------------------------------------------- viewer plumbing

    // Pages the user explicitly navigated to (arrow keys, buttons, thumbnails). While a page
    // is pinned, scrolling never re-derives the current page from the viewport, so the
    // counter can't drift away from the page the user just stepped to (last pages that
    // can't reach the top, zoomed-out views where several pages are visible, …). Any
    // manual scroll (wheel, scrollbar, touch pan, zoom) releases the pin.
    private readonly Dictionary<DocumentViewModel, int> _pins = new();
    private readonly Dictionary<DocumentViewModel, int> _lastShownPage = new();
    private int _trackingSuspended;   // >0 while we move the scroll position ourselves
    private bool _restoring;          // a tab switch / far jump is still settling
    private int _restoreGen;          // supersedes older settle loops
    private Point? _zoomAnchor;       // viewport point to keep fixed during the next zoom change

    private void PagesHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListBox lb && lb.DataContext is DocumentViewModel doc)
            _viewers[doc] = lb;
    }

    private void PagesHost_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Every document owns its viewer, so this runs once per tab, when the tab is created.
        if (sender is not ListBox lb) return;
        StopScroller(lb);
        if (e.NewValue is not DocumentViewModel doc) return;

        _viewers[doc] = lb;
        // A tab created in the background has nothing to restore yet; its position is
        // re-checked when it is first shown (OnSelectedDocumentSwitched).
        if (!ReferenceEquals(doc, ViewModel.SelectedDocument)) return;

        double target = _scrollOffsets.TryGetValue(doc, out var saved)
            ? saved
            : OffsetOfPage(doc, doc.CurrentPageIndex);
        _restoreGen++;
        _restoring = true;
        RestoreScrollWhenReady(lb, doc, target, 0, _restoreGen);
    }

    private async void RecentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: string path } lb)
        {
            lb.SelectedItem = null;
            if (File.Exists(path))
                await ViewModel.OpenFilesAsync(new[] { path });
            else
                MessageDialog.Show(this, "That file no longer exists.", "Graphite",
                    DialogButtons.OK, DialogIcon.Info);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root) => FindDescendant<ScrollViewer>(root);

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T match) return match;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    private const double PageGap = 12; // template margin 6 top + 6 bottom

    /// <summary>Model-based offset of a page's top edge (every page has an exact, bound
    /// height, so this matches the real layout when pages are realized; the virtualizing
    /// panel only estimates it for pages it hasn't built yet).</summary>
    private static double OffsetOfPage(DocumentViewModel doc, int pageIndex, double? zoom = null)
    {
        double z = zoom ?? doc.Zoom;
        double offset = 0;
        for (int i = 0; i < pageIndex && i < doc.Pages.Count; i++)
            offset += doc.Pages[i].HeightPt * z + PageGap;
        return offset;
    }

    /// <summary>Exact scroll offset that puts a page at the top of the viewport. Uses the
    /// real container when it is realized (immune to the panel's size estimates), the
    /// model otherwise.</summary>
    private static double PageOffset(ListBox lb, ScrollViewer sv, DocumentViewModel doc, int page)
    {
        if (lb.ItemContainerGenerator.ContainerFromIndex(page) is FrameworkElement { ActualHeight: > 0 } c)
        {
            try { return sv.VerticalOffset + c.TransformToAncestor(sv).Transform(new Point(0, 0)).Y; }
            catch (InvalidOperationException) { /* not in the visual tree yet */ }
        }
        return OffsetOfPage(doc, page);
    }

    private SmoothScroller? GetScroller(ListBox lb)
    {
        var sv = FindScrollViewer(lb);
        if (sv == null) return null;
        if (_scrollers.TryGetValue(lb, out var existing))
        {
            if (ReferenceEquals(existing.Viewer, sv)) return existing;
            existing.Stop();
        }
        return _scrollers[lb] = new SmoothScroller(sv);
    }

    private void StopScroller(ListBox lb)
    {
        if (_scrollers.TryGetValue(lb, out var s)) s.Stop();
    }

    /// <summary>Move to a page. Nearby pages glide there; far-away pages (and structural
    /// resets) jump, because animating across hundreds of virtualized pages would only
    /// thrash the renderer.</summary>
    private void ScrollToPage(DocumentViewModel doc, int pageIndex, bool instant = false)
    {
        if (!doc.IsContinuous) { AnimatePageTurn(doc); return; }
        if (!_viewers.TryGetValue(doc, out var lb) || !ReferenceEquals(lb.DataContext, doc)) return;
        var sv = FindScrollViewer(lb);
        if (sv == null || doc.Pages.Count == 0) return;
        pageIndex = Math.Clamp(pageIndex, 0, doc.Pages.Count - 1);

        _pins[doc] = pageIndex;
        _lastShownPage[doc] = pageIndex;

        double modelTarget = OffsetOfPage(doc, pageIndex);
        bool near = !instant && !_restoring && sv.ViewportHeight > 0 && sv.ExtentHeight > 0 &&
                    Math.Abs(modelTarget - sv.VerticalOffset) <= sv.ViewportHeight * 6;
        if (near && GetScroller(lb) is { } scroller)
        {
            int page = pageIndex;
            scroller.ScrollTo(() => PageOffset(lb, sv, doc, page), rate: 13);
            return;
        }

        StopScroller(lb);
        _restoreGen++;
        _restoring = true;
        JumpToPage(lb, doc, pageIndex, 0, _restoreGen);
    }

    /// <summary>Instant jump: realize the target page, then nudge until its real top edge
    /// is at the top of the viewport (the panel's offsets are estimates until the pages in
    /// between are built, so one ScrollToVerticalOffset isn't reliable for mixed page sizes).</summary>
    private void JumpToPage(ListBox lb, DocumentViewModel doc, int page, int attempt, int gen)
    {
        if (gen != _restoreGen) return; // superseded by a newer jump / tab switch
        var sv = FindScrollViewer(lb);
        if (sv == null || !doc.IsContinuous || lb.Items.Count == 0 ||
            !_viewers.TryGetValue(doc, out var cur) || !ReferenceEquals(cur, lb) ||
            !ReferenceEquals(lb.DataContext, doc))
        {
            FinishRestore(lb, doc);
            return;
        }
        page = Math.Clamp(page, 0, lb.Items.Count - 1);

        if (lb.ItemContainerGenerator.ContainerFromIndex(page) is not FrameworkElement { ActualHeight: > 0 } c)
        {
            if (attempt >= 30) { FinishRestore(lb, doc); return; }
            lb.ScrollIntoView(lb.Items[page]); // forces the container to be built
            Dispatcher.BeginInvoke(() => JumpToPage(lb, doc, page, attempt + 1, gen), DispatcherPriority.Background);
            return;
        }

        double top;
        try { top = c.TransformToAncestor(sv).Transform(new Point(0, 0)).Y; }
        catch (InvalidOperationException)
        {
            Dispatcher.BeginInvoke(() => JumpToPage(lb, doc, page, attempt + 1, gen), DispatcherPriority.Background);
            return;
        }

        double max = Math.Max(0, sv.ExtentHeight - sv.ViewportHeight);
        double target = Math.Clamp(sv.VerticalOffset + top, 0, max);
        if (attempt < 12 && Math.Abs(target - sv.VerticalOffset) > 0.5)
        {
            sv.ScrollToVerticalOffset(target);
            Dispatcher.BeginInvoke(() => JumpToPage(lb, doc, page, attempt + 1, gen), DispatcherPriority.Background);
            return;
        }
        FinishRestore(lb, doc);
    }

    /// <summary>Scroll to <paramref name="offset"/> once the ScrollViewer's extent can
    /// actually hold it. Right after a tab switch (or a pages reset) the extent is still
    /// near zero because containers realize lazily — a premature ScrollToVerticalOffset
    /// gets clamped to ~0 and the position is lost. Realize the target page first, then
    /// retry on background priority until the extent is built (or we give up). Page
    /// tracking stays suspended meanwhile, so the intermediate offsets (0, then clamped
    /// values) can't overwrite the document's current page.</summary>
    private void RestoreScrollWhenReady(ListBox lb, DocumentViewModel doc, double offset, int attempt, int gen)
    {
        if (gen != _restoreGen) return; // user switched tabs again / another jump took over
        var sv = FindScrollViewer(lb);
        if (sv == null || !doc.IsContinuous ||
            !_viewers.TryGetValue(doc, out var current) || !ReferenceEquals(current, lb) ||
            !ReferenceEquals(lb.DataContext, doc))
        {
            FinishRestore(lb, doc);
            return;
        }

        if (attempt == 0)
        {
            StopScroller(lb);
            // Force realization up to the target page so the extent becomes real.
            int page = PageIndexAtOffset(doc, offset);
            if (page >= 0 && page < lb.Items.Count) lb.ScrollIntoView(lb.Items[page]);
        }

        double max = Math.Max(0, sv.ExtentHeight - sv.ViewportHeight);
        if (max >= offset - 1 || attempt >= 30)
        {
            sv.ScrollToVerticalOffset(Math.Min(offset, max));
            // Let that final scroll lay out, then re-sync the counter from the real viewport.
            Dispatcher.BeginInvoke(() =>
            {
                if (gen == _restoreGen) FinishRestore(lb, doc);
            }, DispatcherPriority.Background);
            return;
        }
        Dispatcher.BeginInvoke(() => RestoreScrollWhenReady(lb, doc, offset, attempt + 1, gen),
            DispatcherPriority.Background);
    }

    private void FinishRestore(ListBox lb, DocumentViewModel doc)
    {
        _restoring = false;
        if (ReferenceEquals(lb.DataContext, doc)) SyncFromViewport(lb, doc);
    }

    private static int PageIndexAtOffset(DocumentViewModel doc, double offset)
    {
        double y = 0;
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            y += doc.Pages[i].DisplayHeight + PageGap;
            if (y > offset) return i;
        }
        return doc.Pages.Count - 1;
    }

    private void PagesHost_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ListBox lb || lb.DataContext is not DocumentViewModel doc || !doc.IsContinuous)
            return;

        // ScrollChanged BUBBLES: any nested ScrollViewer inside a page container reports
        // here too — notably the inline text editor's content host, which fires it on
        // every keystroke as the caret moves. Its tiny offsets (viewport ~12 DIP) would
        // reset the current-page probe to page 1 per keystroke, and the eviction window
        // that follows would blank the very page being edited. Only the ListBox's own
        // ScrollViewer may drive current-page tracking.
        if (!ReferenceEquals(e.OriginalSource, FindScrollViewer(lb)))
            return;

        // Mid tab-switch / jump / zoom the offsets are transient — settle first.
        if (_trackingSuspended > 0 || _restoring) return;
        if (e.VerticalChange == 0 && e.ViewportHeightChange == 0 && e.ExtentHeightChange == 0) return;

        SyncFromViewport(lb, doc);
    }

    /// <summary>Re-derive which pages are on screen (for rendering) and which one is
    /// "current" (for the counter) from where the viewer really is.</summary>
    private void SyncFromViewport(ListBox lb, DocumentViewModel doc)
    {
        if (!doc.IsContinuous || doc.Pages.Count == 0) return;
        var sv = FindScrollViewer(lb);
        if (sv == null || sv.ViewportHeight <= 0) return;

        if (!TryProbe(lb, sv, out int first, out int last, out int current) &&
            !ModelProbe(doc, sv, out first, out last, out current))
            return;

        // A page the user navigated to stays the current one until they scroll themselves.
        if (_pins.TryGetValue(doc, out int pin))
        {
            if (doc.CurrentPageIndex == pin && pin >= 0 && pin < doc.Pages.Count) current = pin;
            else _pins.Remove(doc);
        }

        if (doc.CurrentPageIndex != current)
        {
            _syncingThumbs = true;
            try { doc.CurrentPageIndex = current; }
            finally { _syncingThumbs = false; }
        }
        doc.UpdateViewport(first, last);
    }

    /// <summary>Read the real layout: which realized page containers overlap the viewport,
    /// and which one sits a third of the way down (that one becomes the current page).</summary>
    private static bool TryProbe(ListBox lb, ScrollViewer sv, out int first, out int last, out int current)
    {
        first = last = current = -1;
        if (FindDescendant<VirtualizingPanel>(sv) is not { } panel) return false;

        double viewport = sv.ViewportHeight;
        double probe = viewport / 3;
        int below = -1;
        double belowTop = double.MaxValue;

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
            if (bottom <= 0 || top >= viewport) continue; // realized but off screen (cache)

            if (first < 0 || idx < first) first = idx;
            if (idx > last) last = idx;
            if (top <= probe && probe < bottom) current = idx;
            else if (top > probe && top < belowTop) { below = idx; belowTop = top; }
        }

        if (first < 0) return false;
        if (current < 0) current = below >= 0 ? below : last;
        return true;
    }

    /// <summary>Fallback when no containers are realized yet: walk the model heights.</summary>
    private static bool ModelProbe(DocumentViewModel doc, ScrollViewer sv, out int first, out int last, out int current)
    {
        double top = sv.VerticalOffset;
        double bottom = top + sv.ViewportHeight;
        double probe = top + sv.ViewportHeight / 3;
        first = last = current = -1;
        double y = 0;
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            double pageTop = y;
            y += doc.Pages[i].DisplayHeight + PageGap;
            if (first < 0 && y > top) first = i;
            if (current < 0 && y >= probe) current = i;
            if (pageTop < bottom) last = i;
            else break;
        }
        if (first < 0) return false;
        if (current < 0) current = last;
        return true;
    }

    // ------------------------------------------------------------- wheel, zoom, touch

    private void PagesHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox { DataContext: DocumentViewModel doc } lb) return;
        var sv = FindScrollViewer(lb);
        if (sv == null) return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            // Proportional + anchored: a precision-touchpad pinch arrives as Ctrl+wheel with
            // tiny deltas, so zoom follows the gesture smoothly, centred on the pointer. The
            // pages are scaled live and the real zoom is applied once the gesture settles.
            WheelZoomStep(lb, doc, e.GetPosition(sv), Math.Pow(1.0011, e.Delta));
            e.Handled = true;
            return;
        }

        if (_zp != null) CommitZoomPreview(); // wheel scrolling right after a zoom: settle it first

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
            e.Handled = true;
            return;
        }

        // Eased wheel scrolling: animate toward a target offset instead of jumping
        // line-by-line (works for both notched wheels and precision touchpads).
        _pins.Remove(doc);
        GetScroller(lb)?.ScrollBy(-e.Delta * 1.0);
        e.Handled = true;
    }

    private void PagesHost_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Grabbing the scrollbar is a manual scroll: stop any glide and release the pin.
        if (sender is ListBox lb && lb.DataContext is DocumentViewModel doc &&
            e.OriginalSource is DependencyObject src &&
            FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(src) != null)
        {
            StopScroller(lb);
            _pins.Remove(doc);
        }
    }

    /// <summary>Zoom changed: keep the content under the anchor (pinch centre, pointer, or
    /// viewport centre) where it was, instead of letting it slide away from under the user.</summary>
    private void OnZoomApplied(DocumentViewModel doc, double oldZoom, double newZoom)
    {
        if (oldZoom <= 0 || Math.Abs(newZoom - oldZoom) < 1e-9 || _restoring) return;
        if (_zp != null) CancelZoomPreview(); // zoom changed some other way (keys, buttons)
        if (!_viewers.TryGetValue(doc, out var lb) || !ReferenceEquals(lb.DataContext, doc)) return;
        var sv = FindScrollViewer(lb);
        if (sv == null || sv.ViewportHeight <= 0 || sv.ViewportWidth <= 0) return;

        StopScroller(lb);
        _pins.Remove(doc);

        double vw = sv.ViewportWidth, vh = sv.ViewportHeight;
        var a = _zoomAnchor ?? new Point(vw / 2, vh / 2);
        a = new Point(Math.Clamp(a.X, 0, vw), Math.Clamp(a.Y, 0, vh));
        double ratio = newZoom / oldZoom;

        // Where the anchor sits horizontally, relative to the centre line pages are centred on.
        double wOld = Math.Max(sv.ExtentWidth, vw);
        double xRel = sv.HorizontalOffset + a.X - wOld / 2;
        double yOld = sv.VerticalOffset + a.Y;

        // Vertically: which page is under the anchor, and how far into it (in page points).
        // Read from the real containers first — the panel's own offsets are only estimates
        // for pages it hasn't built, so deriving the page from them could pick the wrong one.
        int anchorPage = -1;
        double innerPt = 0;
        if (doc.IsContinuous && doc.Pages.Count > 0 &&
            !TryAnchorFromContainers(lb, sv, a.Y, oldZoom, out anchorPage, out innerPt))
        {
            double off = 0;
            for (int i = 0; i < doc.Pages.Count; i++)
            {
                double h = doc.Pages[i].HeightPt * oldZoom + PageGap;
                if (yOld < off + h || i == doc.Pages.Count - 1)
                {
                    anchorPage = i;
                    innerPt = (yOld - off - PageGap / 2) / oldZoom;
                    break;
                }
                off += h;
            }
        }

        _trackingSuspended++;
        try
        {
            double newV = anchorPage >= 0
                ? OffsetOfPage(doc, anchorPage, newZoom) + PageGap / 2 + innerPt * newZoom - a.Y
                : yOld * ratio - a.Y;

            lb.UpdateLayout(); // pages now have their new sizes
            sv.ScrollToVerticalOffset(Math.Max(0, newV));
            double wNew = Math.Max(sv.ExtentWidth, vw);
            sv.ScrollToHorizontalOffset(Math.Max(0, wNew / 2 + xRel * ratio - a.X));
            lb.UpdateLayout();

            // One more correction against the real container pins the anchored point exactly
            // (realizing the page first if the estimate left it off screen).
            if (anchorPage >= 0) PinAnchor(lb, sv, anchorPage, innerPt, newZoom, a.Y);
        }
        finally { _trackingSuspended--; }
        SyncFromViewport(lb, doc);
    }

    // ---- touch: two-finger pinch zoom + pan

    private readonly Dictionary<int, TouchDevice> _touchDevices = new();
    private int _pinchIdA = -1, _pinchIdB = -1;
    private double _pinchStartDist, _pinchStartZoom;
    private Point _pinchCentre;

    private void PagesHost_PreviewTouchDown(object sender, TouchEventArgs e)
    {
        if (sender is not ListBox { DataContext: DocumentViewModel doc } lb) return;
        var sv = FindScrollViewer(lb);
        if (sv == null) return;

        foreach (var stale in _touchDevices.Where(kv => !kv.Value.IsActive).Select(kv => kv.Key).ToList())
            _touchDevices.Remove(stale);
        _touchDevices[e.TouchDevice.Id] = e.TouchDevice;

        if (_pinchIdA < 0 && _touchDevices.Count >= 2)
        {
            var two = _touchDevices.Values.Take(2).ToArray();
            BeginPinch(lb, sv, doc, two[0], two[1]);
        }
        if (_pinchIdA >= 0) e.Handled = true; // fingers belong to the gesture, not the page tools
    }

    private void BeginPinch(ListBox lb, ScrollViewer sv, DocumentViewModel doc, TouchDevice a, TouchDevice b)
    {
        if (_zp != null) CommitZoomPreview(); // settle a wheel zoom still in flight before reading the zoom
        Point pa = a.GetTouchPoint(sv).Position, pb = b.GetTouchPoint(sv).Position;
        _pinchIdA = a.Id;
        _pinchIdB = b.Id;
        _pinchStartDist = Math.Max(1, (pa - pb).Length);
        _pinchStartZoom = doc.Zoom;
        _pinchCentre = new Point((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2);

        StopScroller(lb);
        _pins.Remove(doc);
        // The pages are scaled live under the fingers; the real zoom is applied on release.
        BeginZoomPreview(lb, doc, _pinchCentre, eased: false);
        // The first finger already started a promoted mouse gesture (an ink stroke, a drag…).
        // Dropping the mouse capture makes the annotation layer cancel it.
        Mouse.Capture(null);
        lb.CaptureTouch(a);
        lb.CaptureTouch(b);
    }

    private void PagesHost_PreviewTouchMove(object sender, TouchEventArgs e)
    {
        if (_pinchIdA < 0 || sender is not ListBox lb) return;
        int id = e.TouchDevice.Id;
        if (id != _pinchIdA && id != _pinchIdB) { e.Handled = true; return; }
        e.Handled = true;
        UpdatePinch(lb);
    }

    private void UpdatePinch(ListBox lb)
    {
        if (lb.DataContext is not DocumentViewModel doc || FindScrollViewer(lb) is not { } sv ||
            !_touchDevices.TryGetValue(_pinchIdA, out var a) || !_touchDevices.TryGetValue(_pinchIdB, out var b))
        {
            EndPinch(lb);
            return;
        }

        Point pa = a.GetTouchPoint(sv).Position, pb = b.GetTouchPoint(sv).Position;
        var centre = new Point((pa.X + pb.X) / 2, (pa.Y + pb.Y) / 2);
        double dist = Math.Max(1, (pa - pb).Length);

        if (_zp is { Eased: false } zp && ReferenceEquals(zp.List, lb))
        {
            // Live preview: no layout, just a GPU scale + pan of the pages already on screen.
            zp.Factor = dist / _pinchStartDist;
            zp.Shift = centre - zp.Anchor;
            ApplyZoomPreview(zp);
            _pinchCentre = centre;
            return;
        }

        // Fallback (no preview surface): zoom the document directly, as before.
        double zoom = Math.Clamp(_pinchStartZoom * dist / _pinchStartDist, MinZoom, MaxZoom);

        if (Math.Abs(zoom - doc.Zoom) > 0.0005)
        {
            _zoomAnchor = _pinchCentre; // content under the previous centre stays put…
            try { doc.Zoom = zoom; }
            finally { _zoomAnchor = null; }
        }

        double dx = centre.X - _pinchCentre.X, dy = centre.Y - _pinchCentre.Y;
        if (dx != 0 || dy != 0)
        {
            // …then the two-finger drag carries it to the new centre.
            _trackingSuspended++;
            try
            {
                sv.ScrollToHorizontalOffset(Math.Max(0, sv.HorizontalOffset - dx));
                sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset - dy));
                lb.UpdateLayout();
            }
            finally { _trackingSuspended--; }
            SyncFromViewport(lb, doc);
        }
        _pinchCentre = centre;
    }

    private void PagesHost_PreviewTouchUp(object sender, TouchEventArgs e)
    {
        // Not marked handled: the primary finger's promoted mouse-up must still arrive so
        // the system's button state doesn't stay "pressed".
        int id = e.TouchDevice.Id;
        _touchDevices.Remove(id);
        if (id == _pinchIdA || id == _pinchIdB) EndPinch(sender as ListBox);
    }

    private void PagesHost_LostTouchCapture(object sender, TouchEventArgs e)
    {
        if (e.TouchDevice.IsActive) return; // capture just moved elsewhere; the finger is still down
        int id = e.TouchDevice.Id;
        _touchDevices.Remove(id);
        if (id == _pinchIdA || id == _pinchIdB) EndPinch(sender as ListBox);
    }

    private void EndPinch(ListBox? lb)
    {
        if (_pinchIdA < 0) return;
        int a = _pinchIdA, b = _pinchIdB;
        _pinchIdA = _pinchIdB = -1;
        CommitZoomPreview(); // apply the pinched zoom for real, once, on release
        if (lb == null) return;
        if (_touchDevices.TryGetValue(a, out var da)) lb.ReleaseTouchCapture(da);
        if (_touchDevices.TryGetValue(b, out var db)) lb.ReleaseTouchCapture(db);
        if (lb.DataContext is DocumentViewModel doc) SyncFromViewport(lb, doc);
    }

    // ------------------------------------------------------------- animation helpers

    /// <summary>Single / spread layouts swap their pages in place, so a short fade-and-slide
    /// (direction follows the page number) keeps the turn from feeling like a hard cut.</summary>
    private void AnimatePageTurn(DocumentViewModel doc)
    {
        if (!_viewers.TryGetValue(doc, out var lb) || !ReferenceEquals(lb.DataContext, doc)) return;
        int page = doc.CurrentPageIndex;
        bool had = _lastShownPage.TryGetValue(doc, out int prev);
        _lastShownPage[doc] = page;
        FindScrollViewer(lb)?.ScrollToTop();
        if (!had || prev == page || !Motion.Enabled) return;

        if (lb.RenderTransform is not TranslateTransform tt)
            lb.RenderTransform = tt = new TranslateTransform();
        var span = TimeSpan.FromMilliseconds(220);
        tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(page > prev ? 22 : -22, 0, span)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        });
        lb.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, span) { FillBehavior = FillBehavior.Stop });
    }

    // ------------------------------------------------------------- tab switching

    private DocumentViewModel? _shownDoc;

    /// <summary>The selected tab changed. Every document keeps its own viewer, so this is a
    /// visibility flip: remember where the outgoing tab was, play the entrance for the
    /// incoming one, and make sure it is still at the same spot.</summary>
    private void OnSelectedDocumentSwitched()
    {
        var prev = _shownDoc;
        var next = ViewModel.SelectedDocument;
        if (ReferenceEquals(prev, next)) return;
        _shownDoc = next;

        CancelZoomPreview(); // a gesture belongs to the tab it started on

        if (prev != null && ViewModel.Documents.Contains(prev) && !_restoring &&
            _viewers.TryGetValue(prev, out var prevList) && FindScrollViewer(prevList) is { } prevScroller)
            _scrollOffsets[prev] = prevScroller.VerticalOffset;

        NoteTabSwitch(prev, next);
        if (next == null) return;
        AnimateDocSwitch(prev, next);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            EnsureScrollRestored(next);
            next.RenderShownPages();
            // Fullscreen: the new tab's floating page bar follows the shown/hidden state.
            if (ViewModel.IsFullscreen) ShowPageBar(_pageBarShown, animate: false, force: true);
        });
    }

    /// <summary>Hidden viewers keep their scroll position, so this normally has nothing to do;
    /// it is the safety net that puts a tab back where the user left it if it ever moved.</summary>
    private void EnsureScrollRestored(DocumentViewModel doc)
    {
        if (!ReferenceEquals(ViewModel.SelectedDocument, doc) || !doc.IsContinuous) return;
        if (!_viewers.TryGetValue(doc, out var lb) || !ReferenceEquals(lb.DataContext, doc) || !lb.IsVisible) return;
        if (FindScrollViewer(lb) is not { } sv) return;

        if (_scrollOffsets.TryGetValue(doc, out double saved) && Math.Abs(sv.VerticalOffset - saved) > 2)
        {
            _restoreGen++;
            _restoring = true;
            RestoreScrollWhenReady(lb, doc, saved, 0, _restoreGen);
            return;
        }
        SyncFromViewport(lb, doc); // make sure the visible pages have their bitmaps
    }

    /// <summary>Tab switch: the incoming document slides in from the side its tab is on
    /// (left when going back towards the first tab, right when moving forward) and fades up.
    /// A brand-new tab plays its own rise-in instead.</summary>
    private void AnimateDocSwitch(DocumentViewModel? prev, DocumentViewModel next)
    {
        if (!Motion.Enabled) return;
        if (DocHost.ItemContainerGenerator.ContainerFromItem(next) is not FrameworkElement { IsLoaded: true } host ||
            Motion.RigOf(host) is not { } rig)
            return;

        int from = prev == null ? -1 : ViewModel.Documents.IndexOf(prev);
        int to = ViewModel.Documents.IndexOf(next);
        double direction = from < 0 || from == to ? 0 : to > from ? 1 : -1;

        Motion.Tween(host, OpacityProperty, 0, 1, 240);
        Motion.Tween(rig.Scale, ScaleTransform.ScaleXProperty, 0.97, 1, 300);
        Motion.Tween(rig.Scale, ScaleTransform.ScaleYProperty, 0.97, 1, 300);
        if (direction != 0)
            Motion.Tween(rig.Move, TranslateTransform.XProperty, direction * 44, 0, 320);
    }

    /// <summary>Time-based exponential glide toward a (possibly moving) target offset,
    /// driven by CompositionTarget.Rendering so it follows the monitor's refresh rate and
    /// feels identical at 60 and 144 Hz.</summary>
    private sealed class SmoothScroller
    {
        private readonly ScrollViewer _sv;
        private Func<double>? _provider;
        private double _fixed;
        private double _rate = 18;
        private bool _animating;
        private long _last;
        private Action? _arrived;

        public SmoothScroller(ScrollViewer sv) => _sv = sv;

        public ScrollViewer Viewer => _sv;
        public bool IsAnimating => _animating;

        private double Max => Math.Max(0, _sv.ExtentHeight - _sv.ViewportHeight);
        private double Wanted => Math.Clamp(_provider?.Invoke() ?? _fixed, 0, Max);

        /// <summary>Wheel step: accumulates onto the glide already in flight.</summary>
        public void ScrollBy(double delta) =>
            Begin(null, (_animating ? Wanted : _sv.VerticalOffset) + delta, null, 18);

        /// <summary>Glide to a target that is re-evaluated every frame (so it can follow a
        /// page whose real position firms up as it gets realized).</summary>
        public void ScrollTo(Func<double> target, Action? arrived = null, double rate = 14) =>
            Begin(target, 0, arrived, rate);

        private void Begin(Func<double>? provider, double fixedTarget, Action? arrived, double rate)
        {
            _provider = provider;
            _fixed = fixedTarget;
            _arrived = arrived;
            _rate = rate;
            if (_animating) return;
            _animating = true;
            _last = System.Diagnostics.Stopwatch.GetTimestamp();
            CompositionTarget.Rendering += Step;
        }

        /// <summary>Stop gliding right here (a manual scroll or a jump takes over).</summary>
        public void Stop()
        {
            if (!_animating) return;
            CompositionTarget.Rendering -= Step;
            _animating = false;
            _arrived = null;
            _provider = null;
        }

        private void Step(object? sender, EventArgs e)
        {
            if (!_sv.IsLoaded) { Stop(); return; }

            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double dt = Math.Clamp((now - _last) / (double)System.Diagnostics.Stopwatch.Frequency, 0.001, 0.05);
            _last = now;

            double current = _sv.VerticalOffset;
            double target = Wanted;
            double next = current + (target - current) * (1 - Math.Exp(-_rate * dt));
            if (Math.Abs(target - next) < 0.5)
            {
                CompositionTarget.Rendering -= Step;
                _animating = false;
                _sv.ScrollToVerticalOffset(target);
                var arrived = _arrived;
                _arrived = null;
                _provider = null;
                arrived?.Invoke();
                return;
            }
            _sv.ScrollToVerticalOffset(next);
        }
    }

    private void PageRoot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PageViewModel page })
            _ = page.EnsureRenderedAsync();
    }

    private void PageRoot_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is PageViewModel page)
            _ = page.EnsureRenderedAsync();
    }

    private void Thumb_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PageViewModel page })
            _ = page.EnsureThumbnailAsync();
    }

    // With container recycling, a scrolled-back-in thumbnail gets a DataContext swap
    // instead of a fresh Loaded event — render on both so recycled containers fill in.
    private void Thumb_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is PageViewModel page)
            _ = page.EnsureThumbnailAsync();
    }

    private void Thumbs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Keep the highlighted thumbnail in view as the current page changes.
        if (sender is ListBox { SelectedItem: { } selected } list) list.ScrollIntoView(selected);
        if (_syncingThumbs) return;
        if (sender is ListBox { IsMouseOver: true, SelectedIndex: >= 0 } lb &&
            lb.DataContext is DocumentViewModel doc)
            doc.GoToPage(lb.SelectedIndex);
    }

    private void Outline_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is OutlineNode { PageIndex: { } page } &&
            ViewModel.SelectedDocument is { } doc)
            doc.GoToPage(page, flash: true);
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SelectedDocument?.StepPage(-1);

    private void NextPage_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SelectedDocument?.StepPage(+1);

    private void FitWidth_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDocument is not { } doc) return;
        if (!_viewers.TryGetValue(doc, out var lb)) return;
        var sv = FindScrollViewer(lb);
        double viewport = sv?.ViewportWidth > 0 ? sv.ViewportWidth : lb.ActualWidth;
        double maxWidth = doc.Pages.Max(p => p.WidthPt);
        if (maxWidth > 0 && viewport > 60)
            doc.Zoom = Math.Clamp((viewport - 48) / maxWidth, 0.25, 6);
    }

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedDocument is { } doc) doc.Zoom = 1.0;
    }

    // ------------------------------------------------------------- tab strip

    private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: { } item } lb) lb.ScrollIntoView(item);
    }

    /// <summary>With more tabs than fit, the wheel scrolls the strip sideways.</summary>
    private void TabStrip_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox lb || FindScrollViewer(lb) is not { ScrollableWidth: > 0 } sv) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    /// <summary>Left-click selects a tab, middle-click closes it, like a browser.
    /// Selection is done by hand: the tab items are deliberately not focusable (so a click
    /// doesn't steal keyboard focus from the document), and a ListBoxItem that can't take
    /// focus never selects itself on click — which is why tabs couldn't be switched.</summary>
    private void TabStrip_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject src ||
            FindAncestor<ListBoxItem>(src) is not { DataContext: DocumentViewModel doc })
            return;

        if (e.ChangedButton == MouseButton.Middle)
        {
            ViewModel.CloseDocumentCommand.Execute(doc);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left &&
                 FindAncestor<System.Windows.Controls.Button>(src) == null) // the ✕ keeps its own click
        {
            ViewModel.SelectedDocument = doc;
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------- colour popup

    private void ColorPopup_Opened(object? sender, EventArgs e)
    {
        if (!Motion.Enabled) return;
        var span = TimeSpan.FromMilliseconds(170);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        if (ColorCard.RenderTransform is ScaleTransform st)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, span) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.88, 1, span) { EasingFunction = ease });
        }
        ColorCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, span));
    }

    /// <summary>Picking a swatch applies the colour (via its command) and closes the popup.</summary>
    private void ColorSwatch_Click(object sender, RoutedEventArgs e) => ColorToggle.IsChecked = false;

    // ------------------------------------------------------------- menus

    private void OrganizeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } fe)
        {
            menu.DataContext = ViewModel;
            menu.PlacementTarget = fe;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
            FlipChevron(fe, menu);
        }
    }

    /// <summary>Click-again pattern: once the Signature tool
    /// is already active, clicking it again opens its menu (Edit signature…).</summary>
    private void SignatureTool_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedDocument?.ActiveTool != ToolKind.Signature) return;
        if (sender is not FrameworkElement { ContextMenu: { } menu } fe) return;

        e.Handled = true;
        Dispatcher.BeginInvoke(() =>
        {
            menu.DataContext = ViewModel;
            menu.PlacementTarget = fe;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }, DispatcherPriority.Input);
    }

    // ------------------------------------------------------------- search

    private void SearchBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: DocumentViewModel doc } box) _searchBoxes[doc] = box;
    }

    private void SearchRadio_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { DataContext: DocumentViewModel doc } radio) _searchRadios[doc] = radio;
    }

    private async void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel.SelectedDocument is not { } doc) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await doc.RunSearchAsync();
        }
        else if (e.Key == Key.Escape)
        {
            doc.SearchQuery = "";
            await doc.RunSearchAsync();
        }
    }

    private void SearchNext_Click(object sender, RoutedEventArgs e) => ViewModel.SelectedDocument?.NextMatch();
    private void SearchPrev_Click(object sender, RoutedEventArgs e) => ViewModel.SelectedDocument?.PrevMatch();

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedIndex: >= 0 } lb &&
            ViewModel.SelectedDocument is { } doc)
            doc.GoToMatch(lb.SelectedIndex);
    }

    // ------------------------------------------------------------- content edits

    private async void OnEditTextRequested(DocumentViewModel doc, int pageIndex, RectD area)
    {
        double autoSize = Math.Clamp(area.Height * 0.72, 6, 32);
        var result = EditTextDialog.Show(this, "Edit text",
            "Replacement text (the selected region is covered and re-typeset — leave empty to erase):",
            autoSize: autoSize);
        if (result is not { } r) return;
        try
        {
            await doc.ApplyOperationAsync("Editing text…",
                b => ContentEditor.ReplaceText(b, pageIndex, area, r.Text,
                    fontSize: r.Size ?? autoSize));
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, ex.Message, "Graphite", DialogButtons.OK, DialogIcon.Warning);
        }
        doc.ActiveTool = ToolKind.Select;
    }

    // ------------------------------------------------------------- inline text editing

    private readonly Dictionary<PageViewModel, TextBox> _inlineEditors = new();
    private (DocumentViewModel Doc, AnnotationViewModel Vm, TextBox Tb)? _inlineEdit;

    private void InlineEditor_Loaded(object sender, RoutedEventArgs e) => TrackInlineEditor(sender);
    private void InlineEditor_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        TrackInlineEditor(sender);

    private void TrackInlineEditor(object sender)
    {
        // With container recycling the same TextBox serves different pages — always
        // remap to the CURRENT DataContext.
        if (sender is not TextBox tb) return;
        // Drop any page this TextBox used to serve; otherwise the map keeps growing with
        // every page ever realized and pins their view-models (and bitmaps) in memory.
        foreach (var stale in _inlineEditors.Where(kv => ReferenceEquals(kv.Value, tb)).Select(kv => kv.Key).ToList())
            _inlineEditors.Remove(stale);
        if (tb.DataContext is PageViewModel page)
            _inlineEditors[page] = tb;
    }

    /// <summary>Forget inline-editor mappings for pages that no longer exist — after a
    /// structural edit replaced the document's pages, or when the document was closed.
    /// These entries were a leak: each one kept a whole closed document alive.</summary>
    private void PruneInlineEditors(DocumentViewModel doc)
    {
        var live = ViewModel.Documents.Contains(doc) ? doc.Pages.ToHashSet() : new HashSet<PageViewModel>();
        foreach (var page in _inlineEditors.Keys.Where(p => p.Doc == doc && !live.Contains(p)).ToList())
            _inlineEditors.Remove(page);
        if (_inlineEdit is { } edit && edit.Doc == doc && !ViewModel.Documents.Contains(doc))
            _inlineEdit = null;
    }

    private void OnInlineEditRequested(DocumentViewModel doc, AnnotationViewModel vm)
    {
        // Deferred: the annotation visuals settle first, and the page container must exist.
        Dispatcher.BeginInvoke(() =>
        {
            if (vm.PageIndex >= doc.Pages.Count) return;
            var page = doc.Pages[vm.PageIndex];
            if (!_inlineEditors.TryGetValue(page, out var tb)) return;

            double s = page.DisplayWidth / Math.Max(page.WidthPt, 1);
            var b = vm.Model.Bounds;
            Canvas.SetLeft(tb, b.X * s);
            Canvas.SetTop(tb, b.Y * s);
            tb.Width = Math.Max(60, b.Width * s);
            tb.FontSize = Math.Max(6, vm.Model.FontSize * s);
            tb.FontFamily = new FontFamily(vm.Model.FontFamily);
            Brush fg;
            try { fg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(vm.Model.ColorHex)); }
            catch { fg = Brushes.Black; }
            tb.Foreground = fg;
            tb.CaretBrush = fg;
            tb.Text = vm.Model.Contents;
            tb.Visibility = Visibility.Visible;
            _inlineEdit = (doc, vm, tb);
            tb.Focus();
            tb.CaretIndex = tb.Text.Length;
        }, DispatcherPriority.Input);
    }

    private void CommitInlineEdit(bool cancel = false)
    {
        if (_inlineEdit is not { } edit) return;
        _inlineEdit = null;
        edit.Tb.Visibility = Visibility.Collapsed;
        string text = edit.Tb.Text.Trim();
        if (cancel || text.Length == 0)
        {
            // Nothing written — quietly drop the just-created empty text box (the
            // creation undo snapshot already covers it, no extra undo step).
            edit.Doc.DiscardAnnotation(edit.Vm);
            return;
        }
        edit.Vm.Model.Contents = text;
        edit.Vm.Model.Modified = DateTime.Now;
        edit.Doc.NotifyAnnotationChanged();
    }

    private void InlineEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitInlineEdit(); e.Handled = true; }
        else if (e.Key == Key.Escape) { CommitInlineEdit(cancel: true); e.Handled = true; }
    }

    private void InlineEditor_LostFocus(object sender, RoutedEventArgs e) => CommitInlineEdit();

    /// <summary>Double-click (or Enter) on an existing text box reopens the full dialog,
    /// pre-filled, so its text, font, size, style and colors can be edited directly.
    /// The box's position and size on the page are untouched — drag it or its corner
    /// handles with the Select tool to move/resize.</summary>
    private void OnEditFreeTextRequested(DocumentViewModel doc, AnnotationViewModel vm)
    {
        var m = vm.Model;
        if (m.Kind != AnnotationKind.FreeText) return;

        var result = FreeTextDialog.Show(this, "Edit text", "Text on the page:", m.ColorHex,
            initial: m.Contents, initialSize: m.FontSize, initialFontFamily: m.FontFamily,
            initialBold: m.Bold, initialItalic: m.Italic, initialUnderline: m.Underline,
            initialFillColorHex: m.FillColorHex, initialBorderColorHex: m.BorderColorHex);
        if (result is not { } r) return;

        doc.PushUndo();
        m.Contents = r.Text;
        m.FontSize = r.Size ?? m.FontSize;
        m.FontFamily = r.FontFamily;
        m.Bold = r.Bold;
        m.Italic = r.Italic;
        m.Underline = r.Underline;
        m.ColorHex = r.TextColorHex;
        m.FillColorHex = r.FillColorHex;
        m.BorderColorHex = r.BorderColorHex;
        m.Modified = DateTime.Now;
        doc.NotifyAnnotationChanged();
    }

    private void OnPlaceImageRequested(DocumentViewModel doc, int pageIndex, RectD area)
    {
        var dlg = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" };
        if (dlg.ShowDialog(this) != true) { doc.ActiveTool = ToolKind.Select; return; }
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(dlg.FileName);
            bmp.EndInit();
            bmp.Freeze();

            // Fit the image's aspect ratio into the dragged region (or a default size).
            double aspect = bmp.PixelWidth / (double)Math.Max(1, bmp.PixelHeight);
            double w = area.Width, h = area.Height;
            if (w < 12 || h < 12) { w = 200; h = 200 / aspect; }
            else if (w / h > aspect) w = h * aspect;
            else h = w / aspect;

            doc.AddImage(new PendingImage
            {
                PageIndex = pageIndex,
                Path = dlg.FileName,
                Bitmap = bmp,
                Rect = new RectD(area.X, area.Y, w, h),
            });
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, ex.Message, "Graphite", DialogButtons.OK, DialogIcon.Warning);
        }
        doc.ActiveTool = ToolKind.Select;
    }

    // ------------------------------------------------------------- global input

    /// <summary>Paste an image straight from the clipboard onto the current page —
    /// no file picker. It lands as a movable/resizable overlay (Select tool), exactly
    /// like a placed image file, and is baked into the PDF on save.</summary>
    private void PasteImageFromClipboard(DocumentViewModel doc)
    {
        try
        {
            // The clipboard is a shared system resource — another app holding it open makes
            // these calls throw (CLIPBRD_E_CANT_OPEN), so they live inside the try too.
            var bmp = ClipboardHelper.Try(Clipboard.GetImage);
            if (bmp == null) return;

            // Baking placed images into the PDF goes through a file path, so the
            // clipboard bitmap is persisted to a session temp PNG (cleaned up on exit).
            string temp = App.NewPasteTempFile();
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using (var fs = File.Create(temp)) encoder.Save(fs);
            bmp.Freeze();

            // 96 dpi pixels -> points; fit within 80% of the page, centered.
            double wPt = bmp.PixelWidth * 72.0 / 96.0;
            double hPt = bmp.PixelHeight * 72.0 / 96.0;
            var (pw, ph) = doc.Renderer.PageSizes[doc.CurrentPageIndex];
            double fit = Math.Min(1.0, Math.Min(pw * 0.8 / Math.Max(wPt, 1), ph * 0.8 / Math.Max(hPt, 1)));
            wPt *= fit;
            hPt *= fit;

            doc.AddImage(new PendingImage
            {
                PageIndex = doc.CurrentPageIndex,
                Path = temp,
                Bitmap = bmp,
                Rect = new RectD((pw - wPt) / 2, (ph - hPt) / 2, wPt, hPt),
            });
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, ex.Message, "Graphite", DialogButtons.OK, DialogIcon.Warning);
        }
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Clicking anywhere outside the inline text editor commits what was typed.
        if (_inlineEdit is { } edit && !edit.Tb.IsMouseOver)
            CommitInlineEdit();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var doc = ViewModel.SelectedDocument;

        // Command palette swallows its own keys.
        if (ViewModel.IsPaletteOpen)
        {
            if (e.Key == Key.Escape) { ViewModel.ClosePalette(); e.Handled = true; }
            return;
        }

        // Switch tabs: Ctrl+Tab → next, Ctrl+Shift+Tab → previous (wraps around).
        if (e.Key == Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) != 0 &&
            (Keyboard.Modifiers & ModifierKeys.Alt) == 0)
        {
            ViewModel.CycleDocument((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : +1);
            e.Handled = true;
            return;
        }

        // Reading history.
        if (e.Key == Key.System && doc != null)
        {
            if (e.SystemKey == Key.Left) { doc.NavigateBack(); e.Handled = true; return; }
            if (e.SystemKey == Key.Right) { doc.NavigateForward(); e.Handled = true; return; }
        }

        // Fullscreen: F11 toggles, Esc leaves.
        if (e.Key == Key.F11)
        {
            ViewModel.IsFullscreen = !ViewModel.IsFullscreen;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && ViewModel.IsFullscreen)
        {
            ViewModel.IsFullscreen = false;
            e.Handled = true;
            return;
        }

        // Lasso selection: Esc drops it, Delete removes what it holds.
        if (doc is { HasLassoSelection: true } && Keyboard.FocusedElement is not TextBox)
        {
            if (e.Key == Key.Escape) { doc.ClearLasso(); e.Handled = true; return; }
            if (e.Key == Key.Delete) { doc.DeleteLassoSelection(); e.Handled = true; return; }
        }

        // Page stepping: Up/Down (and PageUp/PageDown) go to the previous/next page,
        // Home/End to the first/last — unless the focus is somewhere that wants those keys.
        if (doc != null && Keyboard.Modifiers == ModifierKeys.None && PageKeysAvailable())
        {
            switch (e.Key)
            {
                case Key.Down:
                case Key.PageDown:
                    doc.StepPage(+1);
                    e.Handled = true;
                    return;
                case Key.Up:
                case Key.PageUp:
                    doc.StepPage(-1);
                    e.Handled = true;
                    return;
                case Key.Home:
                    doc.GoToPage(0);
                    e.Handled = true;
                    return;
                case Key.End:
                    doc.GoToPage(doc.Pages.Count - 1);
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control &&
            doc != null && Keyboard.FocusedElement is not TextBox &&
            ClipboardHelper.Try(Clipboard.ContainsImage))
        {
            PasteImageFromClipboard(doc);
            e.Handled = true;
        }
        else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control &&
            Keyboard.FocusedElement is not TextBox && doc != null)
        {
            _ = doc.UndoAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.Y && Keyboard.Modifiers == ModifierKeys.Control &&
                 Keyboard.FocusedElement is not TextBox && doc != null)
        {
            _ = doc.RedoAsync();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            ViewModel.ShowInspector = true;
            if (doc != null)
            {
                if (_searchRadios.TryGetValue(doc, out var radio)) radio.IsChecked = true;
                Dispatcher.BeginInvoke(() =>
                {
                    if (_searchBoxes.TryGetValue(doc, out var box)) box.Focus();
                }, DispatcherPriority.Input);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.F3 && doc != null)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift) doc.PrevMatch();
            else doc.NextMatch();
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control &&
                 doc is { SelectedText.Length: > 0 } &&
                 Keyboard.FocusedElement is not TextBox)
        {
            ClipboardHelper.Try(() => { Clipboard.SetText(doc.SelectedText); return true; });
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && doc?.SelectedAnnotation is { } sel &&
                 Keyboard.FocusedElement is not TextBox)
        {
            doc.RemoveAnnotation(sel);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && doc?.SelectedImage is { } selImg &&
                 Keyboard.FocusedElement is not TextBox)
        {
            doc.RemoveImage(selImg);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && doc?.SelectedAnnotation is { Kind: AnnotationKind.FreeText } selFt &&
                 Keyboard.FocusedElement is not TextBox)
        {
            doc.RequestEditFreeText(selFt);
            e.Handled = true;
        }
    }

    /// <summary>False while the keyboard focus sits in something that uses the arrow keys
    /// itself (text boxes, combo boxes, menus, the outline tree, the search-result list…).</summary>
    private bool PageKeysAvailable()
    {
        if (_inlineEdit != null) return false;
        return Keyboard.FocusedElement switch
        {
            System.Windows.Controls.Primitives.TextBoxBase => false,
            PasswordBox => false,
            ComboBox or ComboBoxItem => false,
            TreeView or TreeViewItem => false,
            MenuItem or System.Windows.Controls.ContextMenu => false,
            System.Windows.Controls.Primitives.RangeBase => false,
            ListBoxItem { DataContext: not PageViewModel } => false,
            _ => true,
        };
    }

    // ------------------------------------------------------------- command palette

    private void PaletteBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (ViewModel.PaletteResults.Count > 0)
                    ViewModel.PaletteSelectedIndex =
                        (ViewModel.PaletteSelectedIndex + 1) % ViewModel.PaletteResults.Count;
                e.Handled = true;
                break;
            case Key.Up:
                if (ViewModel.PaletteResults.Count > 0)
                    ViewModel.PaletteSelectedIndex =
                        (ViewModel.PaletteSelectedIndex - 1 + ViewModel.PaletteResults.Count)
                        % ViewModel.PaletteResults.Count;
                e.Handled = true;
                break;
            case Key.Enter:
                ViewModel.ExecutePaletteSelection();
                e.Handled = true;
                break;
            case Key.Escape:
                ViewModel.ClosePalette();
                e.Handled = true;
                break;
        }
    }

    private void PaletteItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PaletteCommand cmd })
        {
            ViewModel.ClosePalette();
            cmd.Execute();
            e.Handled = true;
        }
    }

    private void PaletteScrim_Click(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource))
            ViewModel.ClosePalette();
    }

    private void PaletteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: { } item } lb)
            lb.ScrollIntoView(item);
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        HideDropOverlay();
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var supported = files.Where(f =>
                Path.GetExtension(f).ToLowerInvariant() is ".pdf" or ".doc" or ".docx" or ".rtf"
                    or ".xls" or ".xlsx" or ".ppt" or ".pptx").ToArray();
            if (supported.Length > 0)
                await ViewModel.OpenFilesAsync(supported);
        }
    }
}
