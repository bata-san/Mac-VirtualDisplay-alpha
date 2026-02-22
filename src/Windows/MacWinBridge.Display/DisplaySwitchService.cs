// Mac-Win Bridge: Display Switcher service.
// Orchestrates switching the 2nd monitor between Windows and Mac modes.

using MacWinBridge.Core.Configuration;
using MacWinBridge.Core.Protocol;
using MacWinBridge.Core.Transport;
using MacWinBridge.Display.Capture;
using MacWinBridge.Display.Monitor;
using MacWinBridge.Display.Streaming;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Display;

/// <summary>
/// High-level service that manages the display switching workflow.
/// </summary>
public sealed class DisplaySwitchService : IAsyncDisposable
{
    private readonly ILogger<DisplaySwitchService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly BridgeConfig _config;
    private readonly BridgeTransport _videoTransport;

    private DesktopDuplicationCapture? _capture;
    private VideoStreamer? _streamer;
    private DisplayMode _currentMode;

    public DisplayMode CurrentMode => _currentMode;
    public event EventHandler<DisplayMode>? ModeChanged;

    public DisplaySwitchService(
        ILogger<DisplaySwitchService> logger,
        ILoggerFactory loggerFactory,
        BridgeConfig config,
        BridgeTransport videoTransport)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _config = config;
        _videoTransport = videoTransport;
        _currentMode = config.Display.Mode;
    }

    /// <summary>
    /// Switch the 2nd monitor to the specified mode.
    /// </summary>
    public async Task SwitchModeAsync(DisplayMode targetMode)
    {
        if (_currentMode == targetMode)
        {
            _logger.LogInformation("Already in {Mode} mode", targetMode);
            return;
        }

        _logger.LogInformation("Switching display from {From} to {To}", _currentMode, targetMode);

        switch (targetMode)
        {
            case DisplayMode.Mac:
                await ActivateMacModeAsync();
                break;
            case DisplayMode.Windows:
                await ActivateWindowsModeAsync();
                break;
        }

        _currentMode = targetMode;
        _config.Display.Mode = targetMode;
        _config.Save();

        // Notify Mac companion about the mode switch
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            Mode = targetMode.ToString(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        await _videoTransport.SendAsync(MessageType.DisplaySwitch, payload);

        ModeChanged?.Invoke(this, targetMode);
        _logger.LogInformation("Display mode switched to {Mode}", targetMode);
    }

    /// <summary>
    /// Toggle between Windows and Mac mode.
    /// </summary>
    public Task ToggleModeAsync()
    {
        var target = _currentMode == DisplayMode.Windows ? DisplayMode.Mac : DisplayMode.Windows;
        return SwitchModeAsync(target);
    }

    /// <summary>
    /// Get information about all connected monitors.
    /// </summary>
    public List<MonitorInfo> GetMonitors() => MonitorManager.GetMonitors();

    private async Task ActivateMacModeAsync()
    {
        // 1. Initialize desktop duplication capture on the target monitor
        _capture = new DesktopDuplicationCapture(
            _loggerFactory.CreateLogger<DesktopDuplicationCapture>(),
            _config.Display.TargetMonitorIndex);

        _capture.Initialize();

        // 2. Start video streamer
        _streamer = new VideoStreamer(
            _loggerFactory.CreateLogger<VideoStreamer>(),
            _capture,
            _videoTransport);

        await _streamer.SendVideoConfigAsync();
        _streamer.Start(_config.Display.MaxFps);

        _logger.LogInformation("Mac mode activated – streaming from monitor {Index}",
            _config.Display.TargetMonitorIndex);
    }

    private async Task ActivateWindowsModeAsync()
    {
        // Stop streaming and release capture resources
        if (_streamer is not null)
        {
            await _streamer.StopAsync();
            await _streamer.DisposeAsync();
            _streamer = null;
        }

        _capture?.Dispose();
        _capture = null;

        _logger.LogInformation("Windows mode activated – normal dual-screen restored");
    }

    public async ValueTask DisposeAsync()
    {
        if (_streamer is not null)
            await _streamer.DisposeAsync();
        _capture?.Dispose();
    }
}
