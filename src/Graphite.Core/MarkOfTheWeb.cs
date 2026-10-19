namespace Graphite.Core;

/// <summary>
/// Removes the "Mark of the Web" (the hidden <c>Zone.Identifier</c> stream Windows attaches to files that came from
/// a browser or a downloaded zip) from files Graphite itself shipped. SmartScreen only shows its "Windows protected
/// your PC" prompt for marked files, so once the person has accepted it for the first launch, the app unblocks its
/// own folder (what PowerShell's <c>Unblock-File</c> does) and the prompt doesn't come back.
/// Best effort: never throws, does nothing outside Windows.
/// </summary>
public static class MarkOfTheWeb
{
    private const string StreamName = ":Zone.Identifier";

    /// <summary>Unblocks every file under <paramref name="folder"/> on a low-priority background thread.</summary>
    public static void UnblockInBackground(string folder)
    {
        if (!OperatingSystem.IsWindows()) return;
        var thread = new Thread(() => UnblockFolder(folder))
        {
            IsBackground = true,
            Name = "Graphite unblock",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    /// <summary>Removes the mark from every file under <paramref name="folder"/>. Returns how many files had one.</summary>
    public static int UnblockFolder(string folder)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return 0;

        int removed = 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                // links could lead out of the install folder: never follow them
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (string file in Directory.EnumerateFiles(folder, "*", options))
                if (Unblock(file)) removed++;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Unblock skipped: {ex.Message}");
        }
        return removed;
    }

    /// <summary>Removes the mark from one file. True if it had one and it is now gone.</summary>
    public static bool Unblock(string file)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            string stream = file + StreamName;
            if (!File.Exists(stream)) return false;
            File.Delete(stream);
            return !File.Exists(stream);
        }
        catch
        {
            return false; // locked, read-only or not allowed: the prompt just stays for that file
        }
    }
}
