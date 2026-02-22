// Mac-Win Bridge: Monitor enumeration utility.
// Provides info about connected monitors for renderer targeting.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Display.Monitor;

/// <summary>
/// Information about a physical monitor.
/// </summary>
public sealed class MonitorInfo
{
    public required string DeviceName { get; init; }
    public int Index     { get; init; }
    public int Left      { get; init; }
    public int Top       { get; init; }
    public int Width     { get; init; }
    public int Height    { get; init; }
    public bool IsPrimary { get; init; }
    public int RefreshRate { get; init; }
}

/// <summary>
/// Enumerates connected monitors via Win32 EnumDisplayMonitors.
/// </summary>
public static class MonitorManager
{
    public static List<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
            {
                var info = new MONITORINFOEX();
                info.cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>();
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    var devMode = new DEVMODE();
                    devMode.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
                    int refreshRate = 0;
                    if (EnumDisplaySettings(info.szDevice, ENUM_CURRENT_SETTINGS, ref devMode))
                        refreshRate = (int)devMode.dmDisplayFrequency;

                    monitors.Add(new MonitorInfo
                    {
                        Index      = monitors.Count,
                        DeviceName = info.szDevice,
                        Left       = info.rcMonitor.Left,
                        Top        = info.rcMonitor.Top,
                        Width      = info.rcMonitor.Right - info.rcMonitor.Left,
                        Height     = info.rcMonitor.Bottom - info.rcMonitor.Top,
                        IsPrimary  = (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                        RefreshRate = refreshRate,
                    });
                }
                return true;
            }, IntPtr.Zero);

        return monitors;
    }

    // ── Win32 Interop ────────────────────────────────

    private const uint MONITORINFOF_PRIMARY = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    private const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public uint   dmFields;
        public int    dmPositionX, dmPositionY;
        public uint   dmDisplayOrientation, dmDisplayFixedOutput;
        public int    dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint   dmBitsPerPel;
        public uint   dmPelsWidth, dmPelsHeight;
        public uint   dmDisplayFlags;
        public uint   dmDisplayFrequency;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
}
