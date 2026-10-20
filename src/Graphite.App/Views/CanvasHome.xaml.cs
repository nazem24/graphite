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
            if (_watched != null) _watched.PageBuilt -= OnPageBuilt;
            _watched = e.NewValue as CanvasViewModel;
            if (_watched != null) _watched.PageBuilt += OnPageBuilt;
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

    /// <summary>A page was (re)built. A refresh of the same page keeps its place; a different page or course
    /// starts at the top and comes in with a short fade.</summary>
    private void OnPageBuilt()
    {
        if (Canvas is not { } canvas) return;
        var now = (canvas.Page, canvas.Page == CanvasPage.Course ? canvas.CurrentCourse?.Id : null);
        if (now == _shown) return;
        _shown = now;
        Scroller.ScrollToVerticalOffset(0);
        Enter();
    }

    /// <summary>Fade and lift the page in (the start screen's sideways page change).</summary>
    public void Enter()
    {
        var move = (TranslateTransform)PageRoot.RenderTransform;
        PageRoot.BeginAnimation(OpacityProperty, null);
        move.BeginAnimation(TranslateTransform.YProperty, null);
        PageRoot.Opacity = 1;
        move.Y = 0;
        if (!Motion.Enabled || !IsVisible) return;

        PageRoot.CacheMode = new BitmapCache();
        PageRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Ms(Motion.PageInFadeMs))
        {
            EasingFunction = Motion.PageFadeEase,
            FillBehavior = FillBehavior.Stop,
        });
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, Motion.Ms(Motion.PageInSlideMs))
        {
            EasingFunction = Motion.PageSlideEase,
            FillBehavior = FillBehavior.Stop,
        });
        Motion.After(Motion.PageInSlideMs + 40, () => PageRoot.CacheMode = null);
    }
}
