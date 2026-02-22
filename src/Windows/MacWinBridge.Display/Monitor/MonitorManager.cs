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
    public int Left      { get; init; }
    public int Top       { get; init; }
    public int Width     { get; init; }
    public int Height    { get; init; }
    public bool IsPrimary { get; init; }
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
                    monitors.Add(new MonitorInfo
                    {
                        DeviceName = info.szDevice,
                        Left       = info.rcMonitor.Left,
                        Top        = info.rcMonitor.Top,
                        Width      = info.rcMonitor.Right - info.rcMonitor.Left,
                        Height     = info.rcMonitor.Bottom - info.rcMonitor.Top,
                        IsPrimary  = (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
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

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);
}
