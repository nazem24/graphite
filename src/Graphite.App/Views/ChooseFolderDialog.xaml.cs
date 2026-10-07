using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Graphite.App.Interop;
using Graphite.App.Services;
using Graphite.App.ViewModels;
using Microsoft.Win32;

namespace Graphite.App.Views;

/// <summary>"Choose your main folder": a small one-time picker that starts at OneDrive and shows,
/// before the person commits, how many subfolders and PDFs the choice holds.</summary>
public partial class ChooseFolderDialog : Window
{
    private readonly ChooseFolderViewModel _vm;
    private FolderChoice? _result;

    private ChooseFolderDialog(string? current, bool includeOffice)
    {
        InitializeComponent();
        _vm = new ChooseFolderViewModel(current, includeOffice);
        DataContext = _vm;
        Loaded += (_, _) => Backdrop.Apply(this, ThemeService.IsDark);
    }

    /// <returns>The chosen folder, or null when the dialog was cancelled.</returns>
    public static FolderChoice? Show(Window? owner, string? current, bool includeOffice)
    {
        var dlg = new ChooseFolderDialog(current, includeOffice);
        if (owner != null) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dlg.ShowDialog();
        return dlg._result;
    }

    private async void Chevron_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: FolderNode node })
        {
            _vm.Selected = node;
            await _vm.ToggleAsync(node);
        }
    }

    /// <summary>Double-click a row to open or close it.</summary>
    private async void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is FrameworkElement { DataContext: FolderNode node })
        {
            e.Handled = true;
            await _vm.ToggleAsync(node);
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Choose a folder", Multiselect = false };
        if (picker.ShowDialog(this) == true && !string.IsNullOrEmpty(picker.FolderName))
            _vm.AddCustomRoot(picker.FolderName);
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } node) return;
        _result = new FolderChoice(node.Path, _vm.IncludeOffice);
        Close();
    }
}
