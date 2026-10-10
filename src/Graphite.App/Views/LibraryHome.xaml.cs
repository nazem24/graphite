using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Graphite.App.Services;
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
        DataContextChanged += LibraryHome_DataContextChanged;
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
    }

    // Thumbnails are asked for when the UI has nothing better to do, so a page's cards appear (and animate in)
    // first and their pictures follow.
    private void FileCard_Loaded(object sender, RoutedEventArgs e) =>
        Dispatcher.InvokeAsync(() => LoadPreview(sender), DispatcherPriority.ContextIdle);

    // Resting the pointer on a folder card reads that folder in the background, so opening it is instant.
    private DispatcherTimer? _hoverTimer;
    private string? _hoverPath;

    private void FolderTile_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: FolderCardViewModel folder }) return;
        _hoverPath = folder.Path;
        _hoverTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _hoverTimer.Tick -= HoverTimer_Tick;
        _hoverTimer.Tick += HoverTimer_Tick;
        _hoverTimer.Stop();
        _hoverTimer.Start();
    }

    private void FolderTile_MouseLeave(object sender, MouseEventArgs e) => _hoverTimer?.Stop();

    private void HoverTimer_Tick(object? sender, EventArgs e)
    {
        _hoverTimer?.Stop();
        Library?.Prefetch(_hoverPath);
    }

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

    // ------------------------------------------------------------- folder cards

    private static FolderCardViewModel? FolderOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as FolderCardViewModel;

    /// <summary>The card a menu item's menu was opened on (right-click, or the "…" button).</summary>
    private static FrameworkElement? MenuTarget(object sender) =>
        sender is MenuItem item && ItemsControl.ItemsControlFromItemContainer(item) is ContextMenu { PlacementTarget: FrameworkElement target }
            ? target
            : null;

    private void FolderMenu_Open(object sender, RoutedEventArgs e)
    {
        if (FolderOf(sender) is { } folder) Library?.ShowFolderCommand.Execute(folder.Path);
    }

    private void FolderMenu_Colour(object sender, RoutedEventArgs e)
    {
        if (FolderOf(sender) is not { } folder || MenuTarget(sender) is not { } target) return;
        // The menu is still closing while this runs; a popup opened right now would be closed again by it.
        Dispatcher.InvokeAsync(() => OpenColorPicker(folder, target), DispatcherPriority.Background);
    }

    private void FolderMenu_ResetColour(object sender, RoutedEventArgs e)
    {
        if (FolderOf(sender) is { } folder) Library?.SetFolderColor(folder, null);
    }

    private void FolderMenu_Reveal(object sender, RoutedEventArgs e)
    {
        if (FolderOf(sender) is { } folder) Library?.RevealInExplorerCommand.Execute(folder.Path);
    }

    private void FolderMenu_CopyPath(object sender, RoutedEventArgs e)
    {
        if (FolderOf(sender) is { } folder) Library?.CopyPathCommand.Execute(folder.Path);
    }

    // ------------------------------------------------------------- folder colour picker

    private FolderCardViewModel? _colorFolder;

    private void OpenColorPicker(FolderCardViewModel folder, FrameworkElement target)
    {
        _colorFolder = folder;
        ColorPopup.PlacementTarget = target;
        RefreshColorPicker();
        ColorPopup.IsOpen = true;
    }

    /// <summary>Show the folder's current colour: tick its swatch (if it is one) and fill the hex box.</summary>
    private void RefreshColorPicker()
    {
        if (_colorFolder == null) return;
        string current = _colorFolder.ColorHex;
        SwatchList.ItemsSource = FolderPalette.Presets
            .Select(p => new FolderSwatch(p.Name, p.Hex, string.Equals(p.Hex, current, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        HexBox.Text = current;
    }

    private void ColorPopup_Opened(object? sender, EventArgs e) => HexBox.CaretIndex = HexBox.Text.Length;

    /// <summary>Colours apply to the folder as soon as they are chosen, so the card itself is the preview.</summary>
    private void ApplyColor(string? hex)
    {
        if (_colorFolder == null) return;
        Library?.SetFolderColor(_colorFolder, hex);
        RefreshColorPicker();
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string hex) ApplyColor(hex);
    }

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e) =>
        HexPreview.Fill = FolderPalette.TryParse(HexBox.Text, out var c) ? new SolidColorBrush(c) : Brushes.Transparent;

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ApplyHexBox();
    }

    private void HexApply_Click(object sender, RoutedEventArgs e) => ApplyHexBox();

    private void ApplyHexBox()
    {
        if (FolderPalette.TryParse(HexBox.Text, out var c)) ApplyColor(FolderPalette.ToHex(c));
        else HexBox.SelectAll();
    }

    private void ColorReset_Click(object sender, RoutedEventArgs e) => ApplyColor(null);

    private void ColorAll_Click(object sender, RoutedEventArgs e)
    {
        if (_colorFolder == null) return;
        // What the box says if it is a colour, else the folder's own.
        string hex = FolderPalette.TryParse(HexBox.Text, out var c) ? FolderPalette.ToHex(c) : _colorFolder.ColorHex;
        Library?.SetAllFolderColors(hex);
        ColorPopup.IsOpen = false;
    }

    // ------------------------------------------------------------- page transition

    // Opening a folder (or going back) happens in three beats: the page you are on fades out (a quick,
    // free GPU fade), then, while nothing is visible, the new page is switched in and built, then it slides
    // and fades in from the side you are heading to. Going deeper moves left, coming back moves right, and
    // a sideways change (recent, pinned) fades and lifts. Because the heavy work (building cards) happens
    // while the page is invisible, no animation frame ever has to wait for it. Skipped when motion is reduced.

    private static readonly IEasingFunction QuintOut = Freeze(new QuinticEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction QuadIn = Freeze(new QuadraticEase { EasingMode = EasingMode.EaseIn });

    private static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    private LibraryViewModel? _watched;
    private int _gen;                        // bumped whenever a transition starts, ends or is cut short
    private int _direction;                  // +1 deeper, -1 back, 0 sideways
    private bool _awaitingPage;              // the page has been swapped and is still loading (it is invisible)
    private bool _playQueued;
    private Action? _pendingApply;           // the page change that waits for the fade-out to finish
    private DispatcherTimer? _failsafe;

    private void LibraryHome_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_watched != null)
        {
            _watched.NavigationStarting -= OnNavigationStarting;
            _watched.PageContentShown -= QueuePageTransition;
            _watched.PropertyChanged -= OnLibraryPropertyChanged;
        }
        _watched = e.NewValue as LibraryViewModel;
        if (_watched != null)
        {
            _watched.NavigationStarting += OnNavigationStarting;
            _watched.PageContentShown += QueuePageTransition;
            _watched.PropertyChanged += OnLibraryPropertyChanged;
        }
    }

    private static void Animate(IAnimatable target, DependencyProperty property, double from, double to,
        double ms, IEasingFunction ease) =>
        target.BeginAnimation(property, new DoubleAnimation(from, to, Motion.Ms(ms))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        });

    /// <summary>Step 1: the page you are leaving fades out and drifts a little. Nothing is photographed and
    /// nothing is built yet, so these frames cost nothing. Step 2 (<see cref="SwapPage"/>) happens once it is
    /// invisible.</summary>
    private void OnNavigationStarting(int direction, Action apply)
    {
        EndTransition();   // an earlier change still waiting for its fade-out is applied right now
        if (!Motion.Enabled || !IsLoaded || !IsVisible || PageHost.ActualWidth < 16 || PageHost.ActualHeight < 16)
        {
            apply();
            return;
        }

        int gen = ++_gen;
        _direction = direction;
        _pendingApply = apply;

        // A page that is one cached picture fades and moves on the graphics card alone.
        PageContent.CacheMode = new BitmapCache();
        var move = (TranslateTransform)PageContent.RenderTransform;
        Animate(PageContent, OpacityProperty, 1, 0, 100, QuadIn);
        Animate(move, TranslateTransform.XProperty, 0, -direction * 12, 100, QuadIn);

        // a few milliseconds after the fade has finished, so its last frame has been drawn
        Motion.After(120, () =>
        {
            if (gen == _gen) SwapPage();
        });
    }

    /// <summary>Step 2: with the page invisible, switch to the new one and let it load.</summary>
    private void SwapPage()
    {
        var apply = _pendingApply;
        _pendingApply = null;
        if (apply == null) return;

        _awaitingPage = true;
        _playQueued = false;

        // A folder that is very slow to read must not leave the page blank for long.
        _failsafe ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _failsafe.Tick -= Failsafe_Tick;
        _failsafe.Tick += Failsafe_Tick;
        _failsafe.Start();

        // Pages that are ready at once (home, recent, pinned, a folder seen before) announce it while this runs.
        apply();
    }

    private void Failsafe_Tick(object? sender, EventArgs e)
    {
        _failsafe?.Stop();
        if (_awaitingPage) PlayPageTransition();
    }

    private void OnLibraryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.IsLoading) && _awaitingPage && _watched is { IsLoading: false })
            QueuePageTransition();
    }

    /// <summary>The new page has its content. Wait until it has been laid out (Input priority runs after
    /// layout and render work) so the movement starts on a free UI thread.</summary>
    private void QueuePageTransition()
    {
        if (!_awaitingPage || _playQueued) return;
        _playQueued = true;
        int gen = _gen;
        Dispatcher.InvokeAsync(() =>
        {
            if (gen == _gen && _awaitingPage) PlayPageTransition();
        }, DispatcherPriority.Input);
    }

    /// <summary>Step 3: the new page arrives from the side you are heading to and settles with a long, soft stop.</summary>
    private void PlayPageTransition()
    {
        _failsafe?.Stop();
        _awaitingPage = false;
        int gen = _gen;
        double dir = _direction;

        if (PageContent.CacheMode == null) PageContent.CacheMode = new BitmapCache();
        var move = (TranslateTransform)PageContent.RenderTransform;
        Animate(PageContent, OpacityProperty, 0, 1, 240, QuintOut);
        Animate(move, TranslateTransform.XProperty, dir * 30, 0, 400, QuintOut);
        Animate(move, TranslateTransform.YProperty, dir == 0 ? 12 : 0, 0, 400, QuintOut);

        // once everything has come to rest, the page goes back to being live content
        Motion.After(440, () =>
        {
            if (gen == _gen) EndTransition();
        });
    }

    /// <summary>Put everything back to rest: the page fully shown and live, no animation running. A page
    /// change still waiting for the fade-out is applied here, so a navigation is never lost.</summary>
    private void EndTransition()
    {
        var pending = _pendingApply;
        _pendingApply = null;

        _gen++;
        _awaitingPage = false;
        _playQueued = false;
        _failsafe?.Stop();

        PageContent.BeginAnimation(OpacityProperty, null);
        PageContent.Opacity = 1;
        PageContent.CacheMode = null;
        var move = (TranslateTransform)PageContent.RenderTransform;
        move.BeginAnimation(TranslateTransform.XProperty, null);
        move.BeginAnimation(TranslateTransform.YProperty, null);
        move.X = 0;
        move.Y = 0;

        pending?.Invoke();
    }
}
