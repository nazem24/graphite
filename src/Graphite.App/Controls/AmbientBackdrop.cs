using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Graphite.App.Services;

namespace Graphite.App.Controls;

/// <summary>
/// Two huge, very soft glows in the accent colour that drift slowly behind the start page's
/// glass card. It only animates while it is visible, and holds still under reduced motion.
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

    public AmbientBackdrop()
    {
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => UpdateDrift();
        Loaded += (_, _) => UpdateDrift();
        Unloaded += (_, _) => BeginAnimation(PhaseProperty, null);
    }

    private void UpdateDrift()
    {
        if (IsVisible && IsLoaded && Motion.Enabled)
        {
            BeginAnimation(PhaseProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(24))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = Motion.Sine,
            });
        }
        else
        {
            BeginAnimation(PhaseProperty, null);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double t = Phase * Math.PI * 2;
        var accent = TryFindResource("App.AccentSystemColor") as Color? ?? Color.FromRgb(0x3E, 0x6D, 0xB5);
        double size = Math.Max(w, h);

        Glow(dc, new Point(w * (0.26 + 0.10 * Math.Sin(t)), h * (0.34 + 0.08 * Math.Cos(t * 0.8))),
            size * 0.42, Color.FromArgb(0x2A, accent.R, accent.G, accent.B));
        Glow(dc, new Point(w * (0.76 + 0.09 * Math.Cos(t * 0.9)), h * (0.68 + 0.10 * Math.Sin(t * 1.1))),
            size * 0.36, Color.FromArgb(0x1E, accent.R, accent.G, accent.B));
    }

    private static void Glow(DrawingContext dc, Point center, double radius, Color color)
    {
        var brush = new RadialGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B));
        brush.Freeze();
        dc.DrawEllipse(brush, null, center, radius, radius);
    }
}
