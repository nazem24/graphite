using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Graphite.App.Services;

/// <summary>
/// Eased mouse-wheel scrolling for a <see cref="ScrollViewer"/>: instead of jumping a few lines per notch,
/// the view glides to where the wheel is pointing, and quick successive notches add up into one longer glide.
/// Switch it on with <c>svc:SmoothScroll.Enabled="True"</c>. Touch panning, keyboard and scroll bars are
/// untouched, and it steps aside when motion is reduced.
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll),
            new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool value) => o.SetValue(EnabledProperty, value);

    /// <summary>For a long, busy page (the Canvas course view): while a glide runs, a transparent shield laid over the
    /// ScrollViewer takes the pointer, so the rows under it stop reacting (they slide under a still mouse while the
    /// wheel scrolls, and each would start hover animations and make the pointer's hit test walk the whole tree).
    /// The shield is one leaf element: switching it is cheap however long the page is, which matters when the person
    /// scrolls slowly and every notch is its own glide. The shield must start with IsHitTestVisible="False", have
    /// Background="Transparent" and sit above the ScrollViewer; it forwards the wheel to it.</summary>
    public static readonly DependencyProperty ShieldProperty =
        DependencyProperty.RegisterAttached("Shield", typeof(FrameworkElement), typeof(SmoothScroll),
            new PropertyMetadata(null, OnShieldChanged));

    public static FrameworkElement? GetShield(DependencyObject o) => (FrameworkElement?)o.GetValue(ShieldProperty);
    public static void SetShield(DependencyObject o, FrameworkElement? value) => o.SetValue(ShieldProperty, value);

    private static void OnShieldChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv && e.NewValue is FrameworkElement shield)
            shield.PreviewMouseWheel += (_, args) => OnWheel(sv, args);
    }

    private static readonly List<FrameworkElement> Held = new();

    private static void Hold(ScrollViewer sv)
    {
        if (GetShield(sv) is not { } shield || Held.Contains(shield)) return;
        Held.Add(shield);
        shield.IsHitTestVisible = true;
    }

    private static void ReleaseHeld()
    {
        if (Held.Count == 0) return;
        foreach (var shield in Held) shield.IsHitTestVisible = false;
        Held.Clear();
        Mouse.Synchronize();   // the row under the pointer lights up again without waiting for it to move
    }

    // The animated stand-in for the scroll offset, where the glide is heading, and when it should arrive.
    private static readonly DependencyProperty OffsetProperty =
        DependencyProperty.RegisterAttached("Offset", typeof(double), typeof(SmoothScroll),
            new PropertyMetadata(0.0, OnOffsetChanged));

    private static readonly DependencyProperty TargetProperty =
        DependencyProperty.RegisterAttached("Target", typeof(double), typeof(SmoothScroll), new PropertyMetadata(0.0));

    private static readonly DependencyProperty ArriveProperty =
        DependencyProperty.RegisterAttached("Arrive", typeof(long), typeof(SmoothScroll), new PropertyMetadata(0L));

    private const double PixelsPerNotch = 110;   // one wheel notch (Delta 120)
    private const double GlideMs = 480;   // the longest glide (was 300): a heavier, longer coast

    private static readonly IEasingFunction Ease = Make(new QuarticEase { EasingMode = EasingMode.EaseOut });

    // While a glide runs, the page's decorative loops hold still (see Motion.Scrolling); this ends that shortly after the last glide.
    private static DispatcherTimer? _idle;
    // Optional measuring aid: start Graphite with the environment variable GRAPHITE_SCROLL_PROBE=1 and every wheel
    // scroll writes one line to the error log (frames drawn, average / slowest gap between frames, how many gaps were
    // over 25 ms, and whether the pointer shield was in use). It does nothing otherwise.
    private static readonly bool Probe = Environment.GetEnvironmentVariable("GRAPHITE_SCROLL_PROBE") == "1";
    private static List<double>? _frames;
    private static long _lastFrame;
    private static string _probeNote = "";
    private static ScrollViewer? _probeView;
    private static long _tScroll;
    private static double _frameLayoutMs, _frameScrolledMs;
    private static int _frameRealized;
    private static List<string> _slow = new();
    private static int _smallSteps;
    private static long _opStart;
    private static double _busyMs;
    private static readonly List<string> LongOps = new();
    private static System.Windows.Threading.Dispatcher? _probeDispatcher;
    private static int _gc0, _gc1, _gc2;
    private static TimeSpan _gcPause;

    /// <summary>True while a scroll is being measured (see GRAPHITE_SCROLL_PROBE).</summary>
    public static bool ProbeActive => _frames != null;

    /// <summary>The page reports work it did for the current frame (time in its scroll handler, modules it brought back).</summary>
    public static void ProbeWork(double ms, int realized)
    {
        _frameScrolledMs += ms;
        _frameRealized += realized;
    }

    private static void ProbeStart(ScrollViewer sv)
    {
        if (!Probe || _frames != null) return;
        _frames = new List<double>();
        _slow = new List<string>();
        _lastFrame = Stopwatch.GetTimestamp();
        _tScroll = 0;
        _frameLayoutMs = _frameScrolledMs = 0;
        _frameRealized = 0;
        _probeNote = $"shield={(GetShield(sv) != null)} extent={sv.ExtentHeight:0} viewport={sv.ViewportHeight:0} " +
                     $"tier={RenderCapability.Tier >> 16} mode={RenderOptions.ProcessRenderMode} " +
                     $"process={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} " +
                     $"os={System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}";
        _gc0 = GC.CollectionCount(0);
        _gc1 = GC.CollectionCount(1);
        _gc2 = GC.CollectionCount(2);
        _gcPause = GC.GetTotalPauseDuration();
        _busyMs = 0;
        _smallSteps = 0;
        LongOps.Clear();
        _probeDispatcher = sv.Dispatcher;
        _probeDispatcher.Hooks.OperationStarted += OnOpStarted;
        _probeDispatcher.Hooks.OperationCompleted += OnOpCompleted;
        _probeView = sv;
        sv.LayoutUpdated += OnProbeLayout;
        CompositionTarget.Rendering += OnProbeFrame;
    }

    private static void OnOpStarted(object? sender, System.Windows.Threading.DispatcherHookEventArgs e) =>
        _opStart = Stopwatch.GetTimestamp();

    private static void OnOpCompleted(object? sender, System.Windows.Threading.DispatcherHookEventArgs e)
    {
        double ms = (Stopwatch.GetTimestamp() - _opStart) * 1000.0 / Stopwatch.Frequency;
        _busyMs += ms;
        if (ms > 8 && LongOps.Count < 12) LongOps.Add($"{e.Operation.Priority} {ms:0}ms");
    }

    private static void OnProbeLayout(object? sender, EventArgs e)
    {
        if (_tScroll == 0) return;
        _frameLayoutMs = Math.Max(_frameLayoutMs, (Stopwatch.GetTimestamp() - _tScroll) * 1000.0 / Stopwatch.Frequency);
        _tScroll = 0;
    }

    private static void OnProbeFrame(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double gap = (now - _lastFrame) * 1000.0 / Stopwatch.Frequency;
        _frames?.Add(gap);
        if (gap > 25 && _slow.Count < 12)
            _slow.Add($"{gap:0}ms [layout {_frameLayoutMs:0}, handler {_frameScrolledMs:0.0}, realized {_frameRealized}]");
        _frameLayoutMs = _frameScrolledMs = 0;
        _frameRealized = 0;
        _lastFrame = now;
    }

    private static void ProbeStop()
    {
        if (_frames == null) return;
        CompositionTarget.Rendering -= OnProbeFrame;
        if (_probeView != null) _probeView.LayoutUpdated -= OnProbeLayout;
        if (_probeDispatcher != null)
        {
            _probeDispatcher.Hooks.OperationStarted -= OnOpStarted;
            _probeDispatcher.Hooks.OperationCompleted -= OnOpCompleted;
            _probeDispatcher = null;
        }
        _probeView = null;
        var gaps = _frames;
        _frames = null;
        if (gaps.Count < 3) return;
        gaps.Sort();
        App.LogError($"Scroll probe: {gaps.Count} frames, avg {gaps.Average():0.0} ms, p95 {gaps[(int)(gaps.Count * 0.95)]:0.0} ms, " +
                     $"max {gaps[^1]:0.0} ms, {gaps.Count(g => g > 25)} over 25 ms; GC gen0/1/2 {GC.CollectionCount(0) - _gc0}/{GC.CollectionCount(1) - _gc1}/{GC.CollectionCount(2) - _gc2}, paused {(GC.GetTotalPauseDuration() - _gcPause).TotalMilliseconds:0} ms; small wheel steps {_smallSteps}; UI thread busy {_busyMs:0} ms of {gaps.Sum():0}; long operations: {string.Join(", ", LongOps)}; {_probeNote}; slow frames: {string.Join(", ", _slow)}", null);
    }

    private static void MarkScrolling(double glideMs)
    {
        Motion.SetScrolling(true);
        _idle ??= new DispatcherTimer();
        _idle.Stop();
        _idle.Interval = TimeSpan.FromMilliseconds(glideMs + 80);
        _idle.Tick -= IdleTick;
        _idle.Tick += IdleTick;
        _idle.Start();
    }
    private static void IdleTick(object? sender, EventArgs e)
    {
        _idle?.Stop();
        ReleaseHeld();
        ProbeStop();
        Motion.SetScrolling(false);
    }

    private static IEasingFunction Make(IEasingFunction f)
    {
        if (f is Freezable z) z.Freeze();
        return f;
    }

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        sv.PreviewMouseWheel -= OnWheel;
        if (e.NewValue is true) sv.PreviewMouseWheel += OnWheel;
    }

    private static void OnOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if (_frames != null && _tScroll == 0) _tScroll = Stopwatch.GetTimestamp();
        sv.ScrollToVerticalOffset((double)e.NewValue);
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer sv) return;
        if (!Motion.Enabled || Keyboard.Modifiers != ModifierKeys.None) return;   // default behaviour
        if (sv.ScrollableHeight <= 0) return;

        long now = Environment.TickCount64;
        double current = sv.VerticalOffset;
        bool gliding = now < (long)sv.GetValue(ArriveProperty);

        // A precision touchpad (or a free-spinning wheel) sends many small steps that already follow the fingers, and
        // Windows adds the coasting itself. Gliding each one would restart an ease-out every few milliseconds, and
        // every restart begins at full speed, so the movement judders, most of all when scrolling slowly. Those steps
        // are applied as they come; the glide is only for the 120-unit notches of an ordinary wheel.
        if (Math.Abs(e.Delta) < 120)
        {
            if (gliding)
            {
                // stop a running glide where it is (the base value first, so clearing it does not jump to 0)
                sv.SetValue(OffsetProperty, current);
                sv.BeginAnimation(OffsetProperty, null);
                sv.SetValue(ArriveProperty, 0L);
            }
            double step = Math.Clamp(current - e.Delta / 120.0 * PixelsPerNotch, 0, sv.ScrollableHeight);
            e.Handled = true;
            if (Math.Abs(step - current) < 0.01) return;
            _smallSteps++;
            MarkScrolling(60);
            ProbeStart(sv);
            Hold(sv);
            sv.ScrollToVerticalOffset(step);
            return;
        }
        double from = gliding ? (double)sv.GetValue(TargetProperty) : current;
        double target = Math.Clamp(from - e.Delta / 120.0 * PixelsPerNotch, 0, sv.ScrollableHeight);

        e.Handled = true;
        if (Math.Abs(target - from) < 0.5 && gliding) return;

        // a short nudge (a touchpad) glides for less time than a full notch
        double ms = Math.Clamp(Math.Abs(target - current) * 3.2 + 150, 150, GlideMs);
        sv.SetValue(TargetProperty, target);
        sv.SetValue(ArriveProperty, now + (long)ms);
        MarkScrolling(ms);
        ProbeStart(sv);
        Hold(sv);
        sv.BeginAnimation(OffsetProperty, new DoubleAnimation(current, target, Motion.Ms(ms))
        {
            EasingFunction = Ease,
            FillBehavior = FillBehavior.HoldEnd,
        });
    }
}
