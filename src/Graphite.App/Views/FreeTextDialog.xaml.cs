using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Graphite.App.Interop;
using Graphite.App.Services;

namespace Graphite.App.Views;

/// <summary>Add/edit dialog for the Free Text (text box) tool — text, size, font family,
/// bold/italic/underline, text color, background fill and border.</summary>
public partial class FreeTextDialog : Window
{
    private static readonly double[] Sizes = { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48 };

    private static readonly string[] Fonts =
    {
        "Segoe UI", "Arial", "Calibri", "Times New Roman", "Georgia", "Courier New", "Verdana",
    };

    private static readonly (string Name, string Hex)[] Palette =
    {
        ("Yellow", "#F2C744"), ("Orange", "#E8853C"), ("Red", "#C24444"), ("Pink", "#C75E9A"),
        ("Blue", "#3E6DB5"), ("Teal", "#2E8C83"), ("Green", "#4E9C51"), ("Purple", "#7B5EC7"),
        ("Graphite", "#3B3B38"), ("Black", "#111111"), ("White", "#FFFFFF"),
    };

    private string _textColor = "#1B1B1A";
    private string? _fillColor;
    private string? _borderColor;

    public FreeTextDialog()
    {
        InitializeComponent();
        foreach (var f in Fonts) FontBox.Items.Add(f);
        foreach (double s in Sizes) SizeBox.Items.Add($"{s:0.#} pt");
        Loaded += (_, _) =>
        {
            Backdrop.Apply(this, ThemeService.IsDark);
            ValueBox.SelectAll();
            ValueBox.Focus();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private static Brush SwatchBrush(string? hex) =>
        hex == null ? Brushes.Transparent : new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

    private void UpdateSwatches()
    {
        TextColorSwatch.Background = SwatchBrush(_textColor);
        FillColorSwatch.Background = SwatchBrush(_fillColor);
        BorderColorSwatch.Background = SwatchBrush(_borderColor);
    }

    private ContextMenu BuildPalette(Action<string?> select, bool allowNone)
    {
        var menu = new ContextMenu();
        if (allowNone)
        {
            var none = new MenuItem { Header = "None" };
            none.Click += (_, _) => select(null);
            menu.Items.Add(none);
        }
        foreach (var (name, hex) in Palette)
        {
            var swatch = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
            };
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(swatch);
            panel.Children.Add(new TextBlock { Text = name, Margin = new Thickness(8, 0, 0, 0) });
            var item = new MenuItem { Header = panel };
            item.Click += (_, _) => select(hex);
            menu.Items.Add(item);
        }
        return menu;
    }

    private static void OpenMenu(ContextMenu menu, UIElement target)
    {
        menu.PlacementTarget = target;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void TextColor_Click(object sender, RoutedEventArgs e)
    {
        var menu = BuildPalette(hex => { _textColor = hex ?? _textColor; UpdateSwatches(); }, allowNone: false);
        OpenMenu(menu, (Button)sender);
    }

    private void FillColor_Click(object sender, RoutedEventArgs e)
    {
        var menu = BuildPalette(hex => { _fillColor = hex; UpdateSwatches(); }, allowNone: true);
        OpenMenu(menu, (Button)sender);
    }

    private void BorderColor_Click(object sender, RoutedEventArgs e)
    {
        var menu = BuildPalette(hex => { _borderColor = hex; UpdateSwatches(); }, allowNone: true);
        OpenMenu(menu, (Button)sender);
    }

    public sealed record Result(
        string Text, double? Size, string FontFamily, bool Bold, bool Italic, bool Underline,
        string TextColorHex, string? FillColorHex, string? BorderColorHex);

    /// <summary>Returns the composed result, or null if the dialog was cancelled. The
    /// initial* parameters let this same dialog serve both "add text" and "edit text".</summary>
    public static Result? Show(Window owner, string title, string prompt, string initialColorHex,
        string initial = "", double? initialSize = null, string initialFontFamily = "Segoe UI",
        bool initialBold = false, bool initialItalic = false, bool initialUnderline = false,
        string? initialFillColorHex = null, string? initialBorderColorHex = null)
    {
        var dlg = new FreeTextDialog { Owner = owner, Title = title };
        dlg.PromptText.Text = prompt;
        dlg.ValueBox.Text = initial;
        dlg._textColor = initialColorHex;
        dlg._fillColor = initialFillColorHex;
        dlg._borderColor = initialBorderColorHex;
        dlg.UpdateSwatches();

        int fontIdx = Array.IndexOf(Fonts, initialFontFamily);
        dlg.FontBox.SelectedIndex = fontIdx >= 0 ? fontIdx : 0;

        dlg.BoldToggle.IsChecked = initialBold;
        dlg.ItalicToggle.IsChecked = initialItalic;
        dlg.UnderlineToggle.IsChecked = initialUnderline;

        dlg.SizeBox.SelectedIndex = Array.IndexOf(Sizes, 12.0);
        if (initialSize is { } init)
        {
            int idx = Array.IndexOf(Sizes, init);
            if (idx >= 0) dlg.SizeBox.SelectedIndex = idx;
        }

        if (dlg.ShowDialog() != true) return null;

        double? size = dlg.SizeBox.SelectedIndex >= 0 ? Sizes[dlg.SizeBox.SelectedIndex] : null;
        return new Result(
            dlg.ValueBox.Text,
            size,
            dlg.FontBox.SelectedItem as string ?? "Segoe UI",
            dlg.BoldToggle.IsChecked == true,
            dlg.ItalicToggle.IsChecked == true,
            dlg.UnderlineToggle.IsChecked == true,
            dlg._textColor,
            dlg._fillColor,
            dlg._borderColor);
    }
}
