// Mac-Win Bridge: Application configuration.

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Core.Configuration;

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
    public string MacHost    { get; set; } = "auto";
    public int ControlPort   { get; set; } = 42100;
    public int AudioPort     { get; set; } = 42102;

    // ── Sub-configs ──────────────────────────────────
    public AudioConfig Audio { get; set; } = new();

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
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to save config");
        }
    }
}

// ── Audio ────────────────────────────────────────────

public sealed class AudioConfig
{
    public bool Enabled            { get; set; } = true;
    public int  SampleRate         { get; set; } = 48000;
    public int  Channels           { get; set; } = 2;
    public int  BitsPerSample      { get; set; } = 16;
    public int  BufferMs           { get; set; } = 20;
    public bool UseOpusCompression { get; set; } = false;
    public AudioRouting Routing    { get; set; } = AudioRouting.WindowsToMac;
}

public enum AudioRouting
{
    WindowsToMac,
    MacToWindows,
    Both,
    Muted,
}
