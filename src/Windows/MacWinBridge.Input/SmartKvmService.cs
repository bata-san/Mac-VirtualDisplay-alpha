// Mac-Win Bridge: Smart KVM service.
// Manages transparent cursor traversal between Windows (1st monitor) and Mac (2nd monitor).
// Single isFocusOnMac state drives both mouse and keyboard routing.

using System.Runtime.InteropServices;
using MacWinBridge.Core.Configuration;
using MacWinBridge.Core.Protocol;
using MacWinBridge.Core.Transport;
using MacWinBridge.Display.Monitor;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Input;

/// <summary>
/// Transparent dual-display KVM: cursor crossing the configured edge
/// seamlessly transfers both mouse and keyboard to Mac.
/// </summary>
public sealed class SmartKvmService : IAsyncDisposable
{
    private readonly ILogger<SmartKvmService> _logger;
    private readonly BridgeConfig _config;
    private readonly BridgeTransport _controlTransport;
    private readonly GlobalInputHook _inputHook;

    private bool _isActive;
    private bool _isFocusOnMac;

    // Screen geometry
    private int _winPrimaryLeft, _winPrimaryTop, _winPrimaryRight, _winPrimaryBottom;
    private int _macWidth, _macHeight;
    private int _macScaleFactor = 100;

    public bool IsActive      => _isActive;
    public bool IsFocusOnMac  => _isFocusOnMac;

    public event EventHandler<bool>? FocusChanged;  // true = Mac, false = Windows

    public SmartKvmService(
        ILogger<SmartKvmService> logger,
        BridgeConfig config,
        BridgeTransport controlTransport,
        GlobalInputHook inputHook)
    {
        _logger = logger;
        _config = config;
        _controlTransport = controlTransport;
        _inputHook = inputHook;
    }

    /// <summary>
    /// Start the KVM service: install hooks and begin edge detection.
    /// </summary>
    public void Start(int macWidth, int macHeight, int macScale = 100)
    {
        _macWidth = macWidth;
        _macHeight = macHeight;
        _macScaleFactor = macScale;

        // Get primary monitor geometry
        var monitors = MonitorManager.GetMonitors();
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.First();
        _winPrimaryLeft   = primary.Left;
        _winPrimaryTop    = primary.Top;
        _winPrimaryRight  = primary.Left + primary.Width;
        _winPrimaryBottom = primary.Top + primary.Height;

        // Wire up hook events
        _inputHook.MouseMoved       += OnMouseMoved;
        _inputHook.MouseButtonAction += OnMouseButton;
        _inputHook.MouseScrolled    += OnMouseScrolled;
        _inputHook.KeyPressed       += OnKeyPressed;
        _inputHook.KeyReleased      += OnKeyReleased;
        _inputHook.EdgeCrossed      += OnForceToggle;

        // Listen for CursorReturn from Mac
        _controlTransport.MessageReceived += OnControlMessage;

        _inputHook.Install();
        _isActive = true;

        _logger.LogInformation("KVM started: edge={Edge}, Win primary={W}x{H}, Mac={MW}x{MH}",
            _config.Input.MacEdge, primary.Width, primary.Height, macWidth, macHeight);
    }

    public void Stop()
    {
        _inputHook.MouseMoved       -= OnMouseMoved;
        _inputHook.MouseButtonAction -= OnMouseButton;
        _inputHook.MouseScrolled    -= OnMouseScrolled;
        _inputHook.KeyPressed       -= OnKeyPressed;
        _inputHook.KeyReleased      -= OnKeyReleased;
        _inputHook.EdgeCrossed      -= OnForceToggle;
        _controlTransport.MessageReceived -= OnControlMessage;

        _inputHook.Uninstall();
        ReleaseCursor();
        _isActive = false;

        _logger.LogInformation("KVM stopped");
    }

    // ── Edge Detection & Focus Management ────────────

    private void OnMouseMoved(int x, int y)
    {
        if (!_isFocusOnMac)
        {
            // Check if cursor crossed the configured edge
            if (ShouldSwitchToMac(x, y))
            {
                SwitchToMac(x, y);
            }
        }
        else
        {
            // Already on Mac: forward mouse position (scaled to Mac coordinates)
            var (macX, macY) = ScaleToMac(x, y);
            SendMouseMove(macX, macY);
        }
    }

    private bool ShouldSwitchToMac(int x, int y)
    {
        var dz = _config.Input.DeadZonePx;
        return _config.Input.MacEdge switch
        {
            ScreenEdge.Right  => x >= _winPrimaryRight - dz,
            ScreenEdge.Left   => x <= _winPrimaryLeft + dz,
            ScreenEdge.Top    => y <= _winPrimaryTop + dz,
            ScreenEdge.Bottom => y >= _winPrimaryBottom - dz,
            _ => false,
        };
    }

