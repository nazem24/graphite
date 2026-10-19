using System.Text.Json;

namespace Graphite.Core.Canvas;

/// <summary>
/// The on-disk cache of the Canvas space: the course list, each course's structure, and what has been
/// seen. One small JSON file per course, so opening a course shows it at once while it refreshes behind.
/// Files are written to a temporary name and then moved over the old one, so a crash never leaves half a file.
/// </summary>
public sealed class CanvasCache
{
    public string Root { get; }

    public CanvasCache(string root) => Root = root;

    private string CoursesPath => Path.Combine(Root, "courses.json");
    private string CoursePath(string id) => Path.Combine(Root, "course-" + CanvasNames.SafeId(id) + ".json");
    private string SeenPath(string id) => Path.Combine(Root, "seen-" + CanvasNames.SafeId(id) + ".json");

    public sealed class CourseList
    {
        public DateTime FetchedUtc { get; set; }
        public List<CanvasCourse> Courses { get; set; } = new();
    }

    public CourseList? LoadCourses() => Read<CourseList>(CoursesPath);
    public void SaveCourses(List<CanvasCourse> courses) =>
        Write(CoursesPath, new CourseList { FetchedUtc = DateTime.UtcNow, Courses = courses });

    public CanvasCourseData? LoadCourse(string id) => Read<CanvasCourseData>(CoursePath(id));
    public void SaveCourse(string id, CanvasCourseData data) => Write(CoursePath(id), data);

    /// <summary>The keys already seen in a course; null when the course was never loaded (so its first load
    /// seeds the set and nothing is flagged as new).</summary>
    public HashSet<string>? LoadSeen(string id) =>
        Read<List<string>>(SeenPath(id)) is { } list ? new HashSet<string>(list) : null;

    public void SaveSeen(string id, IEnumerable<string> keys) => Write(SeenPath(id), keys.ToList());

    /// <summary>Forget the cached course structures (the next visit loads them again). What was seen is kept,
    /// so nothing turns "new" because of it.</summary>
    public void ClearStructure()
    {
        if (!Directory.Exists(Root)) return;
        foreach (var f in Directory.EnumerateFiles(Root, "course*.json")) TryDelete(f);
    }

    private static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), CanvasJson.Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static void Write<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, CanvasJson.Options));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a cache that cannot be written only means the next visit loads from Canvas again
        }
    }

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>What is new in a course: everything whose key is not in the "seen" set. Pure, so it is unit-tested.</summary>
public static class CanvasNew
{
    /// <summary>Every entry of a course (headers left out) with where it sits: "Start page", a module's name,
    /// or "Module › Page" for a file linked inside a page.</summary>
    public static IEnumerable<(CanvasItem Item, string Path, CanvasModule? Module)> Flatten(CanvasCourseData? data)
    {
        if (data == null) yield break;
        if (data.Front != null)
            foreach (var l in data.Front.Links) yield return (l, "Start page", null);
        foreach (var m in data.Modules)
            foreach (var it in m.Items)
            {
                if (it.Kind == CanvasItemKind.Header) continue;
                yield return (it, m.Name, m);
                if (it.Links != null)
                    foreach (var l in it.Links) yield return (l, m.Name + " › " + it.Title, m);
            }
    }

    public static HashSet<string> AllKeys(CanvasCourseData? data) => Flatten(data).Select(x => x.Item.Key).ToHashSet();

    /// <summary>The keys not seen yet. A course never seen (null set) has nothing new.</summary>
    public static HashSet<string> NewKeys(CanvasCourseData? data, IReadOnlySet<string>? seen) =>
        seen == null ? new HashSet<string>() : Flatten(data).Select(x => x.Item.Key).Where(k => !seen.Contains(k)).ToHashSet();

    /// <summary>The files of a module, including those linked inside its pages, each once.</summary>
    public static List<CanvasItem> ModuleFiles(CanvasModule module)
    {
        var output = new List<CanvasItem>();
        var keys = new HashSet<string>();
        void Add(CanvasItem e)
        {
            if (e.Kind == CanvasItemKind.File && keys.Add(e.Key)) output.Add(e);
        }
        foreach (var it in module.Items)
        {
            Add(it);
            if (it.Links != null) foreach (var l in it.Links) Add(l);
        }
        return output;
    }
}

