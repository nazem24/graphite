using System.Collections.Concurrent;
using System.IO.Compression;

namespace Graphite.Core.Library;

/// <summary>
/// Recognizes the .docx files Graphite itself writes (its document editor stores documents as
/// ordinary Word files). They carry <c>&lt;Application&gt;Graphite&lt;/Application&gt;</c> in
/// docProps/app.xml; that is how the library lists them and how Open decides between the built-in
/// editor and "convert to PDF with Office". Documents written by Word or anything else are
/// left to Office. Pure file code, results cached per (path, size, time).
/// </summary>
public static class GraphiteDocx
{
    public const string ApplicationName = "Graphite";

    private static readonly ConcurrentDictionary<string, (long Length, long Ticks, bool Result)> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is a .docx written by Graphite. Never throws, never
    /// opens an online-only (not yet downloaded) OneDrive placeholder.</summary>
    public static bool IsGraphiteDocument(string path)
    {
        try
        {
            if (!path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)) return false;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 100) return false;
            if (LibraryScanner.IsOnlineOnly(info.Attributes)) return false;

            if (Cache.TryGetValue(path, out var cached) &&
                cached.Length == info.Length && cached.Ticks == info.LastWriteTimeUtc.Ticks)
                return cached.Result;

            bool result = Probe(path);
            Cache[path] = (info.Length, info.LastWriteTimeUtc.Ticks, result);
            return result;
        }
        catch
        {
            return false;
        }
    }

    private static bool Probe(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = zip.GetEntry("docProps/app.xml");
        if (entry == null) return false;
        using var reader = new StreamReader(entry.Open());
        string xml = reader.ReadToEnd();
        return xml.Contains("<Application>" + ApplicationName + "</Application>", StringComparison.Ordinal);
    }

    /// <summary>Forget a cached answer (after the file was rewritten).</summary>
    public static void Forget(string path) => Cache.TryRemove(path, out _);

    /// <summary>"Untitled.docx", then "Untitled 2.docx", "Untitled 3.docx"… — the first name not taken in the folder.</summary>
    public static string UniquePath(string folder, string baseName, string extension)
    {
        string stem = string.IsNullOrWhiteSpace(baseName) ? "Untitled" : baseName.Trim();
        string candidate = Path.Combine(folder, stem + extension);
        for (int i = 2; File.Exists(candidate) || Directory.Exists(candidate); i++)
            candidate = Path.Combine(folder, $"{stem} {i}{extension}");
        return candidate;
    }

    /// <summary>Strip characters Windows does not allow in a file name; falls back to "Untitled".</summary>
    public static string SanitizeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        string cleaned = new string(name.Where(c => !bad.Contains(c)).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "Untitled" : cleaned;
    }
}
