using System.Runtime.InteropServices;

namespace Graphite.App.Services;

/// <summary>The Windows clipboard is a shared, lockable resource: when another process
/// (clipboard managers, remote-desktop, Office) has it open, every WPF Clipboard call throws
/// COMException CLIPBRD_E_CANT_OPEN. Retry briefly instead of surfacing an error dialog.</summary>
public static class ClipboardHelper
{
    public static T? Try<T>(Func<T> action, int attempts = 5)
    {
        for (int i = 0; i < attempts; i++)
        {
            try { return action(); }
            catch (ExternalException) when (i < attempts - 1) { Thread.Sleep(30); }
            catch (ExternalException ex)
            {
                App.LogError("Clipboard unavailable", ex);
                return default;
            }
        }
        return default;
    }
}
