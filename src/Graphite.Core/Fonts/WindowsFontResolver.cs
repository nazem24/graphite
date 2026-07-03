using System.Collections.Concurrent;
using PdfSharp.Fonts;

namespace Graphite.Core.Fonts;

/// <summary>
/// PdfSharp on modern .NET has no default font resolver, so anything that
/// creates an XFont (text editing, OCR text layer) needs this. Resolves
/// common families straight from the Windows font folders.
/// </summary>
public sealed class WindowsFontResolver : IFontResolver
{
    private readonly ConcurrentDictionary<string, string> _faceToFile = new();
    private readonly ConcurrentDictionary<string, byte[]> _bytes = new();

    private static readonly string[] FontDirs =
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Fonts"), // per-user installed fonts
    };

    private static readonly Dictionary<string, (string R, string B, string I, string BI)> Families =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["segoe ui"] = ("segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf"),
            ["arial"] = ("arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf"),
            ["times new roman"] = ("times.ttf", "timesbd.ttf", "timesi.ttf", "timesbi.ttf"),
            ["courier new"] = ("cour.ttf", "courbd.ttf", "couri.ttf", "courbi.ttf"),
            ["calibri"] = ("calibri.ttf", "calibrib.ttf", "calibrii.ttf", "calibriz.ttf"),
            ["verdana"] = ("verdana.ttf", "verdanab.ttf", "verdanai.ttf", "verdanaz.ttf"),
            ["tahoma"] = ("tahoma.ttf", "tahomabd.ttf", "tahoma.ttf", "tahomabd.ttf"),
            ["georgia"] = ("georgia.ttf", "georgiab.ttf", "georgiai.ttf", "georgiaz.ttf"),
        };

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        foreach (string candidate in new[] { familyName.Trim(), "Segoe UI", "Arial", "Tahoma" })
        {
            if (!Families.TryGetValue(candidate, out var files)) continue;
            string file = (isBold, isItalic) switch
            {
                (true, true) => files.BI,
                (true, false) => files.B,
                (false, true) => files.I,
                _ => files.R,
            };
            string? path = FindFontFile(file);
            if (path == null) continue;

            string face = $"{candidate}|{(isBold ? "b" : "")}{(isItalic ? "i" : "")}".ToLowerInvariant();
            _faceToFile[face] = path;
            return new FontResolverInfo(face);
        }
        return null;
    }

    public byte[]? GetFont(string faceName) =>
        _faceToFile.TryGetValue(faceName, out var path)
            ? _bytes.GetOrAdd(faceName, _ => File.ReadAllBytes(path))
            : null;

    private static string? FindFontFile(string fileName)
    {
        foreach (string dir in FontDirs)
        {
            if (string.IsNullOrEmpty(dir)) continue;
            string path = Path.Combine(dir, fileName);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}

/// <summary>Idempotent one-time registration; call before any XFont is created.</summary>
public static class FontSetup
{
    private static bool _done;

    public static void Ensure()
    {
        if (_done) return;
        try
        {
            GlobalFontSettings.FontResolver ??= new WindowsFontResolver();
        }
        catch
        {
            // Another resolver was already registered — that's fine.
        }
        _done = true;
    }
}