    private void SwitchToMac(int winX, int winY)
    {
        _isFocusOnMac = true;
        _inputHook.IsFocusOnMac = true;

        // Clip and hide Windows cursor
        ClipCursor();
        ShowCursor(false);

        // Calculate Mac entry point
        var (macX, macY) = CalculateEntryPoint(winX, winY);
        SendMouseMove(macX, macY);

        FocusChanged?.Invoke(this, true);
        _logger.LogDebug("Focus → Mac (entry: {X},{Y})", macX, macY);
    }

    private void SwitchToWindows()
    {
        _isFocusOnMac = false;
        _inputHook.IsFocusOnMac = false;

        // Release cursor
        ReleaseCursor();
        ShowCursor(true);

        FocusChanged?.Invoke(this, false);
        _logger.LogDebug("Focus → Windows");
    }

    private void OnForceToggle()
    {
        if (_isFocusOnMac)
            SwitchToWindows();
        else
            SwitchToMac(_winPrimaryRight, (_winPrimaryTop + _winPrimaryBottom) / 2);
    }

    // ── Coordinate Mapping ───────────────────────────

    private (int macX, int macY) CalculateEntryPoint(int winX, int winY)
    {
        var offset = _config.Input.MacEdgeOffset;
        return _config.Input.MacEdge switch
        {
            ScreenEdge.Right => (
                0, // Enter from Mac's left edge
                (int)((double)(winY - _winPrimaryTop) / (_winPrimaryBottom - _winPrimaryTop) * _macHeight)
            ),
            ScreenEdge.Left => (
                _macWidth - 1, // Enter from Mac's right edge
                (int)((double)(winY - _winPrimaryTop) / (_winPrimaryBottom - _winPrimaryTop) * _macHeight)
            ),
            ScreenEdge.Top => (
                (int)((double)(winX - _winPrimaryLeft) / (_winPrimaryRight - _winPrimaryLeft) * _macWidth),
                _macHeight - 1 // Enter from Mac's bottom edge
            ),
            ScreenEdge.Bottom => (
                (int)((double)(winX - _winPrimaryLeft) / (_winPrimaryRight - _winPrimaryLeft) * _macWidth),
                0 // Enter from Mac's top edge
            ),
            _ => (0, 0),
        };
    }

    private (int macX, int macY) ScaleToMac(int winX, int winY)
    {
        // Scale Windows cursor position to Mac coordinate space
        double relX = (double)(winX - _winPrimaryLeft) / (_winPrimaryRight - _winPrimaryLeft);
        double relY = (double)(winY - _winPrimaryTop) / (_winPrimaryBottom - _winPrimaryTop);
        return ((int)(relX * _macWidth), (int)(relY * _macHeight));
    }

    // ── Input Forwarding ─────────────────────────────

    private async void SendMouseMove(int x, int y)
    {
        var payload = new byte[8];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), x);
        BitConverter.TryWriteBytes(payload.AsSpan(4, 4), y);
        try { await _controlTransport.SendAsync(MessageType.MouseMove, payload); }
        catch { }
    }

    private async void OnMouseButton(int action)
    {
        if (!_isFocusOnMac) return;
        var payload = new byte[4];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), action);
        try { await _controlTransport.SendAsync(MessageType.MouseButton, payload); }
        catch { }
    }

    private async void OnMouseScrolled(int dx, int dy)
    {
        if (!_isFocusOnMac) return;
        var payload = new byte[8];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), dx);
        BitConverter.TryWriteBytes(payload.AsSpan(4, 4), dy);
        try { await _controlTransport.SendAsync(MessageType.MouseScroll, payload); }
        catch { }
    }

    private async void OnKeyPressed(int vkCode)
    {
        if (!_isFocusOnMac) return;
        var payload = new byte[4];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), vkCode);
        try { await _controlTransport.SendAsync(MessageType.KeyDown, payload); }
        catch { }
    }

    private async void OnKeyReleased(int vkCode)
    {
        if (!_isFocusOnMac) return;
        var payload = new byte[4];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), vkCode);
        try { await _controlTransport.SendAsync(MessageType.KeyUp, payload); }
        catch { }
    }

    // ── CursorReturn handler ─────────────────────────

    private void OnControlMessage(object? sender, Core.Transport.MessageReceivedEventArgs e)
    {
        if (e.Header.Type == MessageType.CursorReturn)
        {
            SwitchToWindows();
        }
    }

    // ── Win32 cursor management ──────────────────────

    private void ClipCursor()
    {
        // Clip cursor to primary monitor area (prevent it from going to 2nd monitor)
        var rect = new RECT
        {
            Left   = _winPrimaryLeft,
            Top    = _winPrimaryTop,
            Right  = _winPrimaryRight,
            Bottom = _winPrimaryBottom,
        };
        ClipCursorRect(ref rect);
    }

    private void ReleaseCursor()
    {
        ClipCursorRect(IntPtr.Zero);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "ClipCursor")] private static extern bool ClipCursorRect(ref RECT rect);
    [DllImport("user32.dll", EntryPoint = "ClipCursor")] private static extern bool ClipCursorRect(IntPtr rect);
    [DllImport("user32.dll")] private static extern int ShowCursor(bool bShow);

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }
}
