using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Graphite.App.Interop;

/// <summary>
/// Applies dark/light title bars and the Windows 11 Mica backdrop. Mica is enabled
/// for the main window (the one with custom WindowChrome); dialogs keep a flat,
/// theme-matched background. On Windows 10 (no Mica) everything falls back to the
/// flat theme background.
///
/// Contrast note: Mica tints itself from the desktop wallpaper via the SYSTEM theme,
/// which can diverge from the app's own light/dark toggle. To keep text readable
/// regardless, the app's surfaces (viewer card, glass panels, cards) keep their own
/// translucent theme brushes on top of Mica — Mica only shows through the margins
/// and between panels, where no text sits directly on it.
/// </summary>
public static class Backdrop
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int BACKDROP_NONE = 1;
    private const int BACKDROP_MICA = 2; // DWMSBT_MAINWINDOW

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

        // Mica only for the main window (custom WindowChrome, glass frame extended
        // into the client area). Dialogs don't have the chrome recipe Mica needs and
        // keep the flat theme background.
        bool useMica = IsMicaSupported && WindowChrome.GetWindowChrome(window) != null;

        if (useMica)
        {
            int backdrop = BACKDROP_MICA;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

            // Mica is painted by DWM behind the window — WPF must not cover it with an
            // opaque background, or the effect is invisible.
            if (PresentationSource.FromVisual(window) is HwndSource source)
                source.CompositionTarget.BackgroundColor = Colors.Transparent;
            window.Background = Brushes.Transparent;
            return;
        }

        // Flat fallback (Windows 10, or dialogs): solid theme-matched background.
        int none = BACKDROP_NONE;
        if (IsMicaSupported)
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));

        var bg = darkMode ? Color.FromRgb(0x1F, 0x1F, 0x21) : Color.FromRgb(0xFF, 0xFF, 0xFF);
        var text = darkMode ? Color.FromRgb(0xEC, 0xEC, 0xE8) : Color.FromRgb(0x1B, 0x1B, 0x1A);
        uint captionColor = ToColorRef(bg);
        uint textColor = ToColorRef(text);
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(uint));
        DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(uint));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref captionColor, sizeof(uint));

        if (PresentationSource.FromVisual(window) is HwndSource opaqueSource)
            opaqueSource.CompositionTarget.BackgroundColor = bg;
        window.SetResourceReference(Window.BackgroundProperty, "App.BackgroundBrush");
    }

    private static uint ToColorRef(Color c) => (uint)((c.B << 16) | (c.G << 8) | c.R);
}
