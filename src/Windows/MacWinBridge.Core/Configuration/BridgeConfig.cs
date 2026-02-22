// Mac-Win Bridge: Application configuration.
// JSON-based settings stored in %APPDATA%\MacWinBridge\config.json.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Core.Configuration;

/// <summary>
/// Root configuration for the Mac-Win Bridge application.
/// Loaded from / saved to JSON on disk.
/// </summary>
public sealed class BridgeConfig
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacWinBridge");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    // ── Connection ───────────────────────────────────
    public string MacHost { get; set; } = "auto";
    public int ControlPort { get; set; } = 42100;
    public int VideoPort   { get; set; } = 42101;
    public int AudioPort   { get; set; } = 42102;

    // ── Sub-configs ──────────────────────────────────
    public DisplayConfig Display { get; set; } = new();
    public AudioConfig   Audio   { get; set; } = new();
    public InputConfig   Input   { get; set; } = new();

    // ── Load / Save ──────────────────────────────────

    public static BridgeConfig Load(ILogger? logger = null)
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<BridgeConfig>(json, JsonOpts);
                if (cfg is not null)
                {
                    logger?.LogInformation("Config loaded from {Path}", ConfigPath);
                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to load config, using defaults");
        }

        var defaultCfg = new BridgeConfig();
        defaultCfg.Save(logger);
        return defaultCfg;
    }

    public void Save(ILogger? logger = null)
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(this, JsonOpts);
            File.WriteAllText(ConfigPath, json);
            logger?.LogInformation("Config saved to {Path}", ConfigPath);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to save config");
        }
    }
}

// ── Display ──────────────────────────────────────────

public sealed class DisplayConfig
{
    /// <summary>Index of the target monitor (0-based). -1 = auto-detect secondary.</summary>
    public int TargetMonitor { get; set; } = -1;

    /// <summary>Alias for TargetMonitor (used by UI layer).</summary>
    [JsonIgnore]
    public int TargetMonitorIndex
    {
        get => TargetMonitor;
        set => TargetMonitor = value;
    }

    /// <summary>Current display mode persisted across sessions.</summary>
    public DisplayModeSetting Mode { get; set; } = DisplayModeSetting.Windows;

    // Video quality (requested from Mac encoder)
    public VideoCodecSetting Codec   { get; set; } = VideoCodecSetting.H264;
    public int  Bitrate              { get; set; } = 20_000_000;  // 20 Mbps default
    public int  Fps                  { get; set; } = 60;
    public string Profile            { get; set; } = "Main";
    public int  GopSize              { get; set; } = 30;          // keyframe every 30 frames
    public QualityPreset Quality     { get; set; } = QualityPreset.Balanced;
}

public enum DisplayModeSetting
{
    Windows,
    Mac,
}

public enum VideoCodecSetting
{
    H264,
    H265,
}

public enum QualityPreset
{
    Performance,  // lower bitrate, lower latency
    Balanced,     // default
    Quality,      // higher bitrate, better image
}

// ── Audio ────────────────────────────────────────────

public sealed class AudioConfig
{
    public bool Enabled             { get; set; } = true;
    public int  SampleRate          { get; set; } = 48000;
    public int  Channels            { get; set; } = 2;
    public int  BitsPerSample       { get; set; } = 16;
    public int  BufferMs            { get; set; } = 20;
    public bool UseOpusCompression  { get; set; } = false;
    public AudioRouting Routing     { get; set; } = AudioRouting.WindowsToMac;
}

public enum AudioRouting
{
    WindowsToMac,
    MacToWindows,
    Both,
    Muted,
}

// ── Input / KVM ──────────────────────────────────────

public sealed class InputConfig
{
    /// <summary>Which edge of the Windows screen triggers the switch to Mac.</summary>
    public ScreenEdge MacEdge { get; set; } = ScreenEdge.Right;

    /// <summary>
    /// Vertical (or horizontal) offset of the Mac screen along the trigger edge,
    /// expressed as a percentage (0.0 = top/left, 1.0 = bottom/right).
    /// Used for coordinate mapping when monitors have different resolutions.
    /// </summary>
    public double MacEdgeOffset { get; set; } = 0.0;

    /// <summary>Dead zone in pixels at the edge before triggering the switch.</summary>
    public int DeadZonePx { get; set; } = 2;

    /// <summary>Enable clipboard synchronization between Windows and Mac.</summary>
    public bool ClipboardSync { get; set; } = false;

    /// <summary>Hotkey to force-toggle KVM focus (emergency escape). Format: "Ctrl+Alt+K".</summary>
    public string ForceToggleHotkey { get; set; } = "Ctrl+Alt+K";

    /// <summary>Mac display width in logical pixels (used for mouse coordinate mapping).</summary>
    public int MacDisplayWidth { get; set; } = 2560;

    /// <summary>Mac display height in logical pixels (used for mouse coordinate mapping).</summary>
    public int MacDisplayHeight { get; set; } = 1600;
}

/// <summary>
/// Which edge of the primary Windows monitor the virtual Mac display is placed at.
/// </summary>
public enum ScreenEdge
{
    Left,
    Right,
    Top,
    Bottom,
}
