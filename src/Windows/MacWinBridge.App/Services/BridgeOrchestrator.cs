// Mac-Win Bridge: Orchestrator – manages audio bridge service.

using MacWinBridge.Audio;
using MacWinBridge.Core.Configuration;
using MacWinBridge.Core.Discovery;
using MacWinBridge.Core.Transport;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.App.Services;

/// <summary>
/// Central orchestrator that manages the audio bridge service lifecycle.
/// </summary>
public sealed class BridgeOrchestrator : IAsyncDisposable
{
    private readonly ILogger<BridgeOrchestrator> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly BridgeConfig _config;

    // Transports
    private BridgeTransport? _controlTransport;
    private BridgeTransport? _audioTransport;

    // Services
    public AudioStreamService? AudioService { get; private set; }

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
    /// Connect to the Mac companion and start audio service.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        StatusMessage?.Invoke(this, "Macを検索中...");

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
            _controlTransport = new BridgeTransport(
                _loggerFactory.CreateLogger<BridgeTransport>());
            _audioTransport = new BridgeTransport(
                _loggerFactory.CreateLogger<BridgeTransport>());

            await _controlTransport.ConnectAsync(macHost, _config.ControlPort, ct);
            await _audioTransport.ConnectAsync(macHost, _config.AudioPort, ct);

            _controlTransport.Disconnected += (_, _) => OnDisconnected();

            AudioService = new AudioStreamService(
                _loggerFactory.CreateLogger<AudioStreamService>(),
                _loggerFactory, _config, _audioTransport);

            await AudioService.StartAsync();

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
    /// Change audio routing direction.
    /// </summary>
    public async Task SetAudioRoutingAsync(AudioRouting routing)
    {
        if (AudioService is null) return;
        await AudioService.SetRoutingAsync(routing);
        var desc = routing switch
        {
            AudioRouting.WindowsToMac => "音声: Win→Mac 🍎",
            AudioRouting.MacToWindows => "音声: Mac→Win 🪟",
            AudioRouting.Both         => "音声: 両方で再生 🔀",
            _                         => "音声: 不明",
        };
        StatusMessage?.Invoke(this, desc);
    }

    /// <summary>
    /// Disconnect from Mac and stop all services.
    /// </summary>
    public async Task DisconnectAsync()
    {
        AudioService?.Stop();
        if (AudioService is not null)
            await AudioService.DisposeAsync();
        AudioService = null;

        if (_controlTransport is not null)
            await _controlTransport.DisposeAsync();
        if (_audioTransport is not null)
            await _audioTransport.DisposeAsync();

        _controlTransport = null;
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

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
