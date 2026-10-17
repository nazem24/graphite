using System.Windows;
using System.Windows.Controls.Primitives;

namespace Graphite.App.Interop;

/// <summary>Lets a button inside a window template close its window without code-behind
/// (the dialogs' own close button, see the DialogWindow style).</summary>
public static class WindowActions
{
    public static readonly DependencyProperty CloseWindowProperty = DependencyProperty.RegisterAttached(
        "CloseWindow", typeof(bool), typeof(WindowActions), new PropertyMetadata(false, OnCloseWindowChanged));

    public static bool GetCloseWindow(DependencyObject d) => (bool)d.GetValue(CloseWindowProperty);
    public static void SetCloseWindow(DependencyObject d, bool value) => d.SetValue(CloseWindowProperty, value);

    private static void OnCloseWindowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button) return;
        button.Click -= OnClick;
        if (e.NewValue is true) button.Click += OnClick;
    }

    private static void OnClick(object sender, RoutedEventArgs e) =>
        Window.GetWindow((DependencyObject)sender)?.Close();
}
