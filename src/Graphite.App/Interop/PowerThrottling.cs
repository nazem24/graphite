using System.Runtime.InteropServices;

namespace Graphite.App.Interop;

/// <summary>
/// Windows' "EcoQoS" (process power throttling). While Graphite's window is minimized nobody is waiting on the
/// UI, so the process asks Windows to run it in its efficiency mode: its threads move to the efficiency cores
/// at a lower clock, which uses a fraction of the power. Background work that is already running (a library
/// scan, thumbnails, OCR) simply finishes a little slower; it is switched off again as soon as the window is restored.
/// Needs Windows 10 1709 or newer; anywhere it isn't supported the call fails quietly and nothing changes.
/// </summary>
public static class PowerThrottling
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int informationClass,
        ref ProcessPowerThrottlingState info, int size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    private const int ProcessPowerThrottlingClass = 4;
    private const uint CurrentVersion = 1;
    private const uint ExecutionSpeed = 0x1;

    private static bool? _applied;

    /// <summary>Turn efficiency mode on or off for this process. Calling it with the current state is a no-op.</summary>
    public static void Set(bool efficient)
    {
        if (_applied == efficient) return;
        try
        {
            var state = new ProcessPowerThrottlingState
            {
                Version = CurrentVersion,
                ControlMask = ExecutionSpeed,
                StateMask = efficient ? ExecutionSpeed : 0,
            };
            if (SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottlingClass, ref state,
                    Marshal.SizeOf<ProcessPowerThrottlingState>()))
                _applied = efficient;
        }
        catch { /* not supported here: nothing to do */ }
    }
}
