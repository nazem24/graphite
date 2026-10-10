using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

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

    // The animated stand-in for the scroll offset, where the glide is heading, and when it should arrive.
    private static readonly DependencyProperty OffsetProperty =
        DependencyProperty.RegisterAttached("Offset", typeof(double), typeof(SmoothScroll),
            new PropertyMetadata(0.0, OnOffsetChanged));

    private static readonly DependencyProperty TargetProperty =
        DependencyProperty.RegisterAttached("Target", typeof(double), typeof(SmoothScroll), new PropertyMetadata(0.0));

    private static readonly DependencyProperty ArriveProperty =
        DependencyProperty.RegisterAttached("Arrive", typeof(long), typeof(SmoothScroll), new PropertyMetadata(0L));

    private const double PixelsPerNotch = 110;   // one wheel notch (Delta 120)
    private const double GlideMs = 300;

    private static readonly IEasingFunction Ease = Make(new QuinticEase { EasingMode = EasingMode.EaseOut });

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
        if (d is ScrollViewer sv) sv.ScrollToVerticalOffset((double)e.NewValue);
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer sv) return;
        if (!Motion.Enabled || Keyboard.Modifiers != ModifierKeys.None) return;   // default behaviour
        if (sv.ScrollableHeight <= 0) return;

        long now = Environment.TickCount64;
        double current = sv.VerticalOffset;
        bool gliding = now < (long)sv.GetValue(ArriveProperty);
        double from = gliding ? (double)sv.GetValue(TargetProperty) : current;
        double target = Math.Clamp(from - e.Delta / 120.0 * PixelsPerNotch, 0, sv.ScrollableHeight);

        e.Handled = true;
        if (Math.Abs(target - from) < 0.5 && gliding) return;

        // a short nudge (a touchpad) glides for less time than a full notch
        double ms = Math.Clamp(Math.Abs(target - current) * 3 + 120, 120, GlideMs);
        sv.SetValue(TargetProperty, target);
        sv.SetValue(ArriveProperty, now + (long)ms);
        sv.BeginAnimation(OffsetProperty, new DoubleAnimation(current, target, Motion.Ms(ms))
        {
            EasingFunction = Ease,
            FillBehavior = FillBehavior.HoldEnd,
        });
    }
}
