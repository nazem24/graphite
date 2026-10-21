using System.IO;
using System.Text.Json;
using Graphite.App.Interop;
using Graphite.Core.Canvas;

namespace Graphite.App.Services;

/// <summary>
/// Everything the Canvas space remembers: the Canvas address, the access token (encrypted with Windows DPAPI for
/// the current user, never in plain text), the chosen term, which courses are linked to which folders, pins, and
/// the two behaviour switches. Kept in its own file next to settings.json (%APPDATA%\Graphite\canvas.json), so
/// the token never travels with a synced library folder. Cached course data and downloaded files live in
/// %LOCALAPPDATA%\Graphite\Canvas, which is safe to delete.
/// </summary>
public static class CanvasSettings
{
    private sealed class Data
    {
        public string BaseUrl { get; set; } = CanvasClient.DefaultBaseUrl;
        /// <summary>The token, protected with DPAPI (base64).</summary>
        public string? ProtectedToken { get; set; }
        /// <summary>The account the token belongs to, as Canvas said when it was tested.</summary>
        public string? AccountName { get; set; }
        /// <summary>A term the person picked; null = the one running today.</summary>
        public string? TermId { get; set; }
        /// <summary>Course id → folder on this PC.</summary>
        public Dictionary<string, string> CourseFolders { get; set; } = new();
        /// <summary>Pinned entries as "courseId|key".</summary>
        public List<string> Pins { get; set; } = new();
        /// <summary>Check every course of the term for new files while the Canvas space is open.</summary>
        public bool CheckAllCourses { get; set; } = true;
        /// <summary>List every file type in a course, not only PDFs and Office files.</summary>
        public bool AllFileTypes { get; set; } = true;
        /// <summary>Calendar links (.ics) for lectures and exams. The address is a secret, so it is protected like the token.</summary>
        public List<CalendarSourceData> CalendarSources { get; set; } = new();
        /// <summary>Calendar page: show Canvas due dates.</summary>
        public bool CalendarShowDue { get; set; } = true;
        /// <summary>Calendar page: show lectures and exams from the calendar links.</summary>
        public bool CalendarShowLectures { get; set; } = true;
        /// <summary>Calendar page: show all-day entries of the calendar links (semester dates, registration phases …).</summary>
        public bool CalendarShowAllDay { get; set; } = true;
        /// <summary>Overview: when nothing is new, show today, the next due dates and the all-day entries at the top (off by default).</summary>
        public bool CalendarOverviewStrip { get; set; }
        /// <summary>Calendar page: "Month", "Week" or "Agenda" (the one used last).</summary>
        public string CalendarMode { get; set; } = "Month";
        /// <summary>Which view the calendar opens in: "Last used", "Month", "Week" or "Agenda".</summary>
        public string CalendarStartView { get; set; } = "Last used";
    }

