using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Graphite.App.Services;
using Graphite.App.ViewModels;

namespace Graphite.App.Views;

/// <summary>The open Canvas sidebar. Its only code is the term menu, built when it opens (the terms change
/// with the course list).</summary>
public partial class CanvasRail : UserControl
{
    public CanvasRail() => InitializeComponent();

    private void TermMenu_Click(object sender, RoutedEventArgs e) =>
        CanvasMenus.OpenTermMenu(sender as FrameworkElement, DataContext as CanvasViewModel);
}

/// <summary>Menus shared by the Canvas sidebar (both layouts) and the Canvas page.</summary>
internal static class CanvasMenus
{
    /// <summary>Every term the course list knows (the one on show ticked), then Canvas settings.</summary>
    public static void OpenTermMenu(FrameworkElement? owner, CanvasViewModel? canvas)
    {
        if (owner == null || canvas == null) return;
        if (!canvas.IsConnected) { canvas.OpenSettingsCommand.Execute(null); return; }
        var menu = owner.ContextMenu ?? new ContextMenu();
        menu.Items.Clear();
        foreach (var term in canvas.Terms)
        {
            menu.Items.Add(new MenuItem
            {
                Header = term.Name,
                IsCheckable = true,
                IsChecked = term.Name == canvas.TermName,
                Command = canvas.SelectTermCommand,
                CommandParameter = term.Id,
            });
        }
        if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "No terms loaded yet", IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Refresh", Command = canvas.RefreshCommand });
        menu.Items.Add(new MenuItem { Header = "Open Canvas in the browser", Command = canvas.OpenInCanvasCommand });
        menu.Items.Add(new MenuItem { Header = "Canvas settings…", Command = canvas.OpenSettingsCommand });
        Open(owner, menu);
    }

    /// <summary>The download menu of a file: Quick download, Save as…, Save to course folder.</summary>
    public static void OpenDownloadMenu(FrameworkElement owner, CanvasViewModel canvas, CanvasRowVm row)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem
        {
            Header = "Quick download",
            ToolTip = "Straight into your Downloads folder",
            Command = canvas.QuickDownloadCommand,
            CommandParameter = row,
        });
        menu.Items.Add(new MenuItem { Header = "Save as…", Command = canvas.SaveFileAsCommand, CommandParameter = row });
        menu.Items.Add(new MenuItem
        {
            Header = "Save to course folder",
            ToolTip = row.CanSaveToFolder ? "Into the folder this course is linked to" : "Link the course to a folder first",
            Command = canvas.SaveToCourseFolderCommand,
            CommandParameter = row,
            IsEnabled = row.CanSaveToFolder,
        });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Open in the browser", Command = canvas.OpenRowInBrowserCommand, CommandParameter = row });
        menu.Items.Add(new MenuItem { Header = row.IsPinned ? "Unpin" : "Pin", Command = canvas.TogglePinCommand, CommandParameter = row });
        owner.ContextMenu = menu;
        Open(owner, menu);
    }

    /// <summary>The linked folder's menu on a course page.</summary>
    public static void OpenFolderMenu(FrameworkElement owner, CanvasViewModel canvas)
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "Open folder", Command = canvas.OpenCourseFolderCommand });
        menu.Items.Add(new MenuItem { Header = "Link a different folder…", Command = canvas.LinkFolderCommand });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "Unlink", Command = canvas.UnlinkFolderCommand });
        owner.ContextMenu = menu;
        Open(owner, menu);
    }

    private static void Open(FrameworkElement owner, ContextMenu menu)
    {
        MenuPlacement.Show(menu, owner);
    }
}