/// <summary>Safe names for files and cache entries.</summary>
public static class CanvasNames
{
    private static readonly char[] Invalid = Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }).Distinct().ToArray();

    /// <summary>A Canvas display name as a Windows file name ("Week 1: Slides?.pdf" → "Week 1_ Slides_.pdf").</summary>
    public static string SafeFileName(string? name)
    {
        string n = new string((name ?? "").Select(ch => ch < 32 || Invalid.Contains(ch) ? '_' : ch).ToArray()).Trim().TrimEnd('.');
        if (n.Length == 0) n = "file";
        if (n.Length > 150)
        {
            string ext = Path.GetExtension(n);
            n = n[..(150 - ext.Length)] + ext;
        }
        return n;
    }

    /// <summary>An id as part of a cache file name (digits normally; anything else is replaced).</summary>
    public static string SafeId(string id) => new(id.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

    /// <summary>"name.pdf", or "name (2).pdf", "name (3).pdf"… whichever is free in <paramref name="folder"/>.</summary>
    public static string UniquePath(string folder, string fileName)
    {
        string path = Path.Combine(folder, fileName);
        if (!File.Exists(path)) return path;
        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        for (int i = 2; ; i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }
    }

    public static string Extension(string? name)
    {
        string ext = Path.GetExtension(name ?? "").TrimStart('.');
        return ext.Length is > 0 and <= 5 ? ext.ToLowerInvariant() : "";
    }
}

/// <summary>
/// A course linked to a folder on this PC. Graphite keeps a small hidden file in that folder that maps each
/// Canvas file id to the name it has there, because files get renamed locally ("Week 1 ACA M1-M3 Management
/// Accounting.pdf" becomes "C_M1_Management_Accounting.pdf") and must still be recognised.
/// </summary>
public static class CanvasFolderLink
{
    public const string ManifestName = ".graphite-canvas.json";

    public sealed class Manifest
    {
        public string CourseId { get; set; } = "";
        /// <summary>Canvas file id → file name in the folder.</summary>
        public Dictionary<string, string> Files { get; set; } = new();
    }

    public static Manifest Load(string folder)
    {
        try
        {
            string path = Path.Combine(folder, ManifestName);
            if (File.Exists(path) &&
                JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), CanvasJson.Options) is { } m)
            {
                m.Files ??= new();
                return m;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        return new Manifest();
    }

    public static void Save(string folder, Manifest manifest)
    {
        string path = Path.Combine(folder, ManifestName);
        try
        {
            // a hidden file cannot be overwritten on Windows without lifting the attribute first
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, CanvasJson.Options));
            File.SetAttributes(path, FileAttributes.Hidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Where the file with this Canvas id lives in the folder, if it is still there (renamed or not).</summary>
    public static string? FindLocal(string folder, Manifest manifest, string fileId)
    {
        if (fileId.Length == 0 || !manifest.Files.TryGetValue(fileId, out var name)) return null;
        string path = Path.Combine(folder, name);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Where to put (or find) a Canvas file in a linked folder. Returns the path and whether it is already there:
    /// a file recorded under this id (even renamed), or a file with the same name and size that was put there
    /// by hand (it is adopted). Otherwise a free name for the download.
    /// </summary>
    public static (string Path, bool Exists) Place(string folder, Manifest manifest, string fileId, string displayName, long? size)
    {
        if (FindLocal(folder, manifest, fileId) is { } known) return (known, true);
        string name = CanvasNames.SafeFileName(displayName);
        string candidate = Path.Combine(folder, name);
        if (File.Exists(candidate) && size is { } s && new FileInfo(candidate).Length == s &&
            !manifest.Files.Values.Contains(name, StringComparer.OrdinalIgnoreCase))
            return (candidate, true);
        return (CanvasNames.UniquePath(folder, name), false);
    }

    /// <summary>Remember that the file with this id is <paramref name="path"/> in the folder.</summary>
    public static void Record(string folder, string courseId, string fileId, string path)
    {
        if (fileId.Length == 0) return;
        var m = Load(folder);
        m.CourseId = courseId;
        m.Files[fileId] = Path.GetFileName(path);
        Save(folder, m);
    }
}
