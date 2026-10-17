using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Graphite.App.Interop;

/// <summary>
/// Whether Windows' Energy Saver (Battery Saver) is on. While it is, Windows paints Acrylic as a
/// flat colour instead of blurring the desktop, so the Blur / Frosted / Glass materials would look
/// muddy; <see cref="Services.ThemeService"/> shows Mica instead for as long as this is true.
///
/// Windows reports this two ways and builds differ in which one they use, so both are watched:
/// the older "battery saver" flag (GUID_POWER_SAVING_STATUS / GetSystemPowerStatus) and the newer
/// Energy Saver status (GUID_ENERGY_SAVER_STATUS, off / standard / high savings). The change
/// notifications need no window; Windows calls back with the current value right after
/// registering. A slow poll of the old flag is kept as a safety net.
/// </summary>
public static class EnergySaver
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;   // 1 = Battery Saver is on
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifySubscribe
    {
        public IntPtr Callback;
        public IntPtr Context;
    }

    private delegate uint NotifyCallback(IntPtr context, uint type, IntPtr setting);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSettingRegisterNotification(ref Guid setting, uint flags, IntPtr recipient, out IntPtr handle);

    private const uint DEVICE_NOTIFY_CALLBACK = 2;
    private const uint PBT_POWERSETTINGCHANGE = 0x8013;
    private static readonly Guid PowerSavingStatus = new("E00958C0-C213-4ACE-AC77-FECCED2EEEA5");
    private static readonly Guid EnergySaverStatus = new("550E8400-E29B-41D4-A716-446655440000");

    private static DispatcherTimer? _timer;
    private static Dispatcher? _dispatcher;
    private static NotifyCallback? _callback;   // kept alive: Windows holds a pointer to it
    private static volatile bool _polled, _batterySaver, _energySaver;

    /// <summary>True while Energy Saver is on.</summary>
    public static bool IsOn { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="IsOn"/> changes.</summary>
    public static event Action? Changed;

    /// <summary>Read the current state and start watching it. Call once, on the UI thread.</summary>
    public static void Start()
    {
        if (_timer != null) return;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _polled = Query();
        IsOn = _polled;

        Register(PowerSavingStatus);
        Register(EnergySaverStatus);

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) =>
        {
            _polled = Query();
            Update();
        };
        _timer.Start();
    }

    private static void Register(Guid setting)
    {
        try
        {
            _callback ??= OnPowerSetting;
            var sub = new NotifySubscribe { Callback = Marshal.GetFunctionPointerForDelegate(_callback), Context = IntPtr.Zero };
            IntPtr recipient = Marshal.AllocHGlobal(Marshal.SizeOf<NotifySubscribe>());   // lives as long as the app
            Marshal.StructureToPtr(sub, recipient, false);
            PowerSettingRegisterNotification(ref setting, DEVICE_NOTIFY_CALLBACK, recipient, out _);
        }
        catch { /* the poll below still covers the old flag */ }
    }

    /// <summary>Called by Windows on one of its own threads: POWERBROADCAST_SETTING is a 16-byte GUID,
    /// a 4-byte length, then the value.</summary>
    private static uint OnPowerSetting(IntPtr context, uint type, IntPtr setting)
    {
        try
        {
            if (type != PBT_POWERSETTINGCHANGE || setting == IntPtr.Zero) return 0;
            var guid = Marshal.PtrToStructure<Guid>(setting);
            uint length = (uint)Marshal.ReadInt32(setting, 16);
            if (length < 4) return 0;
            int value = Marshal.ReadInt32(setting, 20);
            if (guid == PowerSavingStatus) _batterySaver = value != 0;
            else if (guid == EnergySaverStatus) _energySaver = value != 0;
            else return 0;
            _dispatcher?.BeginInvoke(Update);
        }
        catch { }
        return 0;
    }

    private static bool Query()
    {
        try { return GetSystemPowerStatus(out var s) && s.SystemStatusFlag == 1; }
        catch { return false; }
    }

    private static void Update()
    {
        bool now = _polled || _batterySaver || _energySaver;
        if (now == IsOn) return;
        IsOn = now;
        Changed?.Invoke();
    }
}
