using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    private TextBox? _searchBox;
    private RadioButton? _searchRadio;
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
        Closing += MainWindow_Closing;
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        ViewModel.PasteImageRequested += () =>
        {
            if (ViewModel.SelectedDocument is { } d && Clipboard.ContainsImage())
                PasteImageFromClipboard(d);
        };

        StateChanged += (_, _) => UpdateMaximizeRestoreIcon();
        Loaded += (_, _) => UpdateMaximizeRestoreIcon();
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

    // ------------------------------------------------------------- fullscreen

    private WindowState _preFullscreenState = WindowState.Normal;
    private bool _preFullscreenSidebar, _preFullscreenInspector;

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsFullscreen))
            ApplyFullscreen(ViewModel.IsFullscreen);
        else if (e.PropertyName == nameof(MainViewModel.IsPaletteOpen) && ViewModel.IsPaletteOpen)
            Dispatcher.BeginInvoke(() =>
            {
                PaletteBox.Focus();
                PaletteBox.SelectAll();
            }, DispatcherPriority.Input);
    }

    private void ApplyFullscreen(bool on)
    {
        if (on)
        {
            _preFullscreenState = WindowState;
            _preFullscreenSidebar = ViewModel.ShowSidebar;
            _preFullscreenInspector = ViewModel.ShowInspector;
            ViewModel.ShowSidebar = false;
            ViewModel.ShowInspector = false;
            // WindowStyle stays None (custom chrome) — fullscreen just drops the resize
            // border and covers the taskbar.
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal; // force a state change so the taskbar is covered
            WindowState = WindowState.Maximized;
        }
        else
        {
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
            $"{dirty.Count} document(s) have unsaved changes. Close anyway?",
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
                _wired.Remove(doc);
                _viewers.Remove(doc);
                _scrollOffsets.Remove(doc);
                doc.PropertyChanged -= Doc_PropertyChanged;
            }

        if (e.NewItems == null) return;
        foreach (DocumentViewModel doc in e.NewItems)
        {
            if (!_wired.Add(doc)) continue;
            doc.ScrollToPageRequested += i => ScrollToPage(doc, i);
            doc.ZoomChangedEvent += () => { _zoomTimer.Stop(); _zoomTimer.Start(); };
            doc.PagesReset += () => Dispatcher.BeginInvoke(() => ScrollToPage(doc, doc.CurrentPageIndex));
            doc.EditTextRequested += (page, rect) => OnEditTextRequested(doc, page, rect);
            doc.PlaceImageRequested += (page, rect) => OnPlaceImageRequested(doc, page, rect);
            doc.InlineEditRequested += vm => OnInlineEditRequested(doc, vm);
            doc.EditFreeTextRequested += vm => OnEditFreeTextRequested(doc, vm);
            doc.PropertyChanged += Doc_PropertyChanged;
        }
    }

    private void Doc_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Picking the Signature tool with no saved signature opens the drawing pad first.
        if (e.PropertyName == nameof(DocumentViewModel.ActiveTool) &&
            sender is DocumentViewModel { ActiveTool: ToolKind.Signature } doc &&
            !SignatureDialog.EnsureSignature(this))
            doc.ActiveTool = ToolKind.Select;
    }

    // ------------------------------------------------------------- viewer plumbing

    private void PagesHost_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListBox lb && lb.DataContext is DocumentViewModel doc)
            _viewers[doc] = lb;
    }

    private void PagesHost_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // The TabControl reuses one ContentPresenter across tabs, so re-register on switch.
        if (sender is not ListBox lb) return;

        // Remember where the outgoing document was scrolled to — switching tabs resets
        // the shared ScrollViewer to 0, and without this the document reopens at the
        // wrong spot (the "tab switch loses my place" bug).
        if (e.OldValue is DocumentViewModel oldDoc && FindScrollViewer(lb) is { } oldSv)
            _scrollOffsets[oldDoc] = oldSv.VerticalOffset;

        if (e.NewValue is DocumentViewModel doc)
        {
            _viewers[doc] = lb;
            double target = _scrollOffsets.TryGetValue(doc, out var saved)
                ? saved
                : OffsetOfPage(doc, doc.CurrentPageIndex);
            RestoreScrollWhenReady(lb, doc, target, 0);
        }
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

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    private const double PageGap = 12; // template margin 6 top + 6 bottom

    private static double OffsetOfPage(DocumentViewModel doc, int pageIndex)
    {
        double offset = 0;
        for (int i = 0; i < pageIndex && i < doc.Pages.Count; i++)
            offset += doc.Pages[i].DisplayHeight + PageGap;
        return offset;
    }

    private void ScrollToPage(DocumentViewModel doc, int pageIndex)
    {
        if (!doc.IsContinuous) { return; } // VisiblePages swap handles it
        if (!_viewers.TryGetValue(doc, out var lb)) return;
        RestoreScrollWhenReady(lb, doc, OffsetOfPage(doc, pageIndex), 0);
    }

    /// <summary>Scroll to <paramref name="offset"/> once the ScrollViewer's extent can
    /// actually hold it. Right after a tab switch (or a pages reset) the extent is still
    /// near zero because containers realize lazily — a premature ScrollToVerticalOffset
    /// gets clamped to ~0 and the position is lost. Realize the target page first, then
    /// retry on background priority until the extent is built (or we give up).</summary>
    private void RestoreScrollWhenReady(ListBox lb, DocumentViewModel doc, double offset, int attempt)
    {
        if (!doc.IsContinuous) return;
        if (!_viewers.TryGetValue(doc, out var current) || !ReferenceEquals(current, lb))
            return; // user switched tabs again — abandon
        var sv = FindScrollViewer(lb);
        if (sv == null) return;

        if (attempt == 0)
        {
            if (_scrollers.TryGetValue(lb, out var sc)) sc.Stop();
            // Force realization up to the target page so the extent becomes real.
            int page = PageIndexAtOffset(doc, offset);
            if (page >= 0 && page < lb.Items.Count) lb.ScrollIntoView(lb.Items[page]);
        }

        double max = Math.Max(0, sv.ExtentHeight - sv.ViewportHeight);
        if (max >= offset - 1 || attempt >= 30)
        {
            sv.ScrollToVerticalOffset(Math.Min(offset, max));
            return;
        }
        Dispatcher.BeginInvoke(() => RestoreScrollWhenReady(lb, doc, offset, attempt + 1),
            DispatcherPriority.Background);
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

        // User scrolled outside the animation (scrollbar drag, keyboard) — re-sync.
        if (_scrollers.TryGetValue(lb, out var sc)) sc.Sync();

        // Which page sits a third of the way down the viewport?
        double probe = e.VerticalOffset + e.ViewportHeight / 3;
        double y = 0;
        for (int i = 0; i < doc.Pages.Count; i++)
        {
            y += doc.Pages[i].DisplayHeight + PageGap;
            if (y >= probe)
            {
                if (doc.CurrentPageIndex != i)
                {
                    _syncingThumbs = true;
                    doc.CurrentPageIndex = i;
                    _syncingThumbs = false;
                }
                break;
            }
        }
    }

    private void PagesHost_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (ViewModel.SelectedDocument is not { } doc) return;

        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (sender is ListBox zoomLb && _scrollers.TryGetValue(zoomLb, out var zoomSc)) zoomSc.Stop();
            doc.Zoom = Math.Clamp(doc.Zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1), 0.25, 6);
            e.Handled = true;
            return;
        }

        // Eased wheel scrolling: animate toward a target offset instead of jumping
        // line-by-line (works for both notched wheels and precision touchpads).
        if (sender is ListBox lb && FindScrollViewer(lb) is { } sv)
        {
            if (!_scrollers.TryGetValue(lb, out var scroller))
                _scrollers[lb] = scroller = new SmoothScroller(sv);
            scroller.ScrollBy(-e.Delta * 1.0);
            e.Handled = true;
        }
    }

    /// <summary>Exponential-decay scroll animation toward a target offset, driven by
    /// CompositionTarget.Rendering so it tracks the monitor's refresh rate.</summary>
    private sealed class SmoothScroller
    {
        private readonly ScrollViewer _sv;
        private double _target;
        private bool _animating;

        public SmoothScroller(ScrollViewer sv)
        {
            _sv = sv;
            _target = sv.VerticalOffset;
        }

        public void ScrollBy(double delta)
        {
            _target = Math.Clamp(_target + delta, 0, Math.Max(0, _sv.ExtentHeight - _sv.ViewportHeight));
            if (_animating) return;
            _animating = true;
            CompositionTarget.Rendering += Step;
        }

        /// <summary>Adopt the current offset as the target (after external scrolls).</summary>
        public void Sync()
        {
            if (!_animating) _target = _sv.VerticalOffset;
        }

        /// <summary>Stop animating immediately (programmatic scrolls take over).</summary>
        public void Stop()
        {
            if (!_animating) return;
            CompositionTarget.Rendering -= Step;
            _animating = false;
            _target = _sv.VerticalOffset;
        }

        private void Step(object? sender, EventArgs e)
        {
            double current = _sv.VerticalOffset;
            double next = current + (_target - current) * 0.28;
            if (Math.Abs(next - _target) < 0.6)
            {
                CompositionTarget.Rendering -= Step;
                _animating = false;
                _sv.ScrollToVerticalOffset(_target);
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
        if (_syncingThumbs) return;
        if (sender is ListBox { IsMouseOver: true, SelectedIndex: >= 0 } lb &&
            lb.DataContext is DocumentViewModel doc)
            doc.GoToPage(lb.SelectedIndex);
    }

    private void Outline_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is OutlineNode { PageIndex: { } page } &&
            ViewModel.SelectedDocument is { } doc)
            doc.GoToPage(page);
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

    // ------------------------------------------------------------- menus

    private void OrganizeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } fe)
        {
            menu.DataContext = ViewModel;
            menu.PlacementTarget = fe;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    /// <summary>The Highlight tool button is one control: a plain click selects it as
    /// usual, but if it's already the active tool, this click instead opens the
    /// text-select / freehand mode menu (right-click also opens it, via ContextMenu).
    /// The menu is opened on a deferred dispatch so the mouse-up that follows this
    /// mouse-down doesn't land on the popup and immediately dismiss it.</summary>
    private void HighlightTool_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ViewModel.SelectedDocument?.ActiveTool != ToolKind.Highlight) return;
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

    private void SearchBox_Loaded(object sender, RoutedEventArgs e) => _searchBox = sender as TextBox;
    private void SearchRadio_Loaded(object sender, RoutedEventArgs e) => _searchRadio = sender as RadioButton;

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
        if (sender is TextBox tb && tb.DataContext is PageViewModel page)
            _inlineEditors[page] = tb;
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
        var bmp = Clipboard.GetImage();
        if (bmp == null) return;
        try
        {
            // Baking placed images into the PDF goes through a file path, so the
            // clipboard bitmap is persisted to a session temp PNG.
            string temp = Path.Combine(Path.GetTempPath(), $"graphite-paste-{Guid.NewGuid():N}.png");
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

        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control &&
            doc != null && Clipboard.ContainsImage() &&
            Keyboard.FocusedElement is not TextBox)
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
            if (_searchRadio != null) _searchRadio.IsChecked = true;
            Dispatcher.BeginInvoke(() => _searchBox?.Focus(), DispatcherPriority.Input);
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
            Clipboard.SetText(doc.SelectedText);
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
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var supported = files.Where(f =>
                Path.GetExtension(f).ToLowerInvariant() is ".pdf" or ".doc" or ".docx" or ".rtf"
                    or ".xls" or ".xlsx" or ".ppt" or ".pptx").ToArray();
            if (supported.Length > 0)
                await ViewModel.OpenFilesAsync(supported);
        }
    }
}
