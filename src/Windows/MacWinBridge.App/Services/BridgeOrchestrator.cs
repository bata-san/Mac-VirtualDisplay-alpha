// Mac-Win Bridge: Orchestrator – coordinates all services.

using System.Timers;
using MacWinBridge.Audio;
using MacWinBridge.Core.Configuration;
using MacWinBridge.Core.Discovery;
using MacWinBridge.Core.Transport;
using MacWinBridge.Display;
using MacWinBridge.Input;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.App.Services;

/// <summary>
/// Central orchestrator that manages the lifecycle of all bridge services.
/// </summary>
public sealed class BridgeOrchestrator : IAsyncDisposable
{
    private readonly ILogger<BridgeOrchestrator> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly BridgeConfig _config;

    // Transports
    private BridgeTransport? _controlTransport;
    private BridgeTransport? _videoTransport;
    private BridgeTransport? _audioTransport;

    // Input hook (owned here; shared with KvmService)
    private GlobalInputHook? _inputHook;

    // Services
    public DisplaySwitchService? DisplayService { get; private set; }
    public AudioStreamService? AudioService { get; private set; }
    public SmartKvmService? KvmService { get; private set; }

    // State
    public bool IsConnected { get; private set; }
    public string? ConnectedMacName { get; private set; }

    // Video statistics (updated by timer)
    public double VideoFps { get; private set; }
    public double DecodingLatencyMs { get; private set; }
    public double BandwidthMbps { get; private set; }
    public string VideoResolution { get; private set; } = "—";

    private System.Timers.Timer? _statsTimer;

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<string>? StatusMessage;
    public event EventHandler? StatsUpdated;

