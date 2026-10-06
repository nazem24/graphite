using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Graphite.App.Services;

namespace Graphite.App.Controls;

/// <summary>
/// Skeleton placeholder: a faint grey panel with a soft highlight that sweeps across it
/// while it is visible. Used behind pages whose bitmap hasn't been rendered yet. The sweep
/// only runs while the element is actually visible, and not at all under reduced motion.
/// </summary>
public sealed class ShimmerBorder : Border
{
    private readonly TranslateTransform _sweep = new();

    public ShimmerBorder()
    {
        IsHitTestVisible = false;

        var edge = Color.FromArgb(0x10, 0x80, 0x80, 0x80);
        var glow = Color.FromArgb(0x2C, 0x80, 0x80, 0x80);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            RelativeTransform = _sweep,
        };
        brush.GradientStops.Add(new GradientStop(edge, 0.0));
        brush.GradientStops.Add(new GradientStop(glow, 0.5));
        brush.GradientStops.Add(new GradientStop(edge, 1.0));
        Background = brush;

        IsVisibleChanged += (_, _) => UpdateSweep();
        Loaded += (_, _) => UpdateSweep();
        Unloaded += (_, _) => _sweep.BeginAnimation(TranslateTransform.XProperty, null);
    }

    private void UpdateSweep()
    {
        if (IsVisible && IsLoaded && Motion.Enabled)
        {
            _sweep.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(-1, 1, TimeSpan.FromMilliseconds(1400))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = Motion.Sine,
                });
        }
        else
        {
            _sweep.BeginAnimation(TranslateTransform.XProperty, null);
        }
    }
}
