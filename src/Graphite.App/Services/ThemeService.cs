using System.IO;
using System.Text.Json;
using System.Windows;
using Graphite.App.Interop;

namespace Graphite.App.Services;

public static class ThemeService
{
    private sealed class Settings
    {
        public bool DarkTheme { get; set; }
        public List<string> RecentFiles { get; set; } = new();

        /// <summary>Saved signature: strokes of x,y pairs in a normalized 0..1 box
        /// (Y scaled by the aspect ratio so shapes keep their proportions).</summary>
        public List<List<double[]>> Signature { get; set; } = new();
    }

    private static Settings _settings = new();

    public static bool IsDark => _settings.DarkTheme;
    public static IReadOnlyList<string> RecentFiles => _settings.RecentFiles;

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Graphite", "settings.json");

    public static void Initialize()
    {
        try
        {
            if (File.Exists(SettingsPath))
                _settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) ?? new Settings();
        }
        catch { _settings = new Settings(); }
        ApplyTheme(_settings.DarkTheme);
    }

    public static void ApplyTheme(bool dark)
    {
        _settings.DarkTheme = dark;
        var uri = new Uri($"Themes/Colors.{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative);
        Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = uri };

        ApplySystemAccent(dark);

        foreach (Window w in Application.Current.Windows)
        {
            if (!w.IsLoaded) continue;
            Backdrop.Apply(w, dark);
            BlipOpacity(w);
        }

        Save();
    }

    /// <summary>A very quick opacity dip so the instant brush swap across every
    /// DynamicResource-bound element reads as a soft cross-fade instead of a hard cut.</summary>
    private static void BlipOpacity(Window w)
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(1.0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(0.82, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70)))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        });
        anim.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(1.0, System.Windows.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        });
        w.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>Publish the Windows accent color as App.Accent* resources
    /// (direct entries override the merged theme dictionaries).</summary>
    private static void ApplySystemAccent(bool dark)
    {
        var color = ReadSystemAccent()
                    ?? (dark ? System.Windows.Media.Color.FromRgb(0x6C, 0xA0, 0xFF)
                             : System.Windows.Media.Color.FromRgb(0x3E, 0x6D, 0xB5));

        // Keep the accent readable against the theme.
        if (dark) color = Lighten(color, 0.25);

        var res = Application.Current.Resources;
        res["App.AccentSystemColor"] = color;
        res["App.AccentSystemBrush"] = Freeze(new System.Windows.Media.SolidColorBrush(color));
        res["App.AccentSoftBrush"] = Freeze(new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb((byte)(dark ? 0x3A : 0x2C), color.R, color.G, color.B)));
    }

    private static System.Windows.Media.Color? ReadSystemAccent()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (key?.GetValue("AccentColor") is int argb)
            {
                // Stored as ABGR.
                byte r = (byte)(argb & 0xFF), g = (byte)((argb >> 8) & 0xFF), b = (byte)((argb >> 16) & 0xFF);
                return System.Windows.Media.Color.FromRgb(r, g, b);
            }
        }
        catch { }
        return null;
    }

    private static System.Windows.Media.Color Lighten(System.Windows.Media.Color c, double amount) =>
        System.Windows.Media.Color.FromRgb(
            (byte)(c.R + (255 - c.R) * amount),
            (byte)(c.G + (255 - c.G) * amount),
            (byte)(c.B + (255 - c.B) * amount));

    private static System.Windows.Media.SolidColorBrush Freeze(System.Windows.Media.SolidColorBrush b)
    {
        b.Freeze();
        return b;
    }

    public static bool HasSignature => _settings.Signature.Count > 0;

    public static List<List<double[]>> GetSignature() => _settings.Signature;

    public static void SetSignature(List<List<double[]>> strokes)
    {
        _settings.Signature = strokes;
        Save();
    }

    public static void AddRecentFile(string path)
    {
        _settings.RecentFiles.Remove(path);
        _settings.RecentFiles.Insert(0, path);
        if (_settings.RecentFiles.Count > 10)
            _settings.RecentFiles.RemoveRange(10, _settings.RecentFiles.Count - 10);
        Save();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings));
        }
        catch { /* settings are best-effort */ }
    }
}
