using System.Runtime;
using System.Runtime.InteropServices;

namespace Graphite.App.Services;

/// <summary>
/// Hands memory back after a document is closed. Disposing a document only drops references:
/// .NET then keeps the freed pages (the PDF byte[] copies and undo snapshots sit on the Large
/// Object Heap, which isn't compacted by default), WPF bitmap pixel buffers are only released
/// when their finalizers run, and Windows keeps the pages in the working set until it needs
/// them elsewhere — so Task Manager stayed high long after the tab was gone.
/// </summary>
public static class MemoryTrim
{
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

    private static int _pending;

    /// <summary>Collect and trim shortly from now. The short delay lets the closing method
    /// return first (its locals still reference the document), and several closes in quick
    /// succession share one pass.</summary>
    public static void Schedule(int delayMs = 1500)
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                Run();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Memory trim skipped: {ex.Message}");
            }
            finally { Volatile.Write(ref _pending, 0); }
        });
    }

    private static void Run()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        // Bitmap pixel buffers and other native handles are released by finalizers; collect
        // again afterwards so what they freed is returned too.
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        // Drop the now-empty pages from the working set (the number Task Manager shows).
        // Pages other open documents still use fault back in on demand.
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        SetProcessWorkingSetSize(self.Handle, (IntPtr)(-1), (IntPtr)(-1));
    }
}
