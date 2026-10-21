using System.Windows;
using System.Windows.Controls;

namespace Graphite.App.Controls;

/// <summary>
/// The column of one day in the calendar's week view. Every child says where it starts (Top, in pixels from the
/// top of the column), how tall it is (BlockHeight), and, when entries overlap in time, which of Cols side-by-side
/// lanes it takes (Col). Set the panel's own Height to the height of the whole grid.
/// </summary>
public sealed class WeekColumnPanel : Panel
{
    public static readonly DependencyProperty TopProperty = DependencyProperty.RegisterAttached(
        "Top", typeof(double), typeof(WeekColumnPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentArrange | FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty BlockHeightProperty = DependencyProperty.RegisterAttached(
        "BlockHeight", typeof(double), typeof(WeekColumnPanel),
        new FrameworkPropertyMetadata(24.0, FrameworkPropertyMetadataOptions.AffectsParentArrange | FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty ColProperty = DependencyProperty.RegisterAttached(
        "Col", typeof(int), typeof(WeekColumnPanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentArrange | FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static readonly DependencyProperty ColsProperty = DependencyProperty.RegisterAttached(
        "Cols", typeof(int), typeof(WeekColumnPanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentArrange | FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static double GetTop(DependencyObject d) => (double)d.GetValue(TopProperty);
    public static void SetTop(DependencyObject d, double value) => d.SetValue(TopProperty, value);
    public static double GetBlockHeight(DependencyObject d) => (double)d.GetValue(BlockHeightProperty);
    public static void SetBlockHeight(DependencyObject d, double value) => d.SetValue(BlockHeightProperty, value);
    public static int GetCol(DependencyObject d) => (int)d.GetValue(ColProperty);
    public static void SetCol(DependencyObject d, int value) => d.SetValue(ColProperty, value);
    public static int GetCols(DependencyObject d) => (int)d.GetValue(ColsProperty);
    public static void SetCols(DependencyObject d, int value) => d.SetValue(ColsProperty, value);

    private static Rect Slot(UIElement child, double width)
    {
        int cols = Math.Max(1, GetCols(child));
        int col = Math.Clamp(GetCol(child), 0, cols - 1);
        double lane = width / cols;
        return new Rect(col * lane + 1, GetTop(child), Math.Max(0, lane - 2), Math.Max(0, GetBlockHeight(child)));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        foreach (UIElement child in InternalChildren)
        {
            var slot = Slot(child, width);
            child.Measure(new Size(slot.Width, slot.Height));
        }
        return new Size(width, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (UIElement child in InternalChildren)
            child.Arrange(Slot(child, finalSize.Width));
        return finalSize;
    }
}
