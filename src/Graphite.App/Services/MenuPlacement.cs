using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Graphite.App.Services;

/// <summary>
/// Opens a menu the way a normal drop-down opens: just under the button that was clicked, its left edge on the
/// button's left edge, flipping to the right edge, or above the button, only where it would not fit on screen.
/// The "…" button on a card used to open its menu below the whole card, because the card is what the menu is
/// attached to; <paramref name="anchor"/> lets the menu stay attached to the card but appear at the button.
/// </summary>
public static class MenuPlacement
{
    // The menu card (Themes/Styles.xaml, ContextMenu) keeps a transparent margin around itself for its shadow.
    private const double PadLeft = 12, PadRight = 12, PadTop = 4, PadBottom = 18, Gap = 2;

    /// <param name="owner">What the menu belongs to (its PlacementTarget; menu handlers look it up).</param>
    /// <param name="anchor">The element to open next to; the owner itself when null.</param>
    public static void Show(ContextMenu menu, FrameworkElement owner, FrameworkElement? anchor = null)
    {
        menu.PlacementTarget = owner;
        menu.Placement = PlacementMode.Custom;
        menu.CustomPopupPlacementCallback = (popup, target, _) =>
        {
            // The anchor's box in the owner's coordinates (the callback's "target" is the owner).
            var box = new Rect(0, 0, target.Width, target.Height);
            if (anchor != null && !ReferenceEquals(anchor, owner))
            {
                try
                {
                    var topLeft = anchor.TranslatePoint(new Point(0, 0), owner);
                    box = new Rect(topLeft, anchor.RenderSize);
                }
                catch (InvalidOperationException) { /* not in the same tree any more: use the owner */ }
            }

            double below = box.Bottom - PadTop + Gap;
            double above = box.Top - popup.Height + PadBottom - Gap;
            double leftAligned = box.Left - PadLeft;
            double rightAligned = box.Right - popup.Width + PadRight;
            // WPF takes the first of these that fits on screen.
            return new[]
            {
                new CustomPopupPlacement(new Point(leftAligned, below), PopupPrimaryAxis.Horizontal),
                new CustomPopupPlacement(new Point(rightAligned, below), PopupPrimaryAxis.Horizontal),
                new CustomPopupPlacement(new Point(leftAligned, above), PopupPrimaryAxis.Vertical),
                new CustomPopupPlacement(new Point(rightAligned, above), PopupPrimaryAxis.Vertical),
            };
        };
        menu.IsOpen = true;
    }
}
