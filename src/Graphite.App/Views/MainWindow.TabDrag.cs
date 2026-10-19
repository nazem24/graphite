using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Graphite.App.Services;
using Graphite.App.ViewModels;

namespace Graphite.App.Views;

// Tab strip behaviour beyond selecting: dragging a tab to a new place, and letting go of a hidden
// tab's page bitmaps only after a while.
public partial class MainWindow
{
    // ------------------------------------------------------------- drag to reorder

    // The Home tab is not part of the strip (it is a separate button before it), so it can neither be
    // dragged nor have a tab dropped in front of it.
    private object? _dragTab;
    private Point _dragStart;
    private bool _dragActive, _dragSettling;
    private double _dragGrab;
    private int _dragFrom, _dragTo;
    private readonly List<(object Tab, FrameworkElement El, double Left, double Width)> _dragSlots = new();

    /// <summary>Left button went down on a tab: remember it, so moving the mouse far enough starts a drag.</summary>
    private void TabDragPress(object tab, MouseButtonEventArgs e)
    {
        if (_dragSettling) return;
        _dragTab = tab;
        _dragStart = e.GetPosition(TabStrip);
        _dragActive = false;
    }

    private void TabStrip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab == null || _dragSettling) return;
        if (e.LeftButton != MouseButtonState.Pressed) { CancelTabDrag(); return; }

        double x = e.GetPosition(TabStrip).X;
        if (!_dragActive)
        {
            if (Math.Abs(x - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance * 2) return;
            if (!BeginTabDrag()) { _dragTab = null; return; }
        }
        UpdateTabDrag(x);
        e.Handled = true;
    }

    private void TabStrip_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragActive) { _dragTab = null; return; }
        FinishTabDrag();
        e.Handled = true;
    }

    private void TabStrip_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragActive && !_dragSettling) CancelTabDrag();
    }

    private bool BeginTabDrag()
    {
        if (ViewModel.Tabs.Count < 2) return false;
        _dragSlots.Clear();
        foreach (var tab in ViewModel.Tabs)
        {
            if (TabStrip.ItemContainerGenerator.ContainerFromItem(tab) is not FrameworkElement el || !el.IsLoaded)
            {
                _dragSlots.Clear();
                return false;
            }
            _dragSlots.Add((tab, el, el.TranslatePoint(new Point(0, 0), TabStrip).X, el.ActualWidth));
        }

        _dragFrom = _dragSlots.FindIndex(s => ReferenceEquals(s.Tab, _dragTab));
        if (_dragFrom < 0) { _dragSlots.Clear(); return false; }
        _dragTo = _dragFrom;
        _dragGrab = _dragStart.X - _dragSlots[_dragFrom].Left;

        foreach (var s in _dragSlots) s.El.RenderTransform = new TranslateTransform();
        Panel.SetZIndex(_dragSlots[_dragFrom].El, 10); // the tab in hand draws above its neighbours
        TabStrip.CaptureMouse();
        _dragActive = true;
        return true;
    }

    private void UpdateTabDrag(double pointerX)
    {
        var drag = _dragSlots[_dragFrom];
        double first = _dragSlots[0].Left;
        double last = _dragSlots[^1].Left + _dragSlots[^1].Width;

        // The tab follows the pointer, kept inside the strip.
        double left = Math.Clamp(pointerX - _dragGrab, first, Math.Max(first, last - drag.Width));
        ((TranslateTransform)drag.El.RenderTransform).X = left - drag.Left;

        // Its new place is after every other tab whose middle it has passed.
        double centre = left + drag.Width / 2;
        int to = 0;
        for (int i = 0; i < _dragSlots.Count; i++)
            if (i != _dragFrom && _dragSlots[i].Left + _dragSlots[i].Width / 2 < centre) to++;
        if (to == _dragTo) return;
        _dragTo = to;

        // The tabs it passed make room.
        for (int i = 0; i < _dragSlots.Count; i++)
        {
            if (i == _dragFrom) continue;
            double shift = 0;
            if (to > _dragFrom && i > _dragFrom && i <= to) shift = -drag.Width;
            else if (to < _dragFrom && i >= to && i < _dragFrom) shift = drag.Width;
            SlideTab(_dragSlots[i].El, shift);
        }
    }

    private static void SlideTab(FrameworkElement el, double x)
    {
        if (el.RenderTransform is not TranslateTransform tt) return;
        if (!Motion.Enabled) { tt.X = x; return; }
        tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    /// <summary>Button released: the tab slides into its slot, then the order is really changed.</summary>
    private void FinishTabDrag()
    {
        var slots = _dragSlots.ToList();
        int from = _dragFrom, to = _dragTo;
        var tab = _dragTab!;
        var drag = slots[from];
        var tt = (TranslateTransform)drag.El.RenderTransform;
        _dragSettling = true;

        void Commit()
        {
            _dragSettling = false;
            _dragActive = false;
            _dragTab = null;
            _dragSlots.Clear();
            foreach (var s in slots)
            {
                if (s.El.RenderTransform is TranslateTransform t) t.BeginAnimation(TranslateTransform.XProperty, null);
                s.El.ClearValue(UIElement.RenderTransformProperty);
                Panel.SetZIndex(s.El, 0);
            }
            TabStrip.ReleaseMouseCapture();
            if (from != to) MoveTabWithoutEntrance(tab, from, to, drag.El);
            TabStrip.ScrollIntoView(tab);
        }

        // Where the tab ends up: after the `to` other tabs in front of it.
        double slotLeft = slots[0].Left;
        for (int i = 0, placed = 0; i < slots.Count && placed < to; i++)
        {
            if (i == from) continue;
            slotLeft += slots[i].Width;
            placed++;
        }
        double target = slotLeft - drag.Left;

        if (!Motion.Enabled || Math.Abs(tt.X - target) < 0.5) { Commit(); return; }
        var slide = new DoubleAnimation(tt.X, target, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        slide.Completed += (_, _) => Commit();
        tt.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    /// <summary>Drag abandoned (mouse capture lost, button released elsewhere): everything snaps back.</summary>
    private void CancelTabDrag()
    {
        if (_dragSettling) return;
        var slots = _dragSlots.ToList();
        _dragActive = false;
        _dragTab = null;
        _dragSlots.Clear();
        foreach (var s in slots)
        {
            if (s.El.RenderTransform is TranslateTransform t) t.BeginAnimation(TranslateTransform.XProperty, null);
            s.El.ClearValue(UIElement.RenderTransformProperty);
            Panel.SetZIndex(s.El, 0);
        }
        TabStrip.ReleaseMouseCapture();
    }

    /// <summary>Move the tab in the list. Taking a tab out of the strip and putting it back makes it replay
    /// its "grow in" entrance, so that is cancelled the moment it is loaded again — it should just be there.</summary>
    private void MoveTabWithoutEntrance(object tab, int from, int to, FrameworkElement container)
    {
        void Settle(object? s, RoutedEventArgs a)
        {
            container.Loaded -= Settle;
            if (container is Control c && c.Template?.FindName("root", c) is FrameworkElement root)
            {
                root.BeginAnimation(OpacityProperty, null);
                root.Opacity = 1;
                if (root.RenderTransform is ScaleTransform st)
                {
                    st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    st.ScaleX = st.ScaleY = 1;
                }
            }
        }
        container.Loaded += Settle;
        ViewModel.MoveTab(from, to);
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => container.Loaded -= Settle);
    }

    // ------------------------------------------------------------- hidden tabs and their bitmaps

    // A tab you leave keeps its rendered pages for a while: flipping between two documents (the common
    // case) is then instant instead of re-rendering the page every time. Pages of a tab left alone for
    // longer are released, which is what keeps many open tabs from piling up memory.
    private static readonly TimeSpan HiddenBitmapLifetime = TimeSpan.FromSeconds(45);
    private readonly Dictionary<DocumentViewModel, CancellationTokenSource> _releaseTimers = new();

    private void ScheduleBitmapRelease(DocumentViewModel doc)
    {
        CancelBitmapRelease(doc);
        var cts = new CancellationTokenSource();
        _releaseTimers[doc] = cts;
        var token = cts.Token;
        _ = Task.Delay(HiddenBitmapLifetime, token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested) return;
                _releaseTimers.Remove(doc);
                if (ViewModel.Documents.Contains(doc) && !ReferenceEquals(ViewModel.SelectedDocument, doc))
                    doc.ReleaseBitmaps();
            });
        }, TaskScheduler.Default);
    }

    private void CancelBitmapRelease(DocumentViewModel doc)
    {
        if (_releaseTimers.Remove(doc, out var cts)) { cts.Cancel(); cts.Dispose(); }
    }
}
