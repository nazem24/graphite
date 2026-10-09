using System.Globalization;

namespace Graphite.Core.Library;

/// <summary>A sub-folder of the library, summarized for the start screen.</summary>
/// <param name="PdfCount">Supported files in the folder, including everything below it.</param>
/// <param name="NewestWriteUtc">Most recent change to any supported file inside (folder time as a fallback).</param>
/// <param name="PreviewFile">A local (not online-only) PDF that can stand in as the folder's page preview.</param>
public sealed record LibraryFolder(string Path, string Name, int PdfCount, DateTime NewestWriteUtc, string? PreviewFile);

/// <summary>One document in a library folder.</summary>
public sealed record LibraryFile(string Path, string Name, string FolderPath, long Size,
    DateTime ModifiedUtc, bool OnlineOnly, bool IsOffice);

/// <summary>
/// Reads folders for the start screen's library: sub-folders with PDF counts, the files of one
/// folder, and name search below a folder. Pure file-system code (no UI), so it is unit-tested.
/// It never opens a file's contents — OneDrive "online only" placeholders are only listed, so
/// browsing a library never triggers a download.
/// </summary>
public static class LibraryScanner
{
    private static readonly string[] OfficeExtensions = { ".doc", ".docx", ".rtf", ".xls", ".xlsx", ".ppt", ".pptx" };

    /// <summary>Upper bound on entries visited while summarizing one tree, so a huge or
    /// looping folder (a drive root, a junction cycle) cannot hang the start screen.</summary>
    private const int MaxEntries = 40_000;

