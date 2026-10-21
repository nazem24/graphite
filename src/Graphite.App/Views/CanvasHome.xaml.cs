using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Graphite.App.Services;
using Graphite.App.ViewModels;

namespace Graphite.App.Views;

/// <summary>The Canvas page. Behaviour lives in <see cref="CanvasViewModel"/>; this file opens the small menus
/// next to the buttons that asked for them, and plays the same page entrance as the Local pages (a short fade and
/// lift, skipped when motion is reduced) when another page or course is shown.</summary>
public partial class CanvasHome : UserControl
{
    private CanvasViewModel? _watched;
    private (CanvasPage Page, string? Course) _shown;

    public CanvasHome()
    {
        InitializeComponent();
        // the start page's links open files in Graphite like their rows do
        FrontPage.LinkClicked += href => Canvas?.OpenPageLink(href);
        SmoothScroll.SetShield(Scroller, GlideShield);   // in code, so there is no binding that could silently not resolve
        Scroller.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange != 0 || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) OnScrolled();
        };
        _pruneTimer.Tick += (_, _) =>
        {
            _pruneTimer.Stop();
            Prune();
        };
        DataContextChanged += (_, e) =>
        {
            if (_watched != null)
            {
                _watched.PageBuilt -= OnPageBuilt;
                _watched.Navigate = null;
            }
            _watched = e.NewValue as CanvasViewModel;
            if (_watched != null)
            {
                _watched.PageBuilt += OnPageBuilt;
                _watched.Navigate = OnNavigate;
            }
        };
    }

    private CanvasViewModel? Canvas => DataContext as CanvasViewModel;

    // ------------------------------------------------------------- "Mark seen" on the New strip
    // The tile (or the whole strip) fades and shrinks away first; only then are the items marked, so the page
    // below does not jump while it is still visible. With motion reduced it is marked at once.

    private bool _seenBusy;

    private void NewCardSeen_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CanvasSectionVm section } button || Canvas is not { } vm) return;
        DependencyObject? up = button;
        while (up != null && up is not ContentPresenter) up = VisualTreeHelper.GetParent(up);
        PlaySeen(up as FrameworkElement, 0.9, () => vm.MarkCourseSeenCommand.Execute(section.CourseId), collapse: false);
    }

    private void NewStripSeen_Click(object sender, RoutedEventArgs e)
    {
        if (Canvas is not { } vm) return;
        PlaySeen(NewStrip, 1, () => vm.MarkAllSeenCommand.Execute(null), collapse: true);
    }

    private void PlaySeen(FrameworkElement? target, double toScale, Action done, bool collapse)
    {
        if (_seenBusy) return;
        if (target == null || !Motion.Enabled || !IsLoaded)
        {
            done();
            return;
        }
        _seenBusy = true;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var dur = Motion.Ms(220);
        void Finish()
        {
            _seenBusy = false;
            // the element may be reused by the ItemsControl: put it back before it is shown again
            target.BeginAnimation(OpacityProperty, null);
            target.Opacity = 1;
            target.RenderTransform = Transform.Identity;
            target.LayoutTransform = Transform.Identity;
            done();
        }
        var fade = new DoubleAnimation(1, 0, dur) { EasingFunction = ease };
        fade.Completed += (_, _) => Finish();
        target.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(1, 1);
        if (collapse)
        {
            // the strip shrinks in height, so everything below glides up with it
            target.LayoutTransform = scale;
            target.RenderTransformOrigin = new Point(0.5, 0);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, 0, dur) { EasingFunction = ease });
        }
        else
        {
            target.RenderTransform = scale;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, toScale, dur) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, toScale, dur) { EasingFunction = ease });
        }
        target.BeginAnimation(OpacityProperty, fade);
    }

    // ------------------------------------------------------------- long courses
    // A course can have hundreds of rows, each a button with a dozen parts. Keeping all of them laid out and drawn
    // made scrolling heavy, so only the modules near the window keep their rows live. A module that is well out of
    // view swaps its rows for an empty space of the same height (the page does not move), and gets them back
    // before it scrolls into view. The rows stay created, so showing them again is only a layout.

    private readonly DispatcherTimer _pruneTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };

    private IEnumerable<(CanvasModuleVm Module, FrameworkElement Box, double Top, double Bottom)> ModuleBoxes()
    {
        if (Canvas is not { Page: CanvasPage.Course }) yield break;
        for (int i = 0; i < ModulesList.Items.Count; i++)
        {
            if (ModulesList.Items[i] is not CanvasModuleVm module) continue;
            if (ModulesList.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement box
                || !box.IsLoaded || box.ActualHeight <= 0) continue;
            double top;
            try { top = box.TranslatePoint(new Point(0, 0), Scroller).Y; }
            catch (InvalidOperationException) { continue; }
            yield return (module, box, top, top + box.ActualHeight);
        }
    }

    /// <summary>The page moved (or grew): bring back the rows of modules that are getting close, at once, and let
    /// the far ones go once the scrolling pauses.</summary>
    private void OnScrolled()
    {
        double view = Scroller.ViewportHeight;
        if (view <= 0 || Canvas is not { Page: CanvasPage.Course }) return;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int realized = 0;
        foreach (var (module, _, top, bottom) in ModuleBoxes())
            if (!module.IsRealized && bottom > -view * 0.75 && top < view * 2.5)
            {
                module.IsRealized = true;
                realized++;
            }
        _pruneTimer.Stop();
        _pruneTimer.Start();
        if (SmoothScroll.ProbeActive)
            SmoothScroll.ProbeWork((System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency, realized);
    }

    private void Prune()
    {
        double view = Scroller.ViewportHeight;
        if (view <= 0) return;
        foreach (var (module, box, top, bottom) in ModuleBoxes().ToList())
        {
            if (!module.IsRealized || !module.IsExpanded) continue;
            if (bottom > -view * 1.5 && top < view * 3.5) continue;   // wider than the margin above, so it does not flicker
            if (box is not ContentPresenter presenter
                || presenter.ContentTemplate?.FindName("RowsHost", presenter) is not FrameworkElement rows
                || rows.ActualHeight <= 0) continue;
            module.PlaceholderHeight = rows.ActualHeight;
            module.IsRealized = false;
        }
    }

    private void DownloadMenu_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: CanvasRowVm row } owner && Canvas is { } canvas)
            CanvasMenus.OpenDownloadMenu(owner, canvas, row);
    }

    private void FolderMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement owner && Canvas is { } canvas) CanvasMenus.OpenFolderMenu(owner, canvas);
    }

    // ------------------------------------------------------------- page change
    // The same three steps as the pages of the Local space: the old page fades out and drifts a little (nothing is
    // built yet, so those frames are free); with it invisible the new page is built and laid out; then it comes in
    // from the side you are heading to and settles. Into a course it arrives from the right, back out of it from
    // the left, between the other pages it just lifts.

    private int _gen;
    private int _direction;
    private Action? _pending;
    private bool _awaiting, _playQueued;
    private DispatcherTimer? _failsafe;

    private static void Animate(IAnimatable target, DependencyProperty property, double from, double to,
        double ms, IEasingFunction ease, EventHandler? completed = null)
    {
        var anim = new DoubleAnimation(from, to, Motion.Ms(ms))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        };
        if (completed != null) anim.Completed += completed;
        target.BeginAnimation(property, anim);
    }

    /// <summary>Step 1: fade the page you are leaving out.</summary>
    private void OnNavigate(int direction, Action apply)
    {
        EndTransition();   // a change still waiting for its fade-out is applied right now
        if (!Motion.Enabled || !IsLoaded || !IsVisible || PageRoot.ActualWidth < 16 || PageRoot.ActualHeight < 16)
        {
            apply();
            return;
        }

        int gen = ++_gen;
        _direction = direction;
        _pending = apply;

        PageRoot.CacheMode = new BitmapCache();
        var move = Motion.RigOf(PageRoot)?.Move;
        Animate(PageRoot, OpacityProperty, 1, 0, Motion.PageOutMs, Motion.PageOutEase,
            completed: (_, _) => Dispatcher.InvokeAsync(() =>
            {
                if (gen == _gen) SwapPage();
            }, DispatcherPriority.Render));
        if (move != null)
            Animate(move, TranslateTransform.XProperty, 0, -direction * Motion.PageOutDriftPx, Motion.PageOutMs, Motion.PageOutEase);

        // if the event above never arrives the page is still swapped (SwapPage runs only once)
        Motion.After(Motion.PageOutMs + 80, () =>
        {
            if (gen == _gen) SwapPage();
        });
    }

    /// <summary>Step 2: with the page invisible, build the new one.</summary>
    private void SwapPage()
    {
        var apply = _pending;
        _pending = null;
        if (apply == null) return;

        _awaiting = true;
        _playQueued = false;

        // if no page is announced (nothing changed after all), the new page still comes in
        _failsafe ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _failsafe.Tick -= Failsafe_Tick;
        _failsafe.Tick += Failsafe_Tick;
        _failsafe.Start();

        apply();   // the page announces itself through OnPageBuilt
    }

    private void Failsafe_Tick(object? sender, EventArgs e)
    {
        _failsafe?.Stop();
        if (_awaiting) PlayEnter();
    }

    /// <summary>A page was (re)built. A refresh of the same page keeps its place; a different page or course
    /// starts at the top and, once it has been laid out, comes in.</summary>
    private void OnPageBuilt()
    {
        if (Canvas is not { } canvas) return;
        var now = (canvas.Page, canvas.Page == CanvasPage.Course ? canvas.CurrentCourse?.Id : null);
        if (now == _shown) return;
        _shown = now;
        Scroller.ScrollToVerticalOffset(0);

        if (_awaiting) QueueEnter();
        else Enter();
    }

    /// <summary>Wait until the new page has been laid out (Input priority runs after layout and render work), so the
    /// movement starts on a free UI thread.</summary>
    private void QueueEnter()
    {
        if (!_awaiting || _playQueued) return;
        _playQueued = true;
        int gen = _gen;
        Dispatcher.InvokeAsync(() =>
        {
            if (gen == _gen && _awaiting) PlayEnter();
        }, DispatcherPriority.Input);
    }

    /// <summary>Step 3: the new page arrives and settles with a long, soft stop.</summary>
    private void PlayEnter()
    {
        _failsafe?.Stop();
        _awaiting = false;
        int gen = _gen;
        double dir = _direction;

        PageRoot.CacheMode ??= new BitmapCache();
        var move = Motion.RigOf(PageRoot)?.Move;
        Animate(PageRoot, OpacityProperty, 0, 1, Motion.PageInFadeMs, Motion.PageFadeEase);
        if (move != null)
        {
            Animate(move, TranslateTransform.XProperty, dir * Motion.PageInSlidePx, 0, Motion.PageInSlideMs, Motion.PageSlideEase);
            Animate(move, TranslateTransform.YProperty, dir == 0 ? 12 : 0, 0, Motion.PageInSlideMs, Motion.PageSlideEase);
        }
        Motion.After(Motion.PageInSlideMs + 40, () =>
        {
            if (gen == _gen) EndTransition();
        });
    }

    /// <summary>Everything back to rest: the page fully shown and live. A change still waiting for the fade-out is
    /// applied here, so a navigation is never lost.</summary>
    private void EndTransition()
    {
        var pending = _pending;
        _pending = null;
        _gen++;
        _awaiting = false;
        _playQueued = false;
        _failsafe?.Stop();

        PageRoot.BeginAnimation(OpacityProperty, null);
        PageRoot.Opacity = 1;
        if (Motion.RigOf(PageRoot) is { } rig)
        {
            rig.Move.BeginAnimation(TranslateTransform.XProperty, null);
            rig.Move.BeginAnimation(TranslateTransform.YProperty, null);
            rig.Move.X = 0;
            rig.Move.Y = 0;
        }
        PageRoot.CacheMode = null;
        pending?.Invoke();
    }

    /// <summary>Fade and lift the page in (a page that was built without a change of page).</summary>
    public void Enter()
    {
        EndTransition();
        if (!Motion.Enabled || !IsVisible) return;

        int gen = _gen;
        PageRoot.CacheMode = new BitmapCache();
        var move = Motion.RigOf(PageRoot)?.Move;
        Animate(PageRoot, OpacityProperty, 0, 1, Motion.PageInFadeMs, Motion.PageFadeEase);
        if (move != null) Animate(move, TranslateTransform.YProperty, 12, 0, Motion.PageInSlideMs, Motion.PageSlideEase);
        Motion.After(Motion.PageInSlideMs + 40, () =>
        {
            if (gen == _gen) EndTransition();
        });
    }
}
