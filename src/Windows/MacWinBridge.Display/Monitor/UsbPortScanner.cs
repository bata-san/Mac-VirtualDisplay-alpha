// Mac-Win Bridge: USB-C / Thunderbolt port information via WMI.
// Scans PnP devices for USB host controllers and Thunderbolt controllers
// to report connection speed of each physical USB-C port.

using System.Management;

namespace MacWinBridge.Display.Monitor;

public enum UsbSpeed
{
    Unknown,
    Usb2,          // 480 Mbps
    Usb3Gen1,      // 5 Gbps  (USB 3.0 / 3.1 Gen1 / 3.2 Gen1)
    Usb3Gen2,      // 10 Gbps (USB 3.1 Gen2 / 3.2 Gen2)
    Usb3Gen2x2,    // 20 Gbps (USB 3.2 Gen2×2)
    Usb4Gen2,      // 20 Gbps (USB4 Gen2)
    Usb4Gen3,      // 40 Gbps (USB4 Gen3)
    Thunderbolt3,  // 40 Gbps
    Thunderbolt4,  // 40 Gbps
    Thunderbolt5,  // 120 Gbps
}

public sealed class UsbPortInfo
{
    public string Name        { get; init; } = "";
    public string DeviceId    { get; init; } = "";
    public UsbSpeed Speed     { get; init; }
    public string SpeedLabel  => Speed switch
    {
        UsbSpeed.Usb2         => "USB 2.0  (480 Mbps)",
        UsbSpeed.Usb3Gen1     => "USB 3.2 Gen 1  (5 Gbps)",
        UsbSpeed.Usb3Gen2     => "USB 3.2 Gen 2  (10 Gbps)",
        UsbSpeed.Usb3Gen2x2   => "USB 3.2 Gen 2×2  (20 Gbps)",
        UsbSpeed.Usb4Gen2     => "USB4 Gen 2  (20 Gbps)",
        UsbSpeed.Usb4Gen3     => "USB4 Gen 3  (40 Gbps)",
        UsbSpeed.Thunderbolt3 => "Thunderbolt 3  (40 Gbps)",
        UsbSpeed.Thunderbolt4 => "Thunderbolt 4  (40 Gbps)",
        UsbSpeed.Thunderbolt5 => "Thunderbolt 5  (120 Gbps)",
        _                     => "不明",
    };

    public string Icon => Speed switch
    {
        UsbSpeed.Thunderbolt3 or UsbSpeed.Thunderbolt4 or UsbSpeed.Thunderbolt5 => "⚡",
        UsbSpeed.Usb4Gen3 or UsbSpeed.Usb4Gen2 => "🔌",
        UsbSpeed.Usb3Gen2x2 or UsbSpeed.Usb3Gen2 or UsbSpeed.Usb3Gen1 => "🔵",
        UsbSpeed.Usb2 => "⚪",
        _ => "❓",
    };
}

public static class UsbPortScanner
{
    /// <summary>
    /// Enumerate USB host controllers and Thunderbolt controllers visible to Windows PnP.
    /// Returns one entry per physical root controller (not per connected device).
    /// </summary>
    public static List<UsbPortInfo> GetPorts()
    {
        var result = new List<UsbPortInfo>();

        try
        {
            // Query USB/Thunderbolt root controllers from Win32_PnPEntity
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID FROM Win32_PnPEntity " +
                "WHERE (ClassGuid = '{36FC9E60-C465-11CF-8056-444553540000}' " +  // USB
                "OR ClassGuid = '{D35FF7CE-B467-4F01-B779-D0E41F7F7456}')");      // Thunderbolt

            foreach (ManagementObject obj in searcher.Get())
            {
                var name     = obj["Name"]?.ToString() ?? "";
                var deviceId = obj["DeviceID"]?.ToString() ?? "";

                // Skip hubs, root hubs and non-controller entries — we only want host controllers
                if (IsController(name))
                {
                    var speed = ClassifySpeed(name, deviceId);
                    if (speed != UsbSpeed.Unknown)
                    {
                        result.Add(new UsbPortInfo
                        {
                            Name     = CleanName(name),
                            DeviceId = deviceId,
                            Speed    = speed,
                        });
                    }
                }
            }

            // If the Thunderbolt ClassGuid query returned nothing (some setups),
            // do a name-based fallback scan.
            if (!result.Any(p => p.Speed >= UsbSpeed.Thunderbolt3))
            {
                using var tbSearcher = new ManagementObjectSearcher(
                    "SELECT Name, DeviceID FROM Win32_PnPEntity " +
                    "WHERE Name LIKE '%Thunderbolt%'");

                foreach (ManagementObject obj in tbSearcher.Get())
                {
                    var name     = obj["Name"]?.ToString() ?? "";
                    var deviceId = obj["DeviceID"]?.ToString() ?? "";
                    var speed    = ClassifySpeed(name, deviceId);
                    if (speed != UsbSpeed.Unknown &&
                        !result.Any(r => r.DeviceId == deviceId))
                    {
                        result.Add(new UsbPortInfo
                        {
                            Name     = CleanName(name),
                            DeviceId = deviceId,
                            Speed    = speed,
                        });
                    }
                }
            }
        }
        catch
        {
            // WMI unavailable (e.g. restricted environment) — return empty
        }

