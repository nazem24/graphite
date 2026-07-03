using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Graphite.App.Interop;

/// <summary>
/// Applies dark/light title bars and a flat, theme-matched window background.
/// (Previously used the Windows 11 Mica system backdrop for a "smoky glass" look, but
/// Mica tints itself from the desktop wallpaper via the system theme — independent of
/// this app's own light/dark toggle — which made the glass panels randomly low-contrast
/// and hard to read. A flat background keeps colors fully controlled by our own theme
/// resources, which are already tuned for contrast in both themes.)
/// </summary>
public static class Backdrop
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int BACKDROP_NONE = 1;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

    public static bool IsMicaSupported =>
        Environment.OSVersion.Version.Build >= 22621;

    public static void Apply(Window window, bool darkMode)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();

        int dark = darkMode ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        // Paint the native title bar the exact same solid color as the app background,
        // instead of letting the Mica tint show through as a lighter band up top.
        var bg = darkMode ? Color.FromRgb(0x1F, 0x1F, 0x21) : Color.FromRgb(0xFF, 0xFF, 0xFF);
        var text = darkMode ? Color.FromRgb(0xEC, 0xEC, 0xE8) : Color.FromRgb(0x1B, 0x1B, 0x1A);
        uint captionColor = ToColorRef(bg);
        uint textColor = ToColorRef(text);
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(uint));
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(uint));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref captionColor, sizeof(uint));

        // Always use a flat, opaque background painted from our own theme resources.
        // Mica is intentionally not used: it derives its color from the desktop wallpaper
        // and system theme rather than from this app's light/dark setting, which was
        // making the glass panels' text unpredictably low-contrast in both themes.
        if (IsMicaSupported)
        {
            int none = BACKDROP_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
        }

        if (PresentationSource.FromVisual(window) is HwndSource opaqueSource)
            opaqueSource.CompositionTarget.BackgroundColor = bg;
        window.SetResourceReference(Window.BackgroundProperty, "App.BackgroundBrush");
    }

    private static uint ToColorRef(Color c) => (uint)((c.B << 16) | (c.G << 8) | c.R);
}
