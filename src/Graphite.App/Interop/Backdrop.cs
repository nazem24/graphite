using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using Graphite.App.Services;

namespace Graphite.App.Interop;

/// <summary>
/// Applies dark/light title bars and the Windows 11 window material: Mica (the default) or,
/// for the Blur / Frosted / Glass materials, Acrylic (a real blur of the desktop behind the
/// window; the panels' own translucency is what tells those three apart, see Themes/Materials).
/// The backdrop is enabled for every window that has a WindowChrome: the main window and, through the
/// DialogWindow style, all dialogs. A window without one keeps a flat, theme-matched background. On Windows 10 (no Mica) everything falls back to the
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
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int BACKDROP_NONE = 1;
    private const int BACKDROP_MICA = 2; // DWMSBT_MAINWINDOW
    private const int BACKDROP_ACRYLIC = 3; // DWMSBT_TRANSIENTWINDOW

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

    public static bool IsMicaSupported =>
        Environment.OSVersion.Version.Build >= 22621;

    /// <summary>False when the person turned off "Transparency effects" in Windows: Mica and
    /// Acrylic then paint as a flat color.</summary>
    public static bool TransparencyEnabled
    {
        get
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return key?.GetValue("EnableTransparency") is not int v || v != 0;
            }
            catch { return true; }
        }
    }

    public static void Apply(Window window, bool darkMode)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();

        int dark = darkMode ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        // Windows 11 rounds a window's corners only when it has a resize border. The dialogs can't be
        // resized, so they came out square next to the main window; ask for the same rounding outright.
        // (Ignored on Windows 10, which has no rounded windows.)
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            int round = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }

        // Mica / Acrylic only for windows with a WindowChrome (glass frame extended into the
        // client area): the main window and the dialogs (DialogWindow style).
        // Ink and Paper paint the whole window themselves: no backdrop, a flat colour of their own.
        string material = ThemeService.Material;
        bool solid = ThemeService.IsSolidMaterial(material);
        bool useMica = IsMicaSupported && WindowChrome.GetWindowChrome(window) != null && !solid;

        if (useMica)
        {
            int backdrop = material == "Mica" ? BACKDROP_MICA : BACKDROP_ACRYLIC;
            DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

            // back to Windows' own caption colours, in case a solid material set its own a moment ago
            uint standard = 0xFFFFFFFF;   // DWMWA_COLOR_DEFAULT
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref standard, sizeof(uint));
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref standard, sizeof(uint));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref standard, sizeof(uint));

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

        var bg = ThemeService.SolidWindowColor(material, darkMode)
                 ?? (darkMode ? Color.FromRgb(0x1F, 0x1F, 0x21) : Color.FromRgb(0xFF, 0xFF, 0xFF));
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
