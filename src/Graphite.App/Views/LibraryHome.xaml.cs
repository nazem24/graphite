using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Graphite.App.ViewModels;

namespace Graphite.App.Views;

/// <summary>The start screen: library rail, folders, files. Almost all behavior lives in
/// <see cref="LibraryViewModel"/>; this file only does what XAML cannot — lazy preview loading
/// when a card appears, and opening menus next to the button that asked for them.</summary>
public partial class LibraryHome : UserControl
{
    public LibraryHome() => InitializeComponent();

    private LibraryViewModel? Library => DataContext as LibraryViewModel;

    /// <summary>Ctrl+F while the start screen is showing: jump to its search box.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Library is { IsSearching: true } lib)
        {
            lib.ClearSearchCommand.Execute(null);
            e.Handled = true;
        }
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

    private void CardMenu_CopyPath(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is { } c) Library?.CopyPathCommand.Execute(c.Path);
    }
}