    public BridgeOrchestrator(
        ILogger<BridgeOrchestrator> logger,
        ILoggerFactory loggerFactory,
        BridgeConfig config)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _config = config;
    }

    /// <summary>
    /// Connect to the Mac companion and start all services.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        StatusMessage?.Invoke(this, "Macを検索中...");

        // Discover Mac
        string macHost;
        if (_config.MacHost == "auto")
        {
            var discovery = new BridgeDiscovery(
                _loggerFactory.CreateLogger<BridgeDiscovery>());
            var macIp = await discovery.DiscoverMacAsync(TimeSpan.FromSeconds(10), ct: ct);
            if (macIp is null)
            {
                StatusMessage?.Invoke(this, "Macが見つかりません。IPアドレスを手動設定してください。");
                return;
            }
            macHost = macIp.ToString();
        }
        else
        {
            macHost = _config.MacHost;
        }

        StatusMessage?.Invoke(this, $"Mac ({macHost}) に接続中...");

        try
        {
            // Create and connect transports
            _controlTransport = new BridgeTransport(
                _loggerFactory.CreateLogger<BridgeTransport>());
            _videoTransport = new BridgeTransport(
                _loggerFactory.CreateLogger<BridgeTransport>());
            _audioTransport = new BridgeTransport(
                _loggerFactory.CreateLogger<BridgeTransport>());

            await _controlTransport.ConnectAsync(macHost, _config.ControlPort, ct);
            await _videoTransport.ConnectAsync(macHost, _config.VideoPort, ct);
            await _audioTransport.ConnectAsync(macHost, _config.AudioPort, ct);

            _controlTransport.Disconnected += (_, _) => OnDisconnected();

            // Initialize services
            DisplayService = new DisplaySwitchService(
                _loggerFactory.CreateLogger<DisplaySwitchService>(),
                _loggerFactory, _config, _videoTransport, _controlTransport);

            AudioService = new AudioStreamService(
                _loggerFactory.CreateLogger<AudioStreamService>(),
                _loggerFactory, _config, _audioTransport);

            KvmService = new SmartKvmService(
                _loggerFactory.CreateLogger<SmartKvmService>(),
                _config, _controlTransport,
                _inputHook ??= new GlobalInputHook(_loggerFactory.CreateLogger<GlobalInputHook>()));

            // Start audio streaming (always active for unified audio)
            await AudioService.StartAsync();

            // Start KVM service
            KvmService.Start(_config.Input.MacDisplayWidth, _config.Input.MacDisplayHeight);

            IsConnected = true;
            ConnectedMacName = macHost;
            ConnectionChanged?.Invoke(this, true);
            StatusMessage?.Invoke(this, $"Mac ({macHost}) に接続完了 ✓");

            // Start stats polling
            _statsTimer = new System.Timers.Timer(1000);
            _statsTimer.Elapsed += OnStatsTimerElapsed;
            _statsTimer.Start();

            _logger.LogInformation("Bridge connected to Mac at {Host}", macHost);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Mac at {Host}", macHost);
            StatusMessage?.Invoke(this, $"接続失敗: {ex.Message}");
            await DisconnectAsync();
        }
    }

    /// <summary>
    /// Switch the display mode.
    /// </summary>
    public async Task SwitchDisplayModeAsync(DisplayMode mode)
    {
        if (DisplayService is null)
        {
            _logger.LogWarning("Display service not initialized");
            return;
        }

        if (mode == DisplayMode.Mac)
            await DisplayService.SwitchToMacAsync();
        else
            await DisplayService.SwitchToWindowsAsync();

        StatusMessage?.Invoke(this, mode == DisplayMode.Mac
            ? "ディスプレイ: Macモード 🖥️"
            : "ディスプレイ: Windowsモード 🪟");
    }

    /// <summary>
    /// Change audio routing direction.
    /// </summary>
    public async Task SetAudioRoutingAsync(AudioRouting routing)
    {
        if (AudioService is null)
        {
            _logger.LogWarning("Audio service not initialized");
            return;
        }

        await AudioService.SetRoutingAsync(routing);
        var desc = routing switch
        {
            AudioRouting.WindowsToMac => "音声: Win→Mac 🍎",
            AudioRouting.MacToWindows => "音声: Mac→Win 🪟",
            AudioRouting.Both => "音声: 両方で再生 🔀",
            _ => "音声: 不明",
        };
        StatusMessage?.Invoke(this, desc);
    }

    /// <summary>
    /// Disconnect from Mac and stop all services.
    /// </summary>
    public async Task DisconnectAsync()
    {
        _statsTimer?.Stop();
        _statsTimer?.Dispose();
        _statsTimer = null;

        KvmService?.Stop();
        if (KvmService is not null)
            await KvmService.DisposeAsync();
        KvmService = null;

        AudioService?.Stop();
        if (AudioService is not null)
            await AudioService.DisposeAsync();
        AudioService = null;

        if (DisplayService is not null)
            await DisplayService.DisposeAsync();
        DisplayService = null;

        if (_controlTransport is not null)
            await _controlTransport.DisposeAsync();
        if (_videoTransport is not null)
            await _videoTransport.DisposeAsync();
        if (_audioTransport is not null)
            await _audioTransport.DisposeAsync();

        _controlTransport = null;
        _videoTransport = null;
        _audioTransport = null;

        IsConnected = false;
        ConnectedMacName = null;
        ConnectionChanged?.Invoke(this, false);

        _logger.LogInformation("Bridge disconnected");
    }

    private void OnStatsTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        if (!IsConnected) return;

        // Read video stats from receiver
        if (DisplayService?.Receiver is { } receiver)
        {
            VideoFps = receiver.CurrentFps;
            DecodingLatencyMs = receiver.AverageDecodeMs;
        }
        else
        {
            VideoFps = 0;
            DecodingLatencyMs = 0;
        }

        // Read bandwidth from video transport
        if (_videoTransport is not null)
        {
            BandwidthMbps = _videoTransport.BytesReceived * 8.0 / 1_000_000.0;
            _videoTransport.ResetCounters();
        }

        // Resolution from renderer
        if (DisplayService?.Renderer is { } renderer)
        {
            VideoResolution = $"{renderer.Width}×{renderer.Height}";
        }

        StatsUpdated?.Invoke(this, EventArgs.Empty);
    }

    private void OnDisconnected()
    {
        _logger.LogWarning("Connection to Mac lost");
        StatusMessage?.Invoke(this, "Macとの接続が切断されました");
        _ = DisconnectAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