    public sealed class CalendarSourceData
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? ProtectedUrl { get; set; }
    }

    private static Data _data = Load();

    /// <summary>Raised after the address, token, term or a switch changed.</summary>
    public static event Action? Changed;

    /// <summary>Raised after a course folder link changed (course id).</summary>
    public static event Action<string>? FolderLinksChanged;

    /// <summary>Raised after a calendar setting changed (links, what to show, the view).</summary>
    public static event Action? CalendarChanged;

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Graphite", "canvas.json");

    /// <summary>Cached course structures and "seen" sets.</summary>
    public static string CacheRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Graphite", "Canvas");

    /// <summary>Files opened from courses that are not linked to a folder.</summary>
    public static string FileCacheRoot => Path.Combine(CacheRoot, "Files");

    /// <summary>True for a file that lives in the Canvas file cache (its first Save goes to "Save as…").</summary>
    public static bool IsCacheFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            string full = Path.GetFullPath(path);
            string root = Path.GetFullPath(FileCacheRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    // ------------------------------------------------------------- connection

    public static string BaseUrl => _data.BaseUrl;
    public static bool HasToken => !string.IsNullOrEmpty(_data.ProtectedToken);
    public static string? AccountName => _data.AccountName;

    /// <summary>The token in plain text, only for building a client. Null when none is saved or it cannot be
    /// decrypted (settings copied from another PC or user).</summary>
    public static string? ReadToken() => Dpapi.Unprotect(_data.ProtectedToken);

    public static void SetConnection(string baseUrl, string token, string? accountName)
    {
        _data.BaseUrl = CanvasClient.NormalizeBase(baseUrl).AbsoluteUri.TrimEnd('/');
        _data.ProtectedToken = Dpapi.Protect(token.Trim());
        _data.AccountName = accountName;
        Save();
        Changed?.Invoke();
    }

    public static void RemoveToken()
    {
        _data.ProtectedToken = null;
        _data.AccountName = null;
        Save();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------- term & switches

    public static string? TermId => _data.TermId;

    public static void SetTerm(string? termId)
    {
        if (_data.TermId == termId) return;
        _data.TermId = termId;
        Save();
        Changed?.Invoke();
    }

    public static bool CheckAllCourses => _data.CheckAllCourses;
    public static bool AllFileTypes => _data.AllFileTypes;

    public static void SetCheckAllCourses(bool on)
    {
        if (_data.CheckAllCourses == on) return;
        _data.CheckAllCourses = on;
        Save();
        Changed?.Invoke();
    }

    public static void SetAllFileTypes(bool on)
    {
        if (_data.AllFileTypes == on) return;
        _data.AllFileTypes = on;
        Save();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------- calendar

    /// <summary>A calendar link with its address in plain text (only held in memory).</summary>
    public sealed record CalendarSource(string Id, string Name, string Url);

    public static bool CalendarShowDue => _data.CalendarShowDue;
    public static bool CalendarShowLectures => _data.CalendarShowLectures;
    public static bool CalendarShowAllDay => _data.CalendarShowAllDay;
    public static bool CalendarOverviewStrip => _data.CalendarOverviewStrip;
    public static string CalendarMode => _data.CalendarMode;
    public static string CalendarStartView => _data.CalendarStartView;

    /// <summary>The view the calendar should open in now: the chosen one, or the one used last.</summary>
    public static string CalendarOpenMode =>
        _data.CalendarStartView is "Month" or "Week" or "Agenda" ? _data.CalendarStartView : _data.CalendarMode;

    public static void SetCalendarShowDue(bool on)
    {
        if (_data.CalendarShowDue == on) return;
        _data.CalendarShowDue = on;
        Save();
        CalendarChanged?.Invoke();
    }

    public static void SetCalendarShowLectures(bool on)
    {
        if (_data.CalendarShowLectures == on) return;
        _data.CalendarShowLectures = on;
        Save();
        CalendarChanged?.Invoke();
    }

    public static void SetCalendarShowAllDay(bool on)
    {
        if (_data.CalendarShowAllDay == on) return;
        _data.CalendarShowAllDay = on;
        Save();
        CalendarChanged?.Invoke();
    }

    public static void SetCalendarOverviewStrip(bool on)
    {
        if (_data.CalendarOverviewStrip == on) return;
        _data.CalendarOverviewStrip = on;
        Save();
        CalendarChanged?.Invoke();
    }

    public static void SetCalendarStartView(string view)
    {
        if (_data.CalendarStartView == view) return;
        _data.CalendarStartView = view;
        Save();
        CalendarChanged?.Invoke();
    }

    public static void SetCalendarMode(string mode)
    {
        if (_data.CalendarMode == mode) return;
        _data.CalendarMode = mode;
        Save();
        CalendarChanged?.Invoke();
    }

    /// <summary>The calendar links whose address can be read on this PC.</summary>
    public static IReadOnlyList<CalendarSource> CalendarSources() =>
        _data.CalendarSources
            .Select(s => new CalendarSource(s.Id, s.Name, Dpapi.Unprotect(s.ProtectedUrl) ?? ""))
            .Where(s => s.Url.Length > 0)
            .ToList();

    public static void AddCalendarSource(string id, string name, string url)
    {
        _data.CalendarSources.Add(new CalendarSourceData { Id = id, Name = name, ProtectedUrl = Dpapi.Protect(url.Trim()) });
        Save();
        CalendarChanged?.Invoke();
    }

    public static void RemoveCalendarSource(string id)
    {
        if (_data.CalendarSources.RemoveAll(s => s.Id == id) == 0) return;
        Save();
        CalendarChanged?.Invoke();
    }

    /// <summary>The file name a calendar link's downloaded copy has in the cache.</summary>
    public static string CalendarCacheName(string sourceId) => "calendar-" + CanvasNames.SafeId(sourceId) + ".ics";

    // ------------------------------------------------------------- folders

    public static IReadOnlyDictionary<string, string> CourseFolders => _data.CourseFolders;

    /// <summary>The folder a course is linked to, if it still exists.</summary>
    public static string? FolderOf(string courseId) =>
        _data.CourseFolders.TryGetValue(courseId, out var f) && Directory.Exists(f) ? f : null;

    /// <summary>Link a course to a folder, or pass null to unlink it.</summary>
    public static void SetFolder(string courseId, string? folder)
    {
        if (string.IsNullOrEmpty(folder)) _data.CourseFolders.Remove(courseId);
        else _data.CourseFolders[courseId] = folder;
        Save();
        FolderLinksChanged?.Invoke(courseId);
    }

    // ------------------------------------------------------------- pins

    public static bool IsPinned(string courseId, string key) => _data.Pins.Contains(courseId + "|" + key);
    public static int PinCount => _data.Pins.Count;
    public static IEnumerable<(string CourseId, string Key)> Pins =>
        _data.Pins.Select(p => p.Split('|', 2)).Where(a => a.Length == 2).Select(a => (a[0], a[1]));

    public static bool TogglePin(string courseId, string key)
    {
        string id = courseId + "|" + key;
        bool pinned = !_data.Pins.Remove(id);
        if (pinned) _data.Pins.Insert(0, id);
        Save();
        return pinned;
    }

    // ------------------------------------------------------------- cache

    /// <summary>Delete cached course structures and the downloaded cache files. Pins, links and what was seen stay.</summary>
    public static void ClearCache()
    {
        new CanvasCache(CacheRoot).ClearStructure();
        try { if (Directory.Exists(FileCacheRoot)) Directory.Delete(FileCacheRoot, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a file that is open in a tab stays; everything else is gone
            App.LogError("Clearing the Canvas file cache was incomplete", ex);
        }
    }

    /// <summary>How long a downloaded file is kept after it was last opened.</summary>
    public static readonly TimeSpan CacheKeep = TimeSpan.FromDays(30);

    /// <summary>Note that a cached file was just used (the clean-up goes by this, not by the file's own dates, which
    /// follow Canvas and are not touched by reading on most Windows setups).</summary>
    public static void TouchCacheFile(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Clean up the file cache: downloaded files not opened for <see cref="CacheKeep"/>, and the leftovers of
    /// interrupted downloads, are deleted, and folders that end up empty go with them. Files in
    /// <paramref name="keep"/> (open in a tab) are never touched. Returns the paths that were deleted.
    /// </summary>
    public static List<string> PruneCache(ISet<string> keep)
    {
        var removed = new List<string>();
        try
        {
            if (!Directory.Exists(FileCacheRoot)) return removed;
            var now = DateTime.UtcNow;
            foreach (var file in new DirectoryInfo(FileCacheRoot).EnumerateFiles("*", SearchOption.AllDirectories).ToList())
            {
                try
                {
                    var used = file.LastAccessTimeUtc > file.CreationTimeUtc ? file.LastAccessTimeUtc : file.CreationTimeUtc;
                    bool partial = file.Name.EndsWith(".graphite-part", StringComparison.OrdinalIgnoreCase);
                    if (now - used < (partial ? TimeSpan.FromDays(1) : CacheKeep)) continue;
                    if (keep.Contains(file.FullName)) continue;
                    file.Delete();
                    removed.Add(file.FullName);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            // empty <file id> and <course> folders, innermost first
            foreach (var dir in new DirectoryInfo(FileCacheRoot).EnumerateDirectories("*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.FullName.Length).ToList())
            {
                try { if (!dir.EnumerateFileSystemInfos().Any()) dir.Delete(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.LogError("Cleaning the Canvas file cache was incomplete", ex);
        }
        return removed;
    }

    /// <summary>How much the downloaded cache files take up.</summary>
    public static long CacheSize()
    {
        try
        {
            if (!Directory.Exists(FileCacheRoot)) return 0;
            return new DirectoryInfo(FileCacheRoot).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    // ------------------------------------------------------------- storage

    private static Data Load()
    {
        try
        {
            if (File.Exists(SettingsPath) && JsonSerializer.Deserialize<Data>(File.ReadAllText(SettingsPath)) is { } d)
            {
                d.CourseFolders = new Dictionary<string, string>(d.CourseFolders ?? new());
                d.Pins ??= new();
                d.CalendarSources ??= new();
                if (d.CalendarMode is not ("Month" or "Week" or "Agenda")) d.CalendarMode = "Month";
                if (d.CalendarStartView is not ("Last used" or "Month" or "Week" or "Agenda")) d.CalendarStartView = "Last used";
                if (string.IsNullOrWhiteSpace(d.BaseUrl)) d.BaseUrl = CanvasClient.DefaultBaseUrl;
                return d;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Canvas settings load failed, using defaults: {ex.Message}");
        }
        return new Data();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            string tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data));
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            App.LogError("Saving the Canvas settings failed", ex);
        }
    }
}
