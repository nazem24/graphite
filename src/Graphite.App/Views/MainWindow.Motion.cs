using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Graphite.App.Controls;
using Graphite.App.Services;
using Graphite.App.ViewModels;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Graphite.App.Views;

/// <summary>
/// Motion for the main window: everything that animates from code rather than from XAML
/// triggers. Each effect checks <see cref="Motion.Enabled"/> so "Reduce motion" (or Windows'
/// own animation setting) switches them all off together.
/// </summary>
public partial class MainWindow
{
    // ------------------------------------------------------------- tree helpers

    private static FrameworkElement? FindByName(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            if (FindByName(child, name) is { } nested) return nested;
        }
        return null;
    }

    private static void FindAllByTag(DependencyObject root, string tag, List<FrameworkElement> into)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Tag: string t } fe && t == tag) into.Add(fe);
            FindAllByTag(child, tag, into);
        }
    }

    private static ListBox? FindListByItemsSource(DependencyObject root, object source)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ListBox lb && ReferenceEquals(lb.ItemsSource, source)) return lb;
            if (FindListByItemsSource(child, source) is { } nested) return nested;
        }
        return null;
    }

    /// <summary>The realized container of a page in the main viewer, if it is on screen.</summary>
    private FrameworkElement? PageContainer(DocumentViewModel doc, int page)
    {
        if (!_viewers.TryGetValue(doc, out var lb) || !ReferenceEquals(lb.DataContext, doc)) return null;
        if (page < 0 || page >= doc.Pages.Count) return null;
        return lb.ItemContainerGenerator.ContainerFromItem(doc.Pages[page]) as FrameworkElement;
    }

    // ------------------------------------------------------------- wiring

    /// <summary>Called once from the constructor.</summary>
    private void InitMotion()
    {
        ViewModel.TabClosing = AnimateTabCloseAsync;
        ViewModel.UpdateAvailable += ShowUpdateToast;

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
        _idleTimer.Tick += (_, _) =>
        {
            _idleTimer.Stop();
            if (ViewModel.IsFullscreen) ShowPageBar(false);
        };

        _dragWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _dragWatch.Tick += (_, _) =>
        {
            if (Environment.TickCount64 - _lastDragSignal > 500) HideDropOverlay();
        };

        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => UpdateToolPill(false));
        SizeChanged += (_, _) => UpdateToolPill(false);
    }

    /// <summary>Per-document hooks (called when a document is added).</summary>
    private void WireMotion(DocumentViewModel doc)
    {
        doc.PageOperationApplied += hint => AnimatePageOperation(doc, hint);
        doc.StateRestored += () => OnStateRestored(doc);
        doc.Saved += () => OnDocSaved(doc);
        doc.PageFlashRequested += page => FlashPage(doc, page);
        doc.OcrPageStarted += page => OcrScanStart(doc, page);
        doc.OcrPageRecognized += page => OcrShimmer(doc, page);
        _lastLayout[doc] = doc.Layout;
    }

    private readonly Dictionary<DocumentViewModel, PageLayout> _lastLayout = new();

    /// <summary>Property changes of the active document that have a motion response.</summary>
    private void MotionOnDocPropertyChanged(DocumentViewModel doc, string? property)
    {
        switch (property)
        {
            case nameof(DocumentViewModel.Layout):
                var previous = _lastLayout.TryGetValue(doc, out var l) ? l : doc.Layout;
                _lastLayout[doc] = doc.Layout;
                AnimateLayoutSwitch(doc, previous);
                break;
            case nameof(DocumentViewModel.InvertPages):
                FadeImage.BeginCrossFade();
                break;
            case nameof(DocumentViewModel.IsBusy) when !doc.IsBusy:
                OcrScanStop(doc);
                break;
        }
    }

    // ------------------------------------------------------------- side panels

    private const double SidebarWidth = 208, InspectorWidth = 292;

    private bool PanelShouldShow(FrameworkElement panel) =>
        panel.Tag as string == "SidebarPanel" ? ViewModel.ShowSidebar : ViewModel.ShowInspector;

    /// <summary>A freshly created panel takes the current state without animating.</summary>
    private void Panel_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border panel) ApplyPanel(panel, PanelShouldShow(panel), animate: false);
    }

    private void AnimatePanels(bool sidebar)
    {
        var panels = new List<FrameworkElement>();
        FindAllByTag(DocHost, sidebar ? "SidebarPanel" : "InspectorPanel", panels);
        foreach (var panel in panels.OfType<Border>())
            ApplyPanel(panel, PanelShouldShow(panel), animate: true);
    }

    /// <summary>Open / close a side panel: width, margin and opacity animate together while the
    /// content inside keeps a fixed width, so it slides instead of reflowing.</summary>
    private void ApplyPanel(Border panel, bool show, bool animate)
    {
        bool left = panel.Tag as string == "SidebarPanel";
        double fullWidth = left ? SidebarWidth : InspectorWidth;
        var openMargin = left ? new Thickness(12, 4, 0, 12) : new Thickness(0, 4, 12, 12);
        var closedMargin = new Thickness(0, 4, 0, 12);

        panel.BeginAnimation(FrameworkElement.WidthProperty, null);
        panel.BeginAnimation(FrameworkElement.MarginProperty, null);
        panel.BeginAnimation(OpacityProperty, null);

        void Settle()
        {
            panel.Width = show ? fullWidth : 0;
            panel.Margin = show ? openMargin : closedMargin;
            panel.Opacity = show ? 1 : 0;
            panel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!animate || !Motion.Enabled)
        {
            Settle();
            return;
        }

        panel.Visibility = Visibility.Visible;
        var span = Motion.Ms(show ? 260 : 220);
        var ease = Motion.EaseInOut;

        var width = new DoubleAnimation(show ? fullWidth : 0, span) { EasingFunction = ease };
        width.Completed += (_, _) =>
        {
            // A later toggle may have reversed the direction while this one was running.
            if (PanelShouldShow(panel) != show) return;
            panel.BeginAnimation(FrameworkElement.WidthProperty, null);
            panel.BeginAnimation(FrameworkElement.MarginProperty, null);
            panel.BeginAnimation(OpacityProperty, null);
            Settle();
        };
        panel.BeginAnimation(FrameworkElement.WidthProperty, width);
        panel.BeginAnimation(FrameworkElement.MarginProperty,
            new ThicknessAnimation(show ? openMargin : closedMargin, span) { EasingFunction = ease });
        panel.BeginAnimation(OpacityProperty,
            new DoubleAnimation(show ? 1 : 0, span) { EasingFunction = ease });
    }

    // ------------------------------------------------------------- page operations

    private void AnimatePageOperation(DocumentViewModel doc, PageOpHint hint)
    {
        if (!Motion.Enabled) return;
        // The page list was just rebuilt — wait for its containers to exist.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (FindListByItemsSource(DocHost, doc.Pages) is not { } list) return;

            FrameworkElement? Item(int i) =>
                i >= 0 && i < doc.Pages.Count &&
                list.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement c
                    ? (FindDescendant<StackPanel>(c) ?? c)
                    : null;

            double pitch = 160;
            if (Item(Math.Clamp(hint.Index, 0, Math.Max(0, doc.Pages.Count - 1))) is { ActualHeight: > 8 } sample)
                pitch = sample.ActualHeight + 6;

            switch (hint.Kind)
            {
                case PageOpKind.Rotate:
                    if (Item(hint.Index) is { } rotated &&
                        FindDescendant<Border>(rotated) is { } frame &&
                        Motion.RigOf(frame) is { } rig)
                    {
                        Motion.Tween(rig.Rotate, RotateTransform.AngleProperty, hint.Extra >= 0 ? -90 : 90, 0, 380);
                        Motion.Tween(frame, OpacityProperty, 0.4, 1, 260);
                    }
                    break;

                case PageOpKind.Delete:
                    for (int i = hint.Index, n = 0; n < 9; i++, n++)
                        if (Item(i) is { } below && Motion.RigOf(below) is { } slide)
                            Motion.Tween(slide.Move, TranslateTransform.YProperty,
                                pitch * Math.Min(hint.Count, 3), 0, 280, delayMs: n * 22);
                    break;

                case PageOpKind.Insert:
                    for (int i = hint.Index, n = 0; n < Math.Min(hint.Count, 4); i++, n++)
                        if (Item(i) is { } added && Motion.RigOf(added) is { } grow)
                        {
                            Motion.Tween(grow.Scale, ScaleTransform.ScaleXProperty, 0.6, 1, 300, n * 40, Motion.Soft);
                            Motion.Tween(grow.Scale, ScaleTransform.ScaleYProperty, 0.6, 1, 300, n * 40, Motion.Soft);
                            Motion.Tween(added, OpacityProperty, 0, 1, 220, n * 40);
                        }
                    for (int i = hint.Index + hint.Count, n = 0; n < 8; i++, n++)
                        if (Item(i) is { } after && Motion.RigOf(after) is { } shift)
                            Motion.Tween(shift.Move, TranslateTransform.YProperty,
                                -pitch * Math.Min(hint.Count, 3), 0, 280, delayMs: n * 22);
                    break;

                case PageOpKind.Move:
                    int from = hint.Index, to = hint.Extra;
                    if (Item(to) is { } moved && Motion.RigOf(moved) is { } m1)
                        Motion.Tween(m1.Move, TranslateTransform.YProperty, (from - to) * pitch, 0, 300);
                    if (Item(from) is { } swapped && Motion.RigOf(swapped) is { } m2)
                        Motion.Tween(m2.Move, TranslateTransform.YProperty, (to - from) * pitch, 0, 300);
                    break;
            }

            // The main viewer re-reads the document too: a soft fade hides the swap.
            if (_viewers.TryGetValue(doc, out var viewer) && ReferenceEquals(viewer.DataContext, doc))
                Motion.Tween(viewer, OpacityProperty, 0.55, 1, 260);
        });
    }

    // ------------------------------------------------------------- layout switch, undo / redo

    private void AnimateLayoutSwitch(DocumentViewModel doc, PageLayout previous)
    {
        if (!Motion.Enabled || previous == doc.Layout) return;
        if (!_viewers.TryGetValue(doc, out var lb) || !ReferenceEquals(lb.DataContext, doc)) return;

        // Same transform the page-turn animation uses, so the two never fight.
        if (lb.RenderTransform is not TranslateTransform tt)
            lb.RenderTransform = tt = new TranslateTransform();
        double direction = (int)doc.Layout >= (int)previous ? 1 : -1;
        Motion.Tween(tt, TranslateTransform.XProperty, direction * 36, 0, 320);
        Motion.Tween(lb, OpacityProperty, 0.2, 1, 280);
    }

    private void OnStateRestored(DocumentViewModel doc)
    {
        if (!Motion.Enabled) return;
        if (_viewers.TryGetValue(doc, out var lb) && ReferenceEquals(lb.DataContext, doc))
            Motion.Tween(lb, OpacityProperty, 0.55, 1, 260);
    }

    // ------------------------------------------------------------- jump flash

    /// <summary>Briefly tint the page you just landed on (outline, Go to page, history, markup cards).</summary>
    private void FlashPage(DocumentViewModel doc, int page)
    {
        if (!Motion.Enabled) return;
        // Give the scroll glide a moment to bring the page into view.
        Motion.After(260, () => PlayFlash(doc, page, retry: true));
    }

    private void PlayFlash(DocumentViewModel doc, int page, bool retry)
    {
        if (ViewModel.SelectedDocument != doc) return;
        if (PageContainer(doc, page) is { } container && FindByName(container, "FlashRing") is { } ring)
        {
            Motion.Keys(ring, OpacityProperty, (0, 0), (110, 0.95), (700, 0));
            return;
        }
        if (retry) Motion.After(220, () => PlayFlash(doc, page, retry: false));
    }

    // ------------------------------------------------------------- save checkmark, chevrons

    private void OnDocSaved(DocumentViewModel doc)
    {
        if (!ReferenceEquals(ViewModel.SelectedDocument, doc)) return;
        if (TryFindResource("Icon.Check") is Geometry check)
            IconMotion.Flash(FileIcon, check);
    }

    /// <summary>The small chevron on a dropdown button turns over while its menu is open.</summary>
    private static void FlipChevron(DependencyObject button, ContextMenu menu)
    {
        if (!Motion.Enabled) return;
        System.Windows.Shapes.Path? chevron = null;
        FindChevron(button, ref chevron);
        if (chevron == null) return;

        if (chevron.RenderTransform is not RotateTransform rotate)
        {
            rotate = new RotateTransform(0);
            chevron.RenderTransformOrigin = new Point(0.5, 0.5);
            chevron.RenderTransform = rotate;
        }
        Motion.Tween(rotate, RotateTransform.AngleProperty, rotate.Angle, 180, 200);

        RoutedEventHandler? closed = null;
        closed = (_, _) =>
        {
            menu.Closed -= closed;
            Motion.Tween(rotate, RotateTransform.AngleProperty, rotate.Angle, 0, 200);
        };
        menu.Closed += closed;
    }

    private static void FindChevron(DependencyObject root, ref System.Windows.Shapes.Path? found)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count && found == null; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Shapes.Path p && ReferenceEquals(p.Data, TryChevron()))
                found = p;
            else
                FindChevron(child, ref found);
        }
    }

    private static Geometry? TryChevron() =>
        Application.Current.TryFindResource("Icon.ChevronDown") as Geometry;

    // ------------------------------------------------------------- tabs

    /// <summary>The closing tab shrinks and fades before it is removed.</summary>
    private Task AnimateTabCloseAsync(DocumentViewModel doc)
    {
        if (!Motion.Enabled ||
            TabStrip.ItemContainerGenerator.ContainerFromItem(doc) is not FrameworkElement tab ||
            !tab.IsLoaded)
            return Task.CompletedTask;

        var done = new TaskCompletionSource();
        var scale = new ScaleTransform(1, 1);
        tab.LayoutTransform = scale;

        var shrink = new DoubleAnimation(1, 0.02, Motion.Ms(180)) { EasingFunction = Motion.EaseIn };
        shrink.Completed += (_, _) => done.TrySetResult();
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        tab.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, Motion.Ms(160)));
        return done.Task;
    }

    // ------------------------------------------------------------- markup cards

    /// <summary>Deleting a card from the Markup panel: it slides out and collapses first.</summary>
    private void DeleteCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AnnotationViewModel vm } button) return;

        Border? card = null;
        for (var d = VisualTreeHelper.GetParent(button); d != null; d = VisualTreeHelper.GetParent(d))
            if (d is Border { Tag: int } b) { card = b; break; }

        if (card == null || !Motion.Enabled)
        {
            vm.DeleteCommand.Execute(null);
            return;
        }

        card.IsHitTestVisible = false;
        var collapse = new ScaleTransform(1, 1);
        card.LayoutTransform = collapse;
        var shrink = new DoubleAnimation(1, 0.02, Motion.Ms(190)) { EasingFunction = Motion.EaseIn };
        shrink.Completed += (_, _) => vm.DeleteCommand.Execute(null);
        collapse.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, Motion.Ms(150)));
        if (card.RenderTransform is TranslateTransform slide)
            slide.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, 28, Motion.Ms(170)) { EasingFunction = Motion.EaseIn });
        e.Handled = true;
    }

    // ------------------------------------------------------------- command palette

    private void StaggerPalette()
    {
        if (!Motion.Enabled) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            for (int i = 0; i < PaletteList.Items.Count && i < 12; i++)
            {
                if (PaletteList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement row) continue;
                double delay = 50 + i * 22;
                Motion.Tween(row, OpacityProperty, 0, 1, 180, delay);
                if (Motion.RigOf(row) is { } rig)
                    Motion.Tween(rig.Move, TranslateTransform.YProperty, 8, 0, 220, delay);
            }
        });
    }

    // ------------------------------------------------------------- tool selection pill

    private bool _pillPending;

    private void Tools_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (_pillPending) return;
        _pillPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            _pillPending = false;
            UpdateToolPill(true);
        });
    }

    /// <summary>Glide the shared highlight to the active tool (or fade it out when none is).</summary>
    private void UpdateToolPill(bool animate)
    {
        if (ToolPill == null || ToolsPanel == null || ToolPill.Parent is not UIElement host) return;
        var move = (TranslateTransform)ToolPill.RenderTransform;

        var active = ToolsPanel.Children.OfType<RadioButton>()
            .FirstOrDefault(r => r.GroupName == "Tools" && r.IsChecked == true);
        if (active == null || !active.IsLoaded || active.ActualWidth <= 0)
        {
            if (ToolPill.Opacity > 0)
            {
                if (animate && Motion.Enabled) Motion.Tween(ToolPill, OpacityProperty, ToolPill.Opacity, 0, 160);
                else { ToolPill.BeginAnimation(OpacityProperty, null); ToolPill.Opacity = 0; }
            }
            return;
        }

        var origin = active.TransformToAncestor(host).Transform(new Point(0, 0));
        ToolPill.Width = active.ActualWidth;
        ToolPill.Height = active.ActualHeight;
        move.BeginAnimation(TranslateTransform.YProperty, null);
        move.Y = origin.Y;

        bool visible = ToolPill.Opacity > 0.05;
        if (animate && visible && Motion.Enabled)
        {
            Motion.Tween(move, TranslateTransform.XProperty, move.X, origin.X, 300, ease: Motion.Soft);
        }
        else
        {
            move.BeginAnimation(TranslateTransform.XProperty, null);
            move.X = origin.X;
        }

        if (!visible || ToolPill.Opacity < 1)
        {
            if (animate && Motion.Enabled) Motion.Tween(ToolPill, OpacityProperty, ToolPill.Opacity, 1, 180);
            else { ToolPill.BeginAnimation(OpacityProperty, null); ToolPill.Opacity = 1; }
        }
    }

    // ------------------------------------------------------------- fullscreen chrome

    private DispatcherTimer _idleTimer = null!;
    private bool _barShown = true;
    private bool _pageBarShown = true;

    /// <summary>In fullscreen the command bar floats over the page and slides in from the top
    /// edge; the page bar slides away when the mouse is idle.</summary>
    private void ApplyFullscreenChrome(bool on)
    {
        if (on)
        {
            // Below WindowChrome's 40 px caption strip, which would otherwise swallow clicks.
            Grid.SetRow(CommandBar, 2);
            CommandBar.VerticalAlignment = VerticalAlignment.Top;
            CommandBar.Margin = new Thickness(12, 44, 12, 0);
            Panel.SetZIndex(CommandBar, 20);
            _barShown = true;
            SetCommandBar(false, animate: false);
            _idleTimer.Stop();
            _idleTimer.Start();
        }
        else
        {
            _idleTimer.Stop();
            Grid.SetRow(CommandBar, 1);
            CommandBar.VerticalAlignment = VerticalAlignment.Stretch;
            CommandBar.Margin = new Thickness(12, 0, 12, 6);
            Panel.SetZIndex(CommandBar, 0);
            if (CommandBar.RenderTransform is TranslateTransform tt)
            {
                tt.BeginAnimation(TranslateTransform.YProperty, null);
                tt.Y = 0;
            }
            _barShown = true;
            ShowPageBar(true, animate: false);
        }
    }

    private void SetCommandBar(bool show, bool animate)
    {
        if (CommandBar.RenderTransform is not TranslateTransform tt || show == _barShown) return;
        _barShown = show;
        double hidden = -(Math.Max(CommandBar.ActualHeight, 56) + 90);
        double to = show ? 0 : hidden;
        if (animate && Motion.Enabled)
        {
            Motion.Tween(tt, TranslateTransform.YProperty, tt.Y, to, 260);
        }
        else
        {
            tt.BeginAnimation(TranslateTransform.YProperty, null);
            tt.Y = to;
        }
    }

    private void ShowPageBar(bool show, bool animate = true, bool force = false)
    {
        if (show == _pageBarShown && !force) return;
        // Every open document has its own page bar; only the visible tab's is ours to move.
        var found = new List<FrameworkElement>();
        if (ViewModel.SelectedDocument is { } active &&
            DocHost.ItemContainerGenerator.ContainerFromItem(active) is DependencyObject host)
            FindAllByTag(host, "PageBar", found);
        if (found.Count == 0 || Motion.RigOf(found[0]) is not { } rig) return;
        var bar = found[0];

        _pageBarShown = show;
        bar.IsHitTestVisible = show;
        if (animate && Motion.Enabled)
        {
            Motion.Tween(rig.Move, TranslateTransform.YProperty, rig.Move.Y, show ? 0 : 90, 260);
            Motion.Tween(bar, OpacityProperty, bar.Opacity, show ? 1 : 0, 220);
        }
        else
        {
            rig.Move.BeginAnimation(TranslateTransform.YProperty, null);
            bar.BeginAnimation(OpacityProperty, null);
            rig.Move.Y = show ? 0 : 90;
            bar.Opacity = show ? 1 : 0;
        }
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!ViewModel.IsFullscreen) return;
        var pos = e.GetPosition(this);

        if (!_barShown && pos.Y <= 6)
            SetCommandBar(true, animate: true);
        else if (_barShown && pos.Y > 44 + Math.Max(CommandBar.ActualHeight, 56) + 28)
            SetCommandBar(false, animate: true);

        ShowPageBar(true);
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    // ------------------------------------------------------------- update toast

    private Func<Task>? _toastAction;
    private DispatcherTimer? _toastTimer;

    private void ShowUpdateToast(string version, Func<Task> install)
    {
        ToastTitle.Text = $"Graphite {version} is available";
        ToastText.Text = $"You're running {ViewModel.AppVersion}. Updating restarts Graphite.";
        ToastAction.Content = "Update now";
        _toastAction = install;

        Toast.Visibility = Visibility.Visible;
        if (Motion.Enabled && Toast.RenderTransform is TranslateTransform tt)
        {
            Motion.Tween(tt, TranslateTransform.YProperty, 26, 0, 340, ease: Motion.Soft);
            Motion.Tween(Toast, OpacityProperty, 0, 1, 240);
        }
        else
        {
            Toast.BeginAnimation(OpacityProperty, null);
            Toast.Opacity = 1;
        }

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(16) };
        _toastTimer.Tick += (_, _) => HideToast();
        _toastTimer.Start();
    }

    private void HideToast()
    {
        _toastTimer?.Stop();
        if (Toast.Visibility != Visibility.Visible) return;
        if (Motion.Enabled && Toast.RenderTransform is TranslateTransform tt)
        {
            Motion.Tween(tt, TranslateTransform.YProperty, 0, 18, 200, ease: Motion.EaseIn);
            Motion.Tween(Toast, OpacityProperty, 1, 0, 200);
            Motion.After(220, () => Toast.Visibility = Visibility.Collapsed);
        }
        else
        {
            Toast.Visibility = Visibility.Collapsed;
        }
    }

    private void ToastDismiss_Click(object sender, RoutedEventArgs e) => HideToast();

    private async void ToastAction_Click(object sender, RoutedEventArgs e)
    {
        var action = _toastAction;
        HideToast();
        if (action == null) return;
        try { await action(); }
        catch (Exception ex) { App.LogError("Update from toast failed", ex); }
    }

    // ------------------------------------------------------------- drop target

    private DispatcherTimer _dragWatch = null!;
    private long _lastDragSignal;
    private bool _dropShown;

    private void Window_DragEnter(object sender, DragEventArgs e) => NoteDrag(e);
    private void Window_DragOver(object sender, DragEventArgs e) => NoteDrag(e);

    /// <summary>Show the "Drop to open" frame while a file hovers over the window. DragLeave
    /// also fires when moving between child elements, so a short watchdog hides it instead.</summary>
    private void NoteDrag(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        _lastDragSignal = Environment.TickCount64;
        if (_dropShown) return;
        _dropShown = true;
        DropOverlay.Visibility = Visibility.Visible;
        if (Motion.Enabled)
            DropOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0.55, 1, Motion.Ms(650))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = Motion.Sine,
            });
        _dragWatch.Start();
    }

    private void HideDropOverlay()
    {
        _dragWatch.Stop();
        if (!_dropShown) return;
        _dropShown = false;
        DropOverlay.BeginAnimation(OpacityProperty, null);
        DropOverlay.Opacity = 1;
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------- OCR

    private readonly Dictionary<DocumentViewModel, int> _ocrScanning = new();

    /// <summary>A scan line sweeps down the page that is being read.</summary>
    private void OcrScanStart(DocumentViewModel doc, int page)
    {
        OcrScanStop(doc);
        _ocrScanning[doc] = page;
        if (!Motion.Enabled) return;
        if (OcrParts(doc, page) is not var (fx, band)) return;

        fx.Visibility = Visibility.Visible;
        var move = BandTransform(band);
        move.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(-band.Height, Math.Max(fx.ActualHeight, 160), Motion.Ms(1500))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = Motion.Sine,
            });
    }

    private void OcrScanStop(DocumentViewModel doc)
    {
        if (!_ocrScanning.Remove(doc, out int page)) return;
        if (OcrParts(doc, page) is not var (fx, band)) return;
        BandTransform(band).BeginAnimation(TranslateTransform.YProperty, null);
        fx.Visibility = Visibility.Collapsed;
    }

    /// <summary>One quick shimmer when a page's text has been recognized.</summary>
    private void OcrShimmer(DocumentViewModel doc, int page)
    {
        if (_ocrScanning.TryGetValue(doc, out int scanning) && scanning == page)
            _ocrScanning.Remove(doc);
        if (!Motion.Enabled) return;
        if (OcrParts(doc, page) is not var (fx, band)) return;

        fx.Visibility = Visibility.Visible;
        var sweep = new DoubleAnimation(-band.Height, Math.Max(fx.ActualHeight, 160), Motion.Ms(650))
        {
            EasingFunction = Motion.EaseOut,
        };
        sweep.Completed += (_, _) =>
        {
            if (!(_ocrScanning.TryGetValue(doc, out int p) && p == page))
                fx.Visibility = Visibility.Collapsed;
        };
        BandTransform(band).BeginAnimation(TranslateTransform.YProperty, sweep);
    }

    private (Grid Fx, Rectangle Band)? OcrParts(DocumentViewModel doc, int page)
    {
        if (PageContainer(doc, page) is { } c &&
            FindByName(c, "OcrFx") is Grid fx && FindByName(c, "OcrBand") is Rectangle band)
            return (fx, band);
        return null;
    }

    private static TranslateTransform BandTransform(Rectangle band)
    {
        if (band.RenderTransform is TranslateTransform tt) return tt;
        tt = new TranslateTransform();
        band.RenderTransform = tt;
        return tt;
    }
}
