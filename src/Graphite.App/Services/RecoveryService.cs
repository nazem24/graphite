using System.IO;
using System.Text.Json;

namespace Graphite.App.Services;

/// <summary>What the journal knows about one document with unsaved changes.</summary>
public sealed record RecoveredDocument(
    string SessionDir,
    string PdfPath,
    string? OriginalPath,
    string Title,
    DateTime SavedAt,
    bool Encrypted);

/// <summary>
/// Crash recovery for PDFs. While a document has unsaved changes, a copy of it — markup, form
/// values and page edits baked in — is written to %LOCALAPPDATA%\Graphite\Recovery every so
/// often (see MainViewModel's journal timer). Each running Graphite has its own session folder
/// holding an exclusively locked file; a folder whose lock can be taken belongs to a session that
/// ended without cleaning up, i.e. a crash or power loss, and its documents are offered back at
/// the next start. Saving, closing (with or without saving) and a normal exit delete the copies.
///
/// Copies of password-protected documents are re-encrypted with the same password, so the
/// journal never holds a decrypted copy on disk.
/// </summary>
public static class RecoveryService
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Graphite", "Recovery");

    private static readonly object Gate = new();
    private static string? _sessionDir;
    private static FileStream? _lock;

    private sealed record Meta(string? OriginalPath, string Title, DateTime SavedAt, bool Encrypted);

    /// <summary>Claim a session folder for this process (call once at startup).</summary>
    public static void StartSession()
    {
        lock (Gate)
        {
            if (_sessionDir != null) return;
            try
            {
                string dir = Path.Combine(Root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
                Directory.CreateDirectory(dir);
                _lock = new FileStream(Path.Combine(dir, "session.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                _sessionDir = dir;
            }
            catch (Exception ex)
            {
                App.LogError("Crash recovery unavailable", ex);
            }
        }
    }

    /// <summary>Remove this session's folder (normal exit).</summary>
    public static void EndSession()
    {
        lock (Gate)
        {
            if (_sessionDir == null) return;
            try { _lock?.Dispose(); } catch { /* ignore */ }
            _lock = null;
            try { Directory.Delete(_sessionDir, recursive: true); } catch { /* best effort */ }
            _sessionDir = null;
        }
    }

    private static string PdfPathFor(Guid id) => Path.Combine(_sessionDir!, id.ToString("N") + ".pdf");
    private static string MetaPathFor(Guid id) => Path.Combine(_sessionDir!, id.ToString("N") + ".json");

    /// <summary>Write (or replace) the recovery copy of a document. Runs on a background thread.</summary>
    public static void Write(Guid id, byte[] pdf, string? originalPath, string title, bool encrypted)
    {
        string? dir;
        lock (Gate) dir = _sessionDir;
        if (dir == null) return;
        try
        {
            string pdfPath = PdfPathFor(id);
            string tmp = pdfPath + ".tmp";
            File.WriteAllBytes(tmp, pdf);
            File.Move(tmp, pdfPath, overwrite: true);
            File.WriteAllText(MetaPathFor(id),
                JsonSerializer.Serialize(new Meta(originalPath, title, DateTime.Now, encrypted)));
        }
        catch (Exception ex)
        {
            App.LogError("Couldn't write the recovery copy", ex);
        }
    }

    /// <summary>Drop a document's recovery copy (it was saved, closed or reloaded).</summary>
    public static void Remove(Guid id)
    {
        lock (Gate)
        {
            if (_sessionDir == null) return;
            try { File.Delete(PdfPathFor(id)); } catch { /* best effort */ }
            try { File.Delete(MetaPathFor(id)); } catch { /* best effort */ }
        }
    }

    /// <summary>Documents left behind by sessions that didn't end normally (newest first).</summary>
    public static List<RecoveredDocument> FindOrphans()
    {
        var found = new List<RecoveredDocument>();
        string? own;
        lock (Gate) own = _sessionDir;
        if (!Directory.Exists(Root)) return found;

        foreach (string dir in Directory.EnumerateDirectories(Root))
        {
            if (own != null && string.Equals(Path.GetFullPath(dir), Path.GetFullPath(own), StringComparison.OrdinalIgnoreCase))
                continue;
            if (IsLive(dir)) continue; // another Graphite is running with this folder

            bool any = false;
            foreach (string metaPath in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(metaPath));
                    string pdf = Path.ChangeExtension(metaPath, ".pdf");
                    if (meta == null || !File.Exists(pdf)) continue;
                    found.Add(new RecoveredDocument(dir, pdf, meta.OriginalPath, meta.Title, meta.SavedAt, meta.Encrypted));
                    any = true;
                }
                catch (Exception ex) { App.LogError("Unreadable recovery entry", ex); }
            }
            // A crashed session with nothing unsaved leaves an empty folder: tidy it up.
            if (!any) Discard(dir);
        }
        return found.OrderByDescending(d => d.SavedAt).ToList();
    }

    /// <summary>Delete a leftover session folder (after its documents were recovered or declined).</summary>
    public static void Discard(string sessionDir)
    {
        try { Directory.Delete(sessionDir, recursive: true); } catch { /* best effort */ }
    }

    private static bool IsLive(string dir)
    {
        string lockPath = Path.Combine(dir, "session.lock");
        if (!File.Exists(lockPath)) return false;
        try
        {
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false; // we could take the lock: its owner is gone
        }
        catch (IOException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }
}
