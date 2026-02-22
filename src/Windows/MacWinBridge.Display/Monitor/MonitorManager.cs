// Mac-Win Bridge: Monitor enumeration and control.

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Display.Monitor;

/// <summary>
/// Information about a physical display.
/// </summary>
public sealed class MonitorInfo
{
    public int Index { get; init; }
    public string DeviceName { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    public int Left { get; init; }
    public int Top { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public bool IsPrimary { get; init; }
    public int RefreshRate { get; init; }
}

/// <summary>
/// Utility to enumerate and manage monitors via Win32 API.
/// </summary>
public static class MonitorManager
{
    // ── Win32 interop ──────────────────────────────────
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc, IntPtr lprcClip,
        MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(
        string deviceName, int modeNum, ref DEVMODE devMode);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor,
        ref RECT lprcMonitor, IntPtr dwData);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        // There are more fields but we don't need them
    }

    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int MONITORINFOF_PRIMARY = 1;

    // ── Public API ─────────────────────────────────────

    /// <summary>
    /// Enumerate all connected monitors and return their information.
    /// </summary>
    public static List<MonitorInfo> GetMonitors()
    {
        var monitors = new List<MonitorInfo>();
        int index = 0;

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(hMonitor, ref info))
                {
                    var devMode = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
                    EnumDisplaySettings(info.szDevice, ENUM_CURRENT_SETTINGS, ref devMode);

                    monitors.Add(new MonitorInfo
                    {
                        Index = index,
                        DeviceName = info.szDevice,
                        FriendlyName = $"Monitor {index + 1}",
                        Left = info.rcMonitor.Left,
                        Top = info.rcMonitor.Top,
                        Width = info.rcMonitor.Right - info.rcMonitor.Left,
                        Height = info.rcMonitor.Bottom - info.rcMonitor.Top,
                        IsPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                        RefreshRate = devMode.dmDisplayFrequency,
                    });
                    index++;
                }
                return true;
            }, IntPtr.Zero);

        return monitors;
    }

    /// <summary>
    /// Get the monitor that a screen point belongs to.
    /// </summary>
    public static MonitorInfo? GetMonitorAt(int x, int y)
    {
        return GetMonitors().FirstOrDefault(m =>
            x >= m.Left && x < m.Left + m.Width &&
            y >= m.Top && y < m.Top + m.Height);
    }

    /// <summary>
    /// Get the screen edge coordinates for transitions between monitors.
    /// </summary>
    public static (int x, int y, int length) GetMonitorEdge(
        MonitorInfo monitor, Core.Configuration.ScreenEdge edge)
    {
        return edge switch
        {
            Core.Configuration.ScreenEdge.Right =>
                (monitor.Left + monitor.Width - 1, monitor.Top, monitor.Height),
            Core.Configuration.ScreenEdge.Left =>
                (monitor.Left, monitor.Top, monitor.Height),
            Core.Configuration.ScreenEdge.Top =>
                (monitor.Left, monitor.Top, monitor.Width),
            Core.Configuration.ScreenEdge.Bottom =>
                (monitor.Left, monitor.Top + monitor.Height - 1, monitor.Width),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
    }
}
