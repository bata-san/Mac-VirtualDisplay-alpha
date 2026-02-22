// Mac-Win Bridge: Display switching service.
// Manages the transition between Windows mode (normal dual screen)
// and Mac mode (2nd monitor shows Mac screen via H.264 stream).

using MacWinBridge.Core.Configuration;
using MacWinBridge.Core.Protocol;
using MacWinBridge.Core.Transport;
using MacWinBridge.Display.Decoding;
using MacWinBridge.Display.Monitor;
using MacWinBridge.Display.Rendering;
using MacWinBridge.Display.Streaming;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Display;

public enum DisplayMode
{
    Windows,    // Normal dual-screen, 2nd monitor used by Windows
    Mac,        // 2nd monitor shows Mac screen via streaming
}

/// <summary>
/// Orchestrates the display mode: creates/destroys the video pipeline
/// when switching between Windows and Mac modes.
/// </summary>
public sealed class DisplaySwitchService : IAsyncDisposable
{
    private readonly ILogger<DisplaySwitchService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly BridgeConfig _config;
    private readonly BridgeTransport _videoTransport;
    private readonly BridgeTransport _controlTransport;

    private H264Decoder?        _decoder;
    private FullScreenRenderer? _renderer;
    private VideoReceiver?      _receiver;

    private DisplayMode _currentMode = DisplayMode.Windows;

    public DisplayMode CurrentMode => _currentMode;
    public VideoReceiver? Receiver => _receiver;
    public FullScreenRenderer? Renderer => _renderer;
    public H264Decoder? Decoder => _decoder;

    public event EventHandler<DisplayMode>? ModeChanged;

    public DisplaySwitchService(
        ILogger<DisplaySwitchService> logger,
        ILoggerFactory loggerFactory,
        BridgeConfig config,
        BridgeTransport videoTransport,
        BridgeTransport controlTransport)
    {
        _logger           = logger;
        _loggerFactory    = loggerFactory;
        _config           = config;
        _videoTransport   = videoTransport;
        _controlTransport = controlTransport;
    }

    /// <summary>
    /// Switch to Mac mode: create decoder, renderer, and start receiving video.
    /// </summary>
    public async Task SwitchToMacAsync()
    {
        if (_currentMode == DisplayMode.Mac)
        {
            _logger.LogWarning("Already in Mac mode");
            return;
        }

        // Find target monitor
        var monitors = MonitorManager.GetMonitors();
        var targetIdx = _config.Display.TargetMonitor;
        MonitorInfo? target = null;

        if (targetIdx >= 0 && targetIdx < monitors.Count)
        {
            target = monitors[targetIdx];
        }
        else
        {
            // Auto-detect: first non-primary monitor
            target = monitors.FirstOrDefault(m => !m.IsPrimary);
        }

        if (target is null)
        {
            _logger.LogError("No secondary monitor found for Mac mode");
            return;
        }

        _logger.LogInformation("Switching to Mac mode on monitor: {Name} ({W}x{H})",
            target.DeviceName, target.Width, target.Height);

        // Create renderer (D3D11 fullscreen window on 2nd monitor)
        _renderer = new FullScreenRenderer(
            _loggerFactory.CreateLogger<FullScreenRenderer>());
        _renderer.Initialize(target);

        // Create decoder (uses renderer's D3D11 device)
        _decoder = new H264Decoder(
            _loggerFactory.CreateLogger<H264Decoder>());
        _decoder.Initialize(_renderer.Device!, _renderer.Context!, target.Width, target.Height);

        // Create receiver pipeline
        _receiver = new VideoReceiver(
            _loggerFactory.CreateLogger<VideoReceiver>(),
            _videoTransport, _decoder, _renderer);
        _receiver.Start();

        // Notify Mac to start streaming
        var switchPayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            Mode = "Mac",
            Width = target.Width,
            Height = target.Height,
            Fps = _config.Display.Fps,
            Bitrate = _config.Display.Bitrate,
            Codec = _config.Display.Codec.ToString(),
        });
        await _controlTransport.SendAsync(MessageType.DisplaySwitch, switchPayload);

        _currentMode = DisplayMode.Mac;
        ModeChanged?.Invoke(this, DisplayMode.Mac);
        _logger.LogInformation("Switched to Mac mode");
    }

    /// <summary>
    /// Switch back to Windows mode: stop receiving, destroy pipeline.
    /// </summary>
    public async Task SwitchToWindowsAsync()
    {
        if (_currentMode == DisplayMode.Windows)
        {
            _logger.LogWarning("Already in Windows mode");
            return;
        }

        // Stop pipeline
        _receiver?.Stop();
        _renderer?.Hide();
        _decoder?.Dispose();
        _renderer?.Dispose();

        _receiver = null;
        _decoder  = null;
        _renderer = null;

        // Notify Mac to stop streaming
        var switchPayload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            Mode = "Windows",
        });
        await _controlTransport.SendAsync(MessageType.DisplaySwitch, switchPayload);

        _currentMode = DisplayMode.Windows;
        ModeChanged?.Invoke(this, DisplayMode.Windows);
        _logger.LogInformation("Switched to Windows mode");
    }

    /// <summary>
    /// Toggle between modes.
    /// </summary>
    public async Task ToggleModeAsync()
    {
        if (_currentMode == DisplayMode.Windows)
            await SwitchToMacAsync();
        else
            await SwitchToWindowsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_currentMode == DisplayMode.Mac)
            await SwitchToWindowsAsync();

        if (_receiver is not null)
            await _receiver.DisposeAsync();
    }
}
