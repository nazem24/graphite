using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Graphite.App.ViewModels;

namespace Graphite.App.Views;

/// <summary>The start screen: library rail, folders, files. Almost all behavior lives in
/// <see cref="LibraryViewModel"/>; this file only does what XAML cannot — lazy preview loading
/// when a card appears, and opening menus next to the button that asked for them.</summary>
public partial class LibraryHome : UserControl
{
    public LibraryHome()
    {
        InitializeComponent();
        // The card hangs just under the button, right edges lined up, so it can grow out of it.
        NewPopup.CustomPopupPlacementCallback = (popupSize, targetSize, _) => new[]
        {
            new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width + NewPopupPad, targetSize.Height + NewGap - NewPopupPad),
                                     PopupPrimaryAxis.None)
        };
    }

    private LibraryViewModel? Library => DataContext as LibraryViewModel;

    // ------------------------------------------------------------- start screen header

    private const double HomeHeaderGap = 30;

    private void HomeHeader_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        PlaceClockColon();
        LayoutHomeHeader();
    }

    // ------------------------------------------------------------- "New" menu

    private DateTime _newClosedAt = DateTime.MinValue;

    /// <summary>Opens or closes the "New" card. A click on the button while the card is open first
    /// dismisses it (StaysOpen = false) and then arrives here, so a click right after a close is
    /// ignored instead of reopening the card.</summary>
    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        if (NewPopup.IsOpen) { NewPopup.IsOpen = false; return; }
        if ((DateTime.UtcNow - _newClosedAt).TotalMilliseconds < 250) return;
        NewPopup.IsOpen = true;
    }

    private void NewItem_Click(object sender, RoutedEventArgs e) => NewPopup.IsOpen = false;

    private const double NewCardWidth = 308, NewCardHeight = 162, NewPopupPad = 40, NewGap = 6;

    /// <summary>The card opens just under the button and grows out of it: it starts as wide as the
    /// button, flat, hugging the button's right edge, and unfolds down and to the left while the
    /// actions fade in. The chevron on the button turns over.</summary>
    private void NewPopup_Opened(object? sender, EventArgs e)
    {
        var rot = (RotateTransform)NewChevron.RenderTransform;
        rot.Angle = 180;

        if (!Services.Motion.Enabled)
        {
            NewCard.BeginAnimation(WidthProperty, null);
            NewCard.BeginAnimation(HeightProperty, null);
            NewItems.BeginAnimation(OpacityProperty, null);
            NewCard.Width = NewCardWidth;
            NewCard.Height = NewCardHeight;
            NewItems.Opacity = 1;
            return;
        }

        var grow = TimeSpan.FromMilliseconds(240);
        var ease = new QuinticEase { EasingMode = EasingMode.EaseOut };
        NewCard.BeginAnimation(WidthProperty, new DoubleAnimation(NewButton.ActualWidth, NewCardWidth, grow) { EasingFunction = ease });
        NewCard.BeginAnimation(HeightProperty, new DoubleAnimation(8, NewCardHeight, grow) { EasingFunction = ease });
        NewItems.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            { BeginTime = TimeSpan.FromMilliseconds(70) });
    }

    /// <summary>Clips the card's contents to its rounded outline (ClipToBounds would clip to a plain
    /// rectangle and the row highlights would poke out of the corners). The clip sits on the inner grid so
    /// the card's own drop shadow is not cut off.</summary>
    private void NewClipHost_SizeChanged(object sender, SizeChangedEventArgs e) =>
        NewClipHost.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 17, 17);

    private void NewPopup_Closed(object? sender, EventArgs e)
    {
        _newClosedAt = DateTime.UtcNow;
        ((RotateTransform)NewChevron.RenderTransform).Angle = 0;

        // Back to the folded state, so the next opening starts from it with no flash.
        NewCard.BeginAnimation(WidthProperty, null);
        NewCard.BeginAnimation(HeightProperty, null);
        NewItems.BeginAnimation(OpacityProperty, null);
        NewCard.Width = Math.Max(1, NewButton.ActualWidth);
        NewCard.Height = 8;
        NewItems.Opacity = 0;
    }

    private bool _colonPlaced;

    /// <summary>Centres the clock's colon on the digits. The two dots are shapes, so they know
    /// nothing about the font; this measures the real outline of a "0" in the clock's typeface and
    /// puts the colon's middle on the middle of that outline (top of the digit row = top of the text).</summary>
    private void PlaceClockColon()
    {
        if (_colonPlaced || ClockHourText == null || ClockColon == null || ClockColon.ActualHeight <= 0) return;

        var tb = ClockHourText;
        var typeface = new System.Windows.Media.Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
        var digit = new System.Windows.Media.FormattedText("0", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, tb.FontSize, System.Windows.Media.Brushes.Black,
            System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var ink = digit.BuildGeometry(new Point(0, 0)).Bounds;
        if (ink.IsEmpty || ink.Height <= 0) return;

        double top = ink.Top + ink.Height / 2 - ClockColon.ActualHeight / 2;
        ClockColon.Margin = new Thickness(ClockColon.Margin.Left, Math.Max(0, top), ClockColon.Margin.Right, 0);
        _colonPlaced = true;
    }

    /// <summary>Clock and greeting on the left, the action pill flush right. When the two no longer
    /// fit on one row the pill drops underneath, left-aligned (and the Viewbox shrinks it if even
    /// that is too wide).</summary>
    private void LayoutHomeHeader()
    {
        if (HomeHeader.ActualWidth <= 0 || HomeLeft.ActualWidth <= 0 || HomePill.Child is not FrameworkElement pill)
            return;

        pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        bool sideBySide = HomeLeft.ActualWidth + HomeHeaderGap + pill.DesiredSize.Width <= HomeHeader.ActualWidth;

        int row = sideBySide ? 0 : 1, column = sideBySide ? 2 : 0, span = sideBySide ? 1 : 3;
        var align = sideBySide ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        if (Grid.GetRow(HomePill) != row) Grid.SetRow(HomePill, row);
        if (Grid.GetColumn(HomePill) != column) Grid.SetColumn(HomePill, column);
        if (Grid.GetColumnSpan(HomePill) != span) Grid.SetColumnSpan(HomePill, span);
        if (HomePill.HorizontalAlignment != align) HomePill.HorizontalAlignment = align;
    }

    // ------------------------------------------------------------- selection

    /// <summary>Ctrl-click or Shift-click on a card ticks it instead of opening it; so does any
    /// click once something is already ticked. Clicks on the card's own small buttons
    /// (pin, more, tick box) keep their meaning.</summary>
    private void Card_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FileCardViewModel file } card) return;
        bool modifier = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        if (!modifier && Library is not { HasSelection: true }) return;

        for (DependencyObject? up = e.OriginalSource as DependencyObject;
             up is System.Windows.Media.Visual && !ReferenceEquals(up, card);
             up = System.Windows.Media.VisualTreeHelper.GetParent(up))
        {
            if (up is System.Windows.Controls.Primitives.ButtonBase) return;
        }

        file.IsSelected = !file.IsSelected;
        e.Handled = true;
    }

    // ------------------------------------------------------------- lazy previews

    private static void LoadPreview(object sender)
    {
        if (sender is FrameworkElement { DataContext: FileCardViewModel file })
            _ = file.LoadThumbnailAsync();
        else if (sender is FrameworkElement { DataContext: FolderCardViewModel folder })
            _ = folder.LoadPreviewAsync();
    }

    private void FileCard_Loaded(object sender, RoutedEventArgs e) => LoadPreview(sender);
    private void FolderCard_Loaded(object sender, RoutedEventArgs e) => LoadPreview(sender);

    private void ContinueCard_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        LoadPreview(sender);

    // ------------------------------------------------------------- menus

    /// <summary>Open a button's own context menu below it, bound to the same data as the button.</summary>
    private static void OpenMenuBelow(FrameworkElement owner, object? dataContext)
    {
        if (owner.ContextMenu is not { } menu) return;
        menu.DataContext = dataContext;
        menu.PlacementTarget = owner;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void LibraryMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) OpenMenuBelow(fe, DataContext);
    }

    private void SortMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) OpenMenuBelow(fe, DataContext);
    }

    /// <summary>Right-click on a card: the menu is shared markup, so hand it this card's data.</summary>
    private void Card_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } fe)
            menu.DataContext = fe.DataContext;
    }

    /// <summary>The "…" button on a card opens the same menu as right-click.</summary>
    private void CardMore_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement more) return;
        // The card is the button that holds this one.
        for (DependencyObject? up = System.Windows.Media.VisualTreeHelper.GetParent(more); up != null;
             up = System.Windows.Media.VisualTreeHelper.GetParent(up))
        {
            if (up is Button { ContextMenu: not null } card && !ReferenceEquals(card, more))
            {
                OpenMenuBelow(card, card.DataContext);
                return;
            }
        }
    }

    private void CardOpenInBackground_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: FileCardViewModel file })
            Library?.OpenFileInBackgroundCommand.Execute(file.Path);
    }

    // The menu items sit in a popup: their DataContext is the card the menu was opened for.
    private static FileCardViewModel? CardOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as FileCardViewModel;

    private void CardMenu_EditWord(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } file) Library?.OpenAsEditorCommand.Execute(file.Path);
    }

    private void CardMenu_PdfWord(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } file) Library?.OpenAsPdfCommand.Execute(file.Path);
    }

    private void CardMenu_Open(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } c) Library?.OpenFileCommand.Execute(c.Path);
    }

    private void CardMenu_OpenInBackground(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } c) Library?.OpenFileInBackgroundCommand.Execute(c.Path);
    }

    private void CardMenu_Pin(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } c) Library?.TogglePinCommand.Execute(c.Path);
    }

    private void CardMenu_Reveal(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } c) Library?.RevealInExplorerCommand.Execute(c.Path);
    }

    private void CardMenu_Delete(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } c) Library?.DeleteFileCommand.Execute(c.Path);
    }

    private void CardMenu_CopyPath(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } c) Library?.CopyPathCommand.Execute(c.Path);
    }
}
