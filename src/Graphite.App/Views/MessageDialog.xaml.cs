using System.Windows;
using System.Windows.Media;
using Graphite.App.Interop;
using Graphite.App.Services;

namespace Graphite.App.Views;

public enum DialogButtons { OK, YesNo, YesNoCancel }
public enum DialogIcon { Info, Warning, Error }

/// <summary>Themed replacement for the native <see cref="MessageBox"/> — same OK / Yes-No /
/// Yes-No-Cancel shapes, but painted with the app's own theme (dark title bar, flat themed
/// background, in-house icon set) instead of popping a plain system dialog that ignores the
/// app's light/dark setting entirely.</summary>
public partial class MessageDialog : Window
{
    private MessageBoxResult _result = MessageBoxResult.None;

    public MessageDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => Backdrop.Apply(this, ThemeService.IsDark);
    }

    private void Ok_Click(object sender, RoutedEventArgs e) { _result = MessageBoxResult.OK; Close(); }
    private void Yes_Click(object sender, RoutedEventArgs e) { _result = MessageBoxResult.Yes; Close(); }
    private void No_Click(object sender, RoutedEventArgs e) { _result = MessageBoxResult.No; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _result = MessageBoxResult.Cancel; Close(); }

    /// <param name="owner">May be null (e.g. an exception raised before the main window exists);
    /// the dialog centers on screen instead of over a nonexistent owner in that case.</param>
    public static MessageBoxResult Show(Window? owner, string message, string title,
        DialogButtons buttons = DialogButtons.OK, DialogIcon icon = DialogIcon.Info)
    {
        var dlg = new MessageDialog { Title = title };
        if (owner != null) dlg.Owner = owner;
        else dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dlg.MessageText.Text = message;
        dlg.ApplyIcon(icon);
        dlg.ApplyButtons(buttons);
        dlg.ShowDialog();
        return dlg._result;
    }

    private void ApplyIcon(DialogIcon icon)
    {
        var (resourceKey, hex) = icon switch
        {
            DialogIcon.Warning => ("Icon.Warning", "#E8A33C"),
            DialogIcon.Error => ("Icon.Error", "#C24444"),
            _ => ("Icon.Info", "#6CA0FF"),
        };
        IconGlyph.Data = (Geometry)FindResource(resourceKey);
        var color = (Color)ColorConverter.ConvertFromString(hex);
        IconGlyph.Stroke = new SolidColorBrush(color);
        IconBadge.Background = new SolidColorBrush(Color.FromArgb(38, color.R, color.G, color.B));
    }

    private void ApplyButtons(DialogButtons buttons)
    {
        OkBtn.Visibility = buttons == DialogButtons.OK ? Visibility.Visible : Visibility.Collapsed;
        YesBtn.Visibility = buttons != DialogButtons.OK ? Visibility.Visible : Visibility.Collapsed;
        NoBtn.Visibility = buttons != DialogButtons.OK ? Visibility.Visible : Visibility.Collapsed;
        CancelBtn.Visibility = buttons == DialogButtons.YesNoCancel ? Visibility.Visible : Visibility.Collapsed;

        // Wire Enter/Escape to sensible buttons for whichever set is actually visible.
        if (buttons == DialogButtons.OK)
        {
            OkBtn.IsDefault = true;
            OkBtn.IsCancel = true;
        }
        else
        {
            YesBtn.IsDefault = true;
            if (buttons == DialogButtons.YesNoCancel) CancelBtn.IsCancel = true;
            else NoBtn.IsCancel = true;
        }
    }
}
