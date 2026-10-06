using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Graphite.App.Services;

/// <summary>
/// One place for the app's code-driven motion: the "Reduce motion" switch, shared easing,
/// and a few tiny helpers so every animation behaves the same way (and every one of them
/// can be switched off together).
///
/// "Reduce motion" is on when the user turned it on in Graphite (palette: "Toggle reduced
/// motion") or when Windows' own "Show animations" setting is off.
/// </summary>
public static class Motion
{
    /// <summary>The user's own choice (persisted by <see cref="ThemeService"/>).</summary>
    public static bool UserReduced { get; set; }

    /// <summary>True when movement / scale / shimmer effects may play.</summary>
    public static bool Enabled => !UserReduced && SystemParameters.ClientAreaAnimation;

    public static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseInOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });
    public static readonly IEasingFunction EaseIn = Freeze(new CubicEase { EasingMode = EasingMode.EaseIn });
    public static readonly IEasingFunction Soft = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });
    public static readonly IEasingFunction Sine = Freeze(new SineEase { EasingMode = EasingMode.EaseInOut });

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    public static Duration Ms(double ms) => new(TimeSpan.FromMilliseconds(ms));

    /// <summary>
    /// Animate a double property from → to and leave it at <paramref name="to"/> afterwards.
    /// With a delay the property is parked at <paramref name="from"/> first, so a staggered
    /// element doesn't flash at its final state before its turn comes.
    /// </summary>
    public static void Tween<T>(T target, DependencyProperty property, double from, double to,
        double ms, double delayMs = 0, IEasingFunction? ease = null)
        where T : DependencyObject, IAnimatable
    {
        var anim = new DoubleAnimation(from, to, Ms(ms))
        {
            EasingFunction = ease ?? EaseOut,
            FillBehavior = FillBehavior.HoldEnd,
        };
        if (delayMs > 0)
        {
            anim.BeginTime = TimeSpan.FromMilliseconds(delayMs);
            target.SetValue(property, from);
        }
        anim.Completed += (_, _) =>
        {
            target.BeginAnimation(property, null);
            target.SetValue(property, to);
        };
        target.BeginAnimation(property, anim);
    }

    /// <summary>Multi-step animation (time in ms → value); ends on its last value and then
    /// hands the property back to its base value, so the last frame must equal the base.</summary>
    public static void Keys<T>(T target, DependencyProperty property, params (double Ms, double Value)[] frames)
        where T : DependencyObject, IAnimatable
    {
        var anim = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        foreach (var (ms, value) in frames)
            anim.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), EaseOut));
        target.BeginAnimation(property, anim);
    }

    /// <summary>Scale / rotate / translate transforms that animations can drive on an element.</summary>
    public sealed class Rig
    {
        public required ScaleTransform Scale { get; init; }
        public required RotateTransform Rotate { get; init; }
        public required TranslateTransform Move { get; init; }
        public required TransformGroup Group { get; init; }
    }

    private static readonly ConditionalWeakTable<UIElement, Rig> Rigs = new();

    /// <summary>
    /// Give <paramref name="el"/> a Scale/Rotate/Translate transform group (once) and return
    /// it. Origin defaults to the centre. Returns null if the element already carries a
    /// transform of its own — we never overwrite those.
    /// </summary>
    public static Rig? RigOf(UIElement el, Point? origin = null)
    {
        if (Rigs.TryGetValue(el, out var existing) && ReferenceEquals(el.RenderTransform, existing.Group))
        {
            if (origin is { } o) el.RenderTransformOrigin = o;
            return existing;
        }
        if (el.RenderTransform is { } t && !t.Value.IsIdentity) return null;

        var scale = new ScaleTransform(1, 1);
        var rotate = new RotateTransform(0);
        var move = new TranslateTransform(0, 0);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(rotate);
        group.Children.Add(move);
        el.RenderTransformOrigin = origin ?? new Point(0.5, 0.5);
        el.RenderTransform = group;

        var rig = new Rig { Scale = scale, Rotate = rotate, Move = move, Group = group };
        Rigs.Remove(el);
        Rigs.Add(el, rig);
        return rig;
    }

    /// <summary>Fade (and optionally lift) an element in.</summary>
    public static void FadeIn(UIElement el, double ms = 160, double delayMs = 0, double liftPx = 0)
    {
        if (!Enabled) return;
        Tween(el, UIElement.OpacityProperty, 0, 1, ms, delayMs);
        if (liftPx != 0 && RigOf(el) is { } rig)
            Tween(rig.Move, TranslateTransform.YProperty, liftPx, 0, ms + 40, delayMs);
    }

    /// <summary>Run <paramref name="then"/> once an animation has finished — immediately
    /// when motion is reduced.</summary>
    public static void After(double ms, Action then)
    {
        if (!Enabled || ms <= 0) { then(); return; }
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            then();
        };
        timer.Start();
    }
}
