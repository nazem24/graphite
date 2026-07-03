using System.Windows;
using Graphite.App.Interop;
using Graphite.App.Services;

namespace Graphite.App.Views;

public partial class EditTextDialog : Window
{
    private static readonly double[] Sizes = { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48 };

    public EditTextDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Backdrop.Apply(this, ThemeService.IsDark);
            ValueBox.SelectAll();
            ValueBox.Focus();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>Returns (text, size) or null when cancelled. Size is null when "Auto".</summary>
    public static (string Text, double? Size)? Show(Window owner, string title, string prompt,
        string initial = "", double? autoSize = null, double? initialSize = null)
    {
        var dlg = new EditTextDialog { Owner = owner, Title = title };
        dlg.PromptText.Text = prompt;
        dlg.ValueBox.Text = initial;

        if (autoSize is { } auto)
            dlg.SizeBox.Items.Add($"Auto ({auto:0.#} pt)");
        foreach (double s in Sizes)
            dlg.SizeBox.Items.Add($"{s:0.#} pt");

        dlg.SizeBox.SelectedIndex = 0;
        if (initialSize is { } init)
        {
            int idx = Array.IndexOf(Sizes, init);
            if (idx >= 0) dlg.SizeBox.SelectedIndex = idx + (autoSize != null ? 1 : 0);
        }

        if (dlg.ShowDialog() != true) return null;

        double? size = null;
        int sel = dlg.SizeBox.SelectedIndex;
        if (autoSize != null && sel == 0) size = null;
        else if (autoSize != null) size = Sizes[sel - 1];
        else if (sel >= 0) size = Sizes[sel];
        return (dlg.ValueBox.Text, size);
    }
}
