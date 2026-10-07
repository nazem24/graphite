using System.IO;
using System.Text.Json;
using System.Windows;
using Graphite.App.Interop;

namespace Graphite.App.Services;

/// <summary>Where the person stopped in a document (shown as the start screen's progress bar
/// and used to reopen the file on that page).</summary>
public sealed class ReadingInfo
{
    public int Page { get; set; }
    public int PageCount { get; set; }
    public DateTime LastOpenedUtc { get; set; }
}

public static class ThemeService
{
    private sealed class Settings
    {
        public bool DarkTheme { get; set; }

        /// <summary>User turned on "Reduce motion" (palette: Toggle reduced motion).</summary>
        public bool ReduceMotion { get; set; }
        public List<string> RecentFiles { get; set; } = new();

        /// <summary>Saved signature: strokes of x,y pairs in a normalized 0..1 box
        /// (Y scaled by the aspect ratio so shapes keep their proportions).</summary>
        public List<List<double[]>> Signature { get; set; } = new();

        // ---- start-screen library ----
        /// <summary>The folders chosen as library roots (the first-run picker adds one).</summary>
        public List<string> LibraryRoots { get; set; } = new();
        /// <summary>The root currently shown on the start screen.</summary>
        public string? ActiveLibraryRoot { get; set; }
        /// <summary>Also list Word / Excel / PowerPoint files (converted to PDF when opened).</summary>
        public bool LibraryIncludeOffice { get; set; }
        public string LibrarySort { get; set; } = "Name";
        public bool LibraryGrid { get; set; } = true;
        public List<string> PinnedFiles { get; set; } = new();
        /// <summary>Where the person stopped reading, per file path.</summary>
        public Dictionary<string, ReadingInfo> Reading { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static Settings _settings = new();

    public static bool IsDark => _settings.DarkTheme;

    public static bool ReduceMotion => _settings.ReduceMotion;

    /// <summary>Turn the app's movement effects off / on, and remember the choice.</summary>
    public static void SetReduceMotion(bool reduce)
    {
        _settings.ReduceMotion = reduce;
        Motion.UserReduced = reduce;
        Save();
    }
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
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Settings load failed, using defaults: {ex.Message}");
            _settings = new Settings();
        }
        // The JSON round-trip drops the dictionary's comparer (and a hand-edited file may carry
        // nulls): rebuild the library collections so they are always usable.
        _settings.Reading = new Dictionary<string, ReadingInfo>(
            _settings.Reading ?? new(), StringComparer.OrdinalIgnoreCase);
        _settings.LibraryRoots ??= new();
        _settings.PinnedFiles ??= new();
        Motion.UserReduced = _settings.ReduceMotion;
        ApplyTheme(_settings.DarkTheme);
    }

    public static void ApplyTheme(bool dark)
    {
        _settings.DarkTheme = dark;
        var uri = new Uri($"Themes/Colors.{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative);
        // Find the colors dictionary by its Source instead of assuming index 0 —
        // the merged-dictionary order is an implementation detail of App.xaml.
        var merged = Application.Current.Resources.MergedDictionaries;
        int at = -1;
        for (int i = 0; i < merged.Count; i++)
            if (merged[i].Source?.OriginalString.Contains("Colors.") == true) { at = i; break; }
        if (at >= 0) merged[at] = new ResourceDictionary { Source = uri };
        else merged.Insert(0, new ResourceDictionary { Source = uri });

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
        if (!Motion.Enabled) return;
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
        if (_settings.RecentFiles.Count > 30)
            _settings.RecentFiles.RemoveRange(30, _settings.RecentFiles.Count - 30);
        Save();
    }

    // ------------------------------------------------------------- start-screen library

    public static IReadOnlyList<string> LibraryRoots => _settings.LibraryRoots;
    public static string? ActiveLibraryRoot => _settings.ActiveLibraryRoot;
    public static bool LibraryIncludeOffice => _settings.LibraryIncludeOffice;
    public static string LibrarySort => _settings.LibrarySort;
    public static bool LibraryGrid => _settings.LibraryGrid;
    public static IReadOnlyList<string> PinnedFiles => _settings.PinnedFiles;

    public static void SetLibrary(IEnumerable<string> roots, string? active, bool includeOffice)
    {
        _settings.LibraryRoots = roots.ToList();
        _settings.ActiveLibraryRoot = active;
        _settings.LibraryIncludeOffice = includeOffice;
        Save();
    }

    public static void SetLibraryView(string sort, bool grid)
    {
        _settings.LibrarySort = sort;
        _settings.LibraryGrid = grid;
        Save();
    }

    public static bool IsPinned(string path) =>
        _settings.PinnedFiles.Contains(path, StringComparer.OrdinalIgnoreCase);

    /// <summary>Pin / unpin a file; returns the new state.</summary>
    public static bool TogglePinned(string path)
    {
        int at = _settings.PinnedFiles.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        bool pinned = at < 0;
        if (pinned) _settings.PinnedFiles.Insert(0, path);
        else _settings.PinnedFiles.RemoveAt(at);
        Save();
        return pinned;
    }

    public static ReadingInfo? GetReading(string path) =>
        _settings.Reading.TryGetValue(path, out var info) ? info : null;

    /// <summary>Remember the page a file was left on (also stamps "last opened").</summary>
    public static void SetReading(string path, int page, int pageCount)
    {
        if (string.IsNullOrEmpty(path) || pageCount <= 0) return;
        _settings.Reading[path] = new ReadingInfo
        {
            Page = Math.Clamp(page, 0, pageCount - 1),
            PageCount = pageCount,
            LastOpenedUtc = DateTime.UtcNow,
        };
        // Keep the map bounded: forget the files opened longest ago.
        if (_settings.Reading.Count > 400)
            foreach (var old in _settings.Reading.OrderBy(kv => kv.Value.LastOpenedUtc).Take(_settings.Reading.Count - 300).Select(kv => kv.Key).ToList())
                _settings.Reading.Remove(old);
        Save();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            // Write-then-rename so a crash or power loss mid-write can't leave a truncated
            // settings.json (which silently reset the theme, recent files and signature).
            string tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_settings));
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Settings save failed: {ex.Message}"); }
    }
}
