using System.Windows;

namespace Graphite.App.Views;

public partial class InputDialog : Window
{
    public InputDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => { ValueBox.SelectAll(); ValueBox.Focus(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    public static string? Show(Window owner, string title, string prompt, string initial = "")
    {
        var dlg = new InputDialog { Owner = owner, Title = title };
        dlg.PromptText.Text = prompt;
        dlg.ValueBox.Text = initial;
        return dlg.ShowDialog() == true ? dlg.ValueBox.Text : null;
    }
}
