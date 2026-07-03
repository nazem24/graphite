using System.Windows;
using Graphite.App.Interop;
using Graphite.App.Services;

namespace Graphite.App.Views;

public partial class PasswordDialog : Window
{
    public PasswordDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Backdrop.Apply(this, ThemeService.IsDark);
            ValueBox.Focus();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    public static string? Show(Window owner, string title, string prompt, string? error = null)
    {
        var dlg = new PasswordDialog { Owner = owner, Title = title };
        dlg.PromptText.Text = prompt;
        if (!string.IsNullOrEmpty(error))
        {
            dlg.ErrorText.Text = error;
            dlg.ErrorText.Visibility = Visibility.Visible;
        }
        return dlg.ShowDialog() == true ? dlg.ValueBox.Password : null;
    }
}
