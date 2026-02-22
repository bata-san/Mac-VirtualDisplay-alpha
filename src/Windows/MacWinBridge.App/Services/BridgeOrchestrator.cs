// Mac-Win Bridge: Orchestrator – coordinates all services.

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

    // Services
    public DisplaySwitchService? DisplayService { get; private set; }
    public AudioStreamService? AudioService { get; private set; }
    public SmartKvmService? KvmService { get; private set; }

    // State
    public bool IsConnected { get; private set; }
    public string? ConnectedMacName { get; private set; }

    public event EventHandler<bool>? ConnectionChanged;
    public event EventHandler<string>? StatusMessage;

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
            var macIp = await discovery.DiscoverMacAsync(TimeSpan.FromSeconds(10), ct);
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
                _loggerFactory, _config, _videoTransport);

            AudioService = new AudioStreamService(
                _loggerFactory.CreateLogger<AudioStreamService>(),
                _loggerFactory, _config, _audioTransport);

            KvmService = new SmartKvmService(
                _loggerFactory.CreateLogger<SmartKvmService>(),
                _config, _controlTransport);

            // Start audio streaming (always active for unified audio)
            await AudioService.StartAsync();

            // Start KVM service
            KvmService.Start();

            IsConnected = true;
            ConnectedMacName = macHost;
            ConnectionChanged?.Invoke(this, true);
            StatusMessage?.Invoke(this, $"Mac ({macHost}) に接続完了 ✓");

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

        await DisplayService.SwitchModeAsync(mode);
        StatusMessage?.Invoke(this, mode == DisplayMode.Mac
            ? "ディスプレイ: Macモード 🖥️"
            : "ディスプレイ: Windowsモード 🪟");
    }

    /// <summary>
    /// Disconnect from Mac and stop all services.
    /// </summary>
    public async Task DisconnectAsync()
    {
        KvmService?.Stop();
        KvmService?.Dispose();
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
