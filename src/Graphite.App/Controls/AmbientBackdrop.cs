using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Graphite.App.Services;

namespace Graphite.App.Controls;

/// <summary>
/// Two huge, very soft glows in the accent colour that drift slowly behind the start page's
/// glass card. It only animates while it is visible, holds still under reduced motion, and
/// pauses while the window is being dragged (every frame of the drift re-runs
/// <see cref="OnRender"/> on the UI thread, which is the same thread Windows uses to move the
/// window — so a drift running during a drag made the drag stutter).
/// </summary>
public sealed class AmbientBackdrop : FrameworkElement
{
    public static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        nameof(Phase), typeof(double), typeof(AmbientBackdrop),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Phase
    {
        get => (double)GetValue(PhaseProperty);
        set => SetValue(PhaseProperty, value);
    }

    private AnimationClock? _clock;
    private bool _paused;

    // The two glow brushes only change with the accent colour — not once per frame.
    private Color _brushAccent;
    private double _brushStrength = 1;
    private Brush? _glowA, _glowB;

    public AmbientBackdrop()
    {
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => UpdateDrift();
        Loaded += (_, _) =>
        {
            Motion.InteractingChanged -= UpdateDrift;
            Motion.InteractingChanged += UpdateDrift;
            ThemeService.AppearanceChanged -= InvalidateVisual;
            ThemeService.AppearanceChanged += InvalidateVisual;
            UpdateDrift();
        };
        Unloaded += (_, _) =>
        {
            Motion.InteractingChanged -= UpdateDrift;
            ThemeService.AppearanceChanged -= InvalidateVisual;
            StopDrift();
        };
    }

    private void UpdateDrift()
    {
        bool animate = IsVisible && IsLoaded && !Motion.UserReduced && SystemParameters.ClientAreaAnimation;
        if (!animate)
        {
            StopDrift();
            return;
        }

        if (_clock == null)
        {
            var drift = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(24))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = Motion.Sine,
            };
            // The glows take half a minute to cross the page: 20 fps looks the same as 60 and
            // costs a third of the UI-thread work.
            Timeline.SetDesiredFrameRate(drift, 20);
            _clock = (AnimationClock)drift.CreateClock();
            _paused = false;
            ApplyAnimationClock(PhaseProperty, _clock);
        }

        // Hold still while the window is dragged, then carry on from the same spot.
        if (Motion.Interacting && !_paused)
        {
            _clock.Controller?.Pause();
            _paused = true;
        }
        else if (!Motion.Interacting && _paused)
        {
            _clock.Controller?.Resume();
            _paused = false;
        }
    }

    private void StopDrift()
    {
        if (_clock == null) return;
        _clock = null;
        _paused = false;
        ApplyAnimationClock(PhaseProperty, null);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double t = Phase * Math.PI * 2;
        var accent = TryFindResource("App.AccentSystemColor") as Color? ?? Color.FromRgb(0x3E, 0x6D, 0xB5);
        // The material (Themes/Materials) says how strongly the glows show: faint behind Frosted,
        // livelier behind Glass.
        double strength = TryFindResource("App.AmbientStrength") as double? ?? 1.0;
        if (_glowA == null || _glowB == null || accent != _brushAccent || strength != _brushStrength)
        {
            _brushAccent = accent;
            _brushStrength = strength;
            _glowA = Glow(accent, Scale(0x2A, strength));
            _glowB = Glow(accent, Scale(0x1E, strength));
        }
        double size = Math.Max(w, h);

        dc.DrawEllipse(_glowA, null,
            new Point(w * (0.26 + 0.10 * Math.Sin(t)), h * (0.34 + 0.08 * Math.Cos(t * 0.8))),
            size * 0.42, size * 0.42);
        dc.DrawEllipse(_glowB, null,
            new Point(w * (0.76 + 0.09 * Math.Cos(t * 0.9)), h * (0.68 + 0.10 * Math.Sin(t * 1.1))),
            size * 0.36, size * 0.36);
    }

    private static byte Scale(byte alpha, double strength) => (byte)Math.Clamp(alpha * strength, 0, 255);

    private static Brush Glow(Color accent, byte alpha)
    {
        var color = Color.FromArgb(alpha, accent.R, accent.G, accent.B);
        var brush = new RadialGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}