    // Hidden folders are listed on purpose (a library folder may well be called ".Studium");
    // only real system entries are skipped.
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.System,
    };

    // ------------------------------------------------------------- file kinds

    public static bool IsOffice(string path) =>
        OfficeExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>PDFs always; Word / Excel / PowerPoint only when <paramref name="includeOffice"/>.
    /// Office lock files ("~$report.docx") are never listed.</summary>
    public static bool IsSupported(string path, bool includeOffice)
    {
        string name = System.IO.Path.GetFileName(path);
        if (name.StartsWith("~$", StringComparison.Ordinal)) return false;
        string ext = System.IO.Path.GetExtension(name);
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return true;
        return includeOffice && OfficeExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What a folder view lists: the supported documents, or (<paramref name="allTypes"/>)
    /// every ordinary file. Office lock files and hidden files are never listed.</summary>
    private static bool Accept(FileInfo f, bool includeOffice, bool allTypes) =>
        allTypes
            ? !f.Name.StartsWith("~$", StringComparison.Ordinal) && (f.Attributes & FileAttributes.Hidden) == 0
            : IsListed(f, includeOffice);

    /// <summary>A supported file, or a document written by Graphite's own editor (listed even when
    /// Office files are switched off, because the person created it here).</summary>
    private static bool IsListed(FileInfo f, bool includeOffice) =>
        IsSupported(f.Name, includeOffice) ||
        (f.Extension.Equals(".docx", StringComparison.OrdinalIgnoreCase) &&
         !f.Name.StartsWith("~$", StringComparison.Ordinal) &&
         GraphiteDocx.IsGraphiteDocument(f.FullName));

    /// <summary>True for a OneDrive-style placeholder whose content is not on this PC
    /// (recall-on-open / recall-on-data-access / offline attribute).</summary>
    public static bool IsOnlineOnly(FileAttributes attributes) =>
        ((int)attributes & (0x400000 | 0x40000 | 0x1000)) != 0;

    private static bool SkipFolder(string name) => name.StartsWith('$') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------- OneDrive discovery

    /// <summary>The OneDrive folders signed in on this PC (personal and work/school).</summary>
    public static IReadOnlyList<string> FindOneDriveRoots()
    {
        var found = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                string full = System.IO.Path.GetFullPath(p).TrimEnd(System.IO.Path.DirectorySeparatorChar);
                if (Directory.Exists(full) && !found.Contains(full, StringComparer.OrdinalIgnoreCase)) found.Add(full);
            }
            catch { /* malformed path in the environment: ignore */ }
        }

        Add(Environment.GetEnvironmentVariable("OneDriveCommercial"));
        Add(Environment.GetEnvironmentVariable("OneDrive"));
        Add(Environment.GetEnvironmentVariable("OneDriveConsumer"));
        try
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (Directory.Exists(profile))
                foreach (var dir in Directory.EnumerateDirectories(profile, "OneDrive*", Options))
                    Add(dir);
        }
        catch { /* profile not readable */ }
        return found;
    }

    /// <summary>True when <paramref name="path"/> is, or lies inside, one of the OneDrive folders.</summary>
    public static bool IsUnderOneDrive(string path)
    {
        foreach (var root in FindOneDriveRoots())
            if (IsSameOrChild(root, path)) return true;
        return false;
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="folder"/> or lies below it.</summary>
    public static bool IsSameOrChild(string folder, string path)
    {
        string a = Normalize(folder), b = Normalize(path);
        return b.Equals(a, StringComparison.OrdinalIgnoreCase) ||
               b.StartsWith(a + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static bool SamePath(string a, string b) => Normalize(a).Equals(Normalize(b), StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string p)
    {
        try { return System.IO.Path.GetFullPath(p).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar); }
        catch { return p.TrimEnd('\\', '/'); }
    }

    // ------------------------------------------------------------- listing

    /// <summary>Supported files directly inside <paramref name="folder"/> (not below it);
    /// with <paramref name="allTypes"/>, every file of any kind.</summary>
    public static List<LibraryFile> ScanFiles(string folder, bool includeOffice, bool allTypes = false)
    {
        var result = new List<LibraryFile>();
        try
        {
            foreach (var f in new DirectoryInfo(folder).EnumerateFiles("*", Options))
            {
                if (!Accept(f, includeOffice, allTypes)) continue;
                result.Add(new LibraryFile(f.FullName, f.Name, folder, f.Length, f.LastWriteTimeUtc,
                    IsOnlineOnly(f.Attributes), IsOffice(f.Name)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* folder vanished or is locked */ }
        return result;
    }

    /// <summary>The sub-folders of <paramref name="folder"/> with the PDF count, newest change
    /// and a preview file of each, sorted by name.</summary>
    public static List<LibraryFolder> ScanSubfolders(string folder, bool includeOffice, CancellationToken ct = default)
    {
        var result = new List<LibraryFolder>();
        try
        {
            foreach (var dir in new DirectoryInfo(folder).EnumerateDirectories("*", Options))
            {
                ct.ThrowIfCancellationRequested();
                if (SkipFolder(dir.Name)) continue;
                var (count, newest, preview) = Summarize(dir.FullName, includeOffice, ct);
                result.Add(new LibraryFolder(dir.FullName, dir.Name, count,
                    count > 0 ? newest : dir.LastWriteTimeUtc, preview));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    /// <summary>Count supported files below a folder, the newest change among them, and the
    /// newest local PDF (the preview). Bounded by <see cref="MaxEntries"/>.</summary>
    private static (int Count, DateTime Newest, string? Preview) Summarize(string root, bool includeOffice, CancellationToken ct)
    {
        int count = 0, visited = 0;
        DateTime newest = DateTime.MinValue, previewTime = DateTime.MinValue;
        string? preview = null;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0 && visited < MaxEntries)
        {
            ct.ThrowIfCancellationRequested();
            string dir = pending.Pop();
            try
            {
                var info = new DirectoryInfo(dir);
                foreach (var f in info.EnumerateFiles("*", Options))
                {
                    visited++;
                    if (!IsListed(f, includeOffice)) continue;
                    count++;
                    DateTime t = f.LastWriteTimeUtc;
                    if (t > newest) newest = t;
                    if (!IsOffice(f.Name) && !IsOnlineOnly(f.Attributes) && t > previewTime)
                    {
                        previewTime = t;
                        preview = f.FullName;
                    }
                }
                foreach (var d in info.EnumerateDirectories("*", Options))
                {
                    visited++;
                    if (!SkipFolder(d.Name)) pending.Push(d.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* skip unreadable folders */ }
        }
        return (count, newest, preview);
    }

    /// <summary>Direct sub-folder count and total supported files below <paramref name="folder"/>
    /// (what the "choose folder" dialog shows before the person commits).</summary>
    public static (int Subfolders, int Files) CountTotals(string folder, bool includeOffice, CancellationToken ct = default)
    {
        int subs = 0;
        try
        {
            foreach (var d in new DirectoryInfo(folder).EnumerateDirectories("*", Options))
                if (!SkipFolder(d.Name)) subs++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return (subs, Summarize(folder, includeOffice, ct).Count);
    }

    /// <summary>Direct sub-folders only (names and paths), sorted — for the folder tree.</summary>
    public static List<(string Name, string Path)> ListSubfolders(string folder)
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var d in new DirectoryInfo(folder).EnumerateDirectories("*", Options))
                if (!SkipFolder(d.Name)) list.Add((d.Name, d.FullName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        list.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.CurrentCultureIgnoreCase));
        return list;
    }

    // ------------------------------------------------------------- search

    /// <summary>Files below <paramref name="root"/> whose name contains every word of
    /// <paramref name="query"/> (case-insensitive), sorted by name. Stops at <paramref name="limit"/> hits.</summary>
    public static List<LibraryFile> Search(string root, string query, bool includeOffice,
        CancellationToken ct = default, int limit = 300, bool allTypes = false)
    {
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var hits = new List<LibraryFile>();
        if (terms.Length == 0) return hits;

        int visited = 0;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && hits.Count < limit && visited < MaxEntries * 2)
        {
            ct.ThrowIfCancellationRequested();
            string dir = pending.Pop();
            try
            {
                var info = new DirectoryInfo(dir);
                foreach (var f in info.EnumerateFiles("*", Options))
                {
                    visited++;
                    if (!Accept(f, includeOffice, allTypes)) continue;
                    string stem = System.IO.Path.GetFileNameWithoutExtension(f.Name);
                    if (!terms.All(t => stem.Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
                    hits.Add(new LibraryFile(f.FullName, f.Name, dir, f.Length, f.LastWriteTimeUtc,
                        IsOnlineOnly(f.Attributes), IsOffice(f.Name)));
                    if (hits.Count >= limit) break;
                }
                foreach (var d in info.EnumerateDirectories("*", Options))
                {
                    visited++;
                    if (!SkipFolder(d.Name)) pending.Push(d.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        hits.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return hits;
    }

    // ------------------------------------------------------------- text

    /// <summary>"just now", "5 min ago", "2 h ago", "yesterday", "4 days ago", "2 weeks ago", "3 months ago".</summary>
    public static string Ago(DateTime utc, DateTime? nowUtc = null)
    {
        var span = (nowUtc ?? DateTime.UtcNow) - utc;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 2) return "yesterday";
        if (span.TotalDays < 14) return $"{(int)span.TotalDays} days ago";
        if (span.TotalDays < 60) return $"{(int)(span.TotalDays / 7)} weeks ago";
        if (span.TotalDays < 700) return $"{(int)(span.TotalDays / 30)} months ago";
        return $"{(int)(span.TotalDays / 365)} years ago";
    }

    /// <summary>Like <see cref="Ago"/> but day-granular, for folders: "today", "yesterday", "5 days ago"…</summary>
    public static string Changed(DateTime utc, DateTime? nowUtc = null)
    {
        var now = (nowUtc ?? DateTime.UtcNow).ToLocalTime().Date;
        var then = utc.ToLocalTime().Date;
        int days = (int)(now - then).TotalDays;
        if (days <= 0) return "today";
        if (days == 1) return "yesterday";
        return Ago(utc, nowUtc);
    }

    /// <summary>"3 Oct" (and the year when it is not this year): a file's modified date.</summary>
    public static string ShortDate(DateTime utc, DateTime? nowUtc = null)
    {
        var local = utc.ToLocalTime();
        var now = (nowUtc ?? DateTime.UtcNow).ToLocalTime();
        return local.ToString(local.Year == now.Year ? "d MMM" : "d MMM yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>"310 KB", "2.1 MB", "1.4 GB".</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb.ToString("0", CultureInfo.InvariantCulture)} KB";
        double mb = kb / 1024.0;
        if (mb < 1024) return $"{mb.ToString("0.0", CultureInfo.InvariantCulture)} MB";
        return $"{(mb / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} GB";
    }

    public static string Plural(int n, string singular, string? plural = null) =>
        n == 1 ? $"1 {singular}" : $"{n} {plural ?? singular + "s"}";
}
