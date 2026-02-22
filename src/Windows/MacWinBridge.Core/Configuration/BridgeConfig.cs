// Mac-Win Bridge: Application configuration model.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MacWinBridge.Core.Configuration;

/// <summary>
/// Top-level configuration saved to %APPDATA%\MacWinBridge\config.json
/// </summary>
public sealed class BridgeConfig
{
    private static readonly string ConfigDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacWinBridge");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

    // ── Connection ──────────────────────────────────────
    public string MacHost { get; set; } = "auto";          // "auto" = mDNS discovery, or IP
    public int ControlPort { get; set; } = 42100;
    public int VideoPort { get; set; } = 42101;
    public int AudioPort { get; set; } = 42102;

    // ── Display ─────────────────────────────────────────
    public DisplayConfig Display { get; set; } = new();

    // ── Audio ───────────────────────────────────────────
    public AudioConfig Audio { get; set; } = new();

    // ── Input / KVM ─────────────────────────────────────
    public InputConfig Input { get; set; } = new();

    // ── Persistence ─────────────────────────────────────
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static BridgeConfig Load()
    {
        if (!File.Exists(ConfigPath))
            return new BridgeConfig();

        var json = File.ReadAllText(ConfigPath);
        return JsonSerializer.Deserialize<BridgeConfig>(json, JsonOpts) ?? new BridgeConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        var json = JsonSerializer.Serialize(this, JsonOpts);
        File.WriteAllText(ConfigPath, json);
    }
}

public sealed class DisplayConfig
{
    /// <summary>Zero-based index of the monitor used as the "switchable" display.</summary>
    public int TargetMonitorIndex { get; set; } = 1;

    /// <summary>Current active mode.</summary>
    public DisplayMode Mode { get; set; } = DisplayMode.Windows;

    /// <summary>Encoder to use for streaming to Mac.</summary>
    public VideoCodec Codec { get; set; } = VideoCodec.H264;

    /// <summary>Target bitrate in Mbps.</summary>
    public int BitrateMbps { get; set; } = 30;

    /// <summary>Max FPS for capture.</summary>
    public int MaxFps { get; set; } = 60;
}

public sealed class AudioConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Sample rate for audio streaming.</summary>
    public int SampleRate { get; set; } = 48000;

    /// <summary>Number of channels (2 = stereo).</summary>
    public int Channels { get; set; } = 2;

    /// <summary>Bits per sample.</summary>
    public int BitsPerSample { get; set; } = 16;

    /// <summary>Audio buffer size in milliseconds (lower = less latency).</summary>
    public int BufferMs { get; set; } = 10;

    /// <summary>Use Opus compression for lower bandwidth.</summary>
    public bool UseOpusCompression { get; set; } = true;

    /// <summary>Audio routing direction: where to play audio.</summary>
    public AudioRouting Routing { get; set; } = AudioRouting.WindowsToMac;
}

public sealed class InputConfig
{
    public bool Enabled { get; set; } = true;

    /// <summary>Which screen edge triggers Mac control handoff (matches TargetMonitorIndex side).</summary>
    public ScreenEdge TransitionEdge { get; set; } = ScreenEdge.Right;

    /// <summary>Pixels of "dead zone" before handoff triggers, to prevent accidental switches.</summary>
    public int DeadZonePixels { get; set; } = 5;

    /// <summary>Enable clipboard sync between Windows and Mac.</summary>
    public bool ClipboardSync { get; set; } = true;

    /// <summary>Keyboard shortcut to force-toggle KVM mode.</summary>
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+K";
}

public enum DisplayMode
{
    Windows,
    Mac,
}

public enum VideoCodec
{
    H264,
    H265,
    Raw,
}

public enum ScreenEdge
{
    Left,
    Right,
    Top,
    Bottom,
}

/// <summary>
/// Audio routing direction.
/// </summary>
public enum AudioRouting
{
    /// <summary>Windows audio plays on Mac speakers.</summary>
    WindowsToMac,
    /// <summary>Mac audio plays on Windows speakers.</summary>
    MacToWindows,
    /// <summary>Audio plays on both devices simultaneously.</summary>
    Both,
}
