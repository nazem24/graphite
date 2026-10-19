using System.IO;
using System.Runtime.InteropServices;

namespace Graphite.App.Interop;

/// <summary>Windows folders that have no Environment.SpecialFolder entry (Downloads).</summary>
public static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>The person's Downloads folder (wherever they moved it), created if it is missing.</summary>
    public static string Downloads
    {
        get
        {
            string? path = null;
            try
            {
                if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var ptr) == 0)
                {
                    path = Marshal.PtrToStringUni(ptr);
                    Marshal.FreeCoTaskMem(ptr);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
            path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            try { Directory.CreateDirectory(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return path;
        }
    }
}