        // De-duplicate by DeviceID and sort fastest-first
        return result
            .DistinctBy(p => p.DeviceId)
            .OrderByDescending(p => p.Speed)
            .ToList();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static bool IsController(string name)
    {
        var n = name.ToUpperInvariant();
        // Accept "controller", "host", "thunderbolt"; reject plain "hub", "composite device"
        return (n.Contains("CONTROLLER") || n.Contains("HOST") || n.Contains("THUNDERBOLT"))
            && !n.Contains("ROOT HUB");
    }

    private static UsbSpeed ClassifySpeed(string name, string deviceId)
    {
        var n = name.ToUpperInvariant();
        var d = deviceId.ToUpperInvariant();

        // ── Thunderbolt ──
        if (n.Contains("THUNDERBOLT"))
        {
            if (n.Contains("5") || n.Contains("GEN5"))          return UsbSpeed.Thunderbolt5;
            if (n.Contains("4") || n.Contains("GEN4"))          return UsbSpeed.Thunderbolt4;
            return UsbSpeed.Thunderbolt3;
        }

        // ── USB4 ──
        if (n.Contains("USB4") || n.Contains("USB 4"))
        {
            if (n.Contains("80") || n.Contains("120"))           return UsbSpeed.Usb4Gen3;
            if (n.Contains("GEN 2") || n.Contains("GEN2"))      return UsbSpeed.Usb4Gen2;
            return UsbSpeed.Usb4Gen3;  // default USB4 to Gen 3
        }

        // ── USB 3.2 Gen 2×2 ──
        if ((n.Contains("3.2") || n.Contains("3,2")) &&
            (n.Contains("2X2") || n.Contains("2 X 2") || n.Contains("20GBPS") || n.Contains("20G")))
            return UsbSpeed.Usb3Gen2x2;

        // ── USB 3.x Gen 2 / 10 Gbps ──
        if (n.Contains("GEN 2") || n.Contains("GEN2") ||
            n.Contains("10GBPS") || n.Contains("10G") ||
            n.Contains("3.1 GEN") || n.Contains("XHCI") && d.Contains("8086") && ContainsAny(n, "3.1", "GEN2"))
            return UsbSpeed.Usb3Gen2;

        // ── USB 3.x (eXtensible = any SuperSpeed) ──
        if (n.Contains("EXTENSIBLE") || n.Contains("XHCI") ||
            n.Contains("SUPERSPEED") || n.Contains("3.0") || n.Contains("3.1") || n.Contains("3.2"))
            return UsbSpeed.Usb3Gen1;

        // ── USB 2.0 ──
        if (n.Contains("2.0") || n.Contains("EHCI") || n.Contains("ENHANCED"))
            return UsbSpeed.Usb2;

        // ── Intel / AMD board-specific heuristics ──
        // Intel Alpine Ridge / Titan Ridge / Ice Lake = TB3/TB4
        if (d.Contains("8086") && ContainsAny(d, "15E8", "15D9", "8A0D", "9A1B", "9A1C"))
            return UsbSpeed.Thunderbolt4;
        if (d.Contains("8086") && ContainsAny(d, "157D", "1577", "1575"))
            return UsbSpeed.Thunderbolt3;

        return UsbSpeed.Unknown;
    }

    private static bool ContainsAny(string source, params string[] tokens) =>
        tokens.Any(t => source.Contains(t, StringComparison.OrdinalIgnoreCase));

    private static string CleanName(string name)
    {
        // Remove vendor noise
        var remove = new[] { "Intel(R) ", "AMD ", "(R)", "(TM)", "  " };
        foreach (var r in remove)
            name = name.Replace(r, " ").Trim();
        return name;
    }
}
