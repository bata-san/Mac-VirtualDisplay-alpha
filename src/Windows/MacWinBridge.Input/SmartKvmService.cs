// Mac-Win Bridge: Smart KVM service.
// Detects when mouse crosses to the second monitor and forwards input to Mac.

using System.Runtime.InteropServices;
using MacWinBridge.Core.Configuration;
using MacWinBridge.Core.Protocol;
using MacWinBridge.Core.Transport;
using MacWinBridge.Input.Hooks;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Input;

/// <summary>
/// The Smart KVM service manages seamless input switching between Windows and Mac.
/// 
/// When the mouse reaches the edge of the designated monitor, input is redirected
/// to the Mac companion. The mouse cursor is "captured" at the screen edge on Windows,
/// while the Mac receives relative mouse movements.
/// </summary>
public sealed class SmartKvmService : IDisposable
{
    private readonly ILogger<SmartKvmService> _logger;
    private readonly BridgeConfig _config;
    private readonly BridgeTransport _controlTransport;
    private readonly GlobalInputHook _hook;

    private bool _macControlActive;
    private int _edgeX, _edgeY, _edgeLength;
    private bool _isVerticalEdge;
    private int _lastMacX, _lastMacY;
    private int _monitorWidth, _monitorHeight;

    // Win32 for cursor control
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(ref RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(IntPtr lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public bool IsMacControlActive => _macControlActive;
    public event EventHandler<bool>? MacControlChanged;

    public SmartKvmService(
        ILogger<SmartKvmService> logger,
        BridgeConfig config,
        BridgeTransport controlTransport)
    {
        _logger = logger;
        _config = config;
        _controlTransport = controlTransport;
        _hook = new GlobalInputHook(logger as ILogger<GlobalInputHook>
            ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GlobalInputHook>.Instance);
    }

    /// <summary>
    /// Start the Smart KVM service.
    /// </summary>
    public void Start()
    {
        if (!_config.Input.Enabled)
        {
            _logger.LogInformation("Smart KVM is disabled in config");
            return;
        }

        // Calculate the transition edge based on config
        CalculateTransitionEdge();

        // Install global hooks
        _hook.MouseEvent += OnMouseEvent;
        _hook.KeyboardEvent += OnKeyboardEvent;
        _hook.Install();

        _logger.LogInformation("Smart KVM started. Transition edge: {Edge} at ({X},{Y}), length={Len}",
            _config.Input.TransitionEdge, _edgeX, _edgeY, _edgeLength);
    }

    /// <summary>
    /// Stop the Smart KVM service.
    /// </summary>
    public void Stop()
    {
        _hook.MouseEvent -= OnMouseEvent;
        _hook.KeyboardEvent -= OnKeyboardEvent;
        _hook.Uninstall();

        if (_macControlActive)
            DeactivateMacControl();

        _logger.LogInformation("Smart KVM stopped");
    }

    /// <summary>
    /// Force toggle between Windows and Mac control.
    /// </summary>
    public void ToggleControl()
    {
        if (_macControlActive)
            DeactivateMacControl();
        else
            ActivateMacControl();
    }

    private void CalculateTransitionEdge()
    {
        // Get monitor info (we need the display module's MonitorManager)
        // For now, use a simplified approach based on screen bounds
        var screens = System.Windows.Forms.Screen.AllScreens;
        var targetIndex = Math.Min(_config.Display.TargetMonitorIndex, screens.Length - 1);
        var targetScreen = screens[targetIndex];

        _monitorWidth = targetScreen.Bounds.Width;
        _monitorHeight = targetScreen.Bounds.Height;

        var edge = _config.Input.TransitionEdge;
        switch (edge)
        {
            case ScreenEdge.Right:
                _edgeX = targetScreen.Bounds.Right - 1;
                _edgeY = targetScreen.Bounds.Top;
                _edgeLength = targetScreen.Bounds.Height;
                _isVerticalEdge = true;
                break;
            case ScreenEdge.Left:
                _edgeX = targetScreen.Bounds.Left;
                _edgeY = targetScreen.Bounds.Top;
                _edgeLength = targetScreen.Bounds.Height;
                _isVerticalEdge = true;
                break;
            case ScreenEdge.Top:
                _edgeX = targetScreen.Bounds.Left;
                _edgeY = targetScreen.Bounds.Top;
                _edgeLength = targetScreen.Bounds.Width;
                _isVerticalEdge = false;
                break;
            case ScreenEdge.Bottom:
                _edgeX = targetScreen.Bounds.Left;
                _edgeY = targetScreen.Bounds.Bottom - 1;
                _edgeLength = targetScreen.Bounds.Width;
                _isVerticalEdge = false;
                break;
        }
    }

    private void OnMouseEvent(object? sender, MouseHookEventArgs e)
    {
        if (_macControlActive)
        {
            // When Mac control is active, capture all mouse events
            HandleMacMouseEvent(e);
            return;
        }

        // Check if cursor hit the transition edge
        if (e.Action == MouseHookAction.Move && IsAtTransitionEdge(e.X, e.Y))
        {
            ActivateMacControl();
            e.Handled = true;
        }
    }

    private void OnKeyboardEvent(object? sender, KeyboardHookEventArgs e)
    {
        // Check for toggle hotkey (Ctrl+Alt+K by default)
        if (IsToggleHotkey(e))
        {
            if (e.IsKeyDown)
                ToggleControl();
            e.Handled = true;
            return;
        }

        if (_macControlActive)
        {
            // Forward keyboard events to Mac
            ForwardKeyboardEvent(e);
            e.Handled = true;
        }
    }

    private bool IsAtTransitionEdge(int x, int y)
    {
        var deadZone = _config.Input.DeadZonePixels;

        return _config.Input.TransitionEdge switch
        {
            ScreenEdge.Right => x >= _edgeX - deadZone
                && y >= _edgeY && y < _edgeY + _edgeLength,
            ScreenEdge.Left => x <= _edgeX + deadZone
                && y >= _edgeY && y < _edgeY + _edgeLength,
            ScreenEdge.Top => y <= _edgeY + deadZone
                && x >= _edgeX && x < _edgeX + _edgeLength,
            ScreenEdge.Bottom => y >= _edgeY - deadZone
                && x >= _edgeX && x < _edgeX + _edgeLength,
            _ => false,
        };
    }

    private void ActivateMacControl()
    {
        _macControlActive = true;
        _lastMacX = _monitorWidth / 2;
        _lastMacY = _monitorHeight / 2;

        // Lock cursor to the edge area on Windows
        var clipRect = new RECT
        {
            Left = _edgeX - 2,
            Top = _edgeY,
            Right = _edgeX + 2,
            Bottom = _edgeY + _edgeLength,
        };
        ClipCursor(ref clipRect);

        // Notify Mac to activate cursor
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            Active = true,
            InitialX = _lastMacX,
            InitialY = _lastMacY,
        });
        _ = _controlTransport.SendAsync(MessageType.MouseMove, payload);

        MacControlChanged?.Invoke(this, true);
        _logger.LogInformation("Mac control activated");
    }

    private void DeactivateMacControl()
    {
        _macControlActive = false;

        // Release cursor clip
        ClipCursor(IntPtr.Zero);

        MacControlChanged?.Invoke(this, false);
        _logger.LogInformation("Mac control deactivated");
    }

    private void HandleMacMouseEvent(MouseHookEventArgs e)
    {
        e.Handled = true;

        switch (e.Action)
        {
            case MouseHookAction.Move:
                // Check if mouse moved back towards Windows
                var movedBack = _config.Input.TransitionEdge switch
                {
                    ScreenEdge.Right => e.X < _edgeX - _config.Input.DeadZonePixels * 2,
                    ScreenEdge.Left => e.X > _edgeX + _config.Input.DeadZonePixels * 2,
                    ScreenEdge.Top => e.Y > _edgeY + _config.Input.DeadZonePixels * 2,
                    ScreenEdge.Bottom => e.Y < _edgeY - _config.Input.DeadZonePixels * 2,
                    _ => false,
                };

                if (movedBack)
                {
                    DeactivateMacControl();
                    return;
                }

                // Send relative mouse movement to Mac
                ForwardMouseMove(e.X, e.Y);
                break;

            case MouseHookAction.LeftDown:
            case MouseHookAction.LeftUp:
            case MouseHookAction.RightDown:
            case MouseHookAction.RightUp:
            case MouseHookAction.MiddleDown:
            case MouseHookAction.MiddleUp:
                ForwardMouseButton(e.Action);
                break;

            case MouseHookAction.Wheel:
            case MouseHookAction.HWheel:
                ForwardMouseScroll(e.Action, e.WheelDelta);
                break;
        }
    }

    private void ForwardMouseMove(int screenX, int screenY)
    {
        var payload = new byte[8];
        BitConverter.GetBytes(screenX).CopyTo(payload, 0);
        BitConverter.GetBytes(screenY).CopyTo(payload, 4);
        _ = _controlTransport.SendAsync(MessageType.MouseMove, payload, MessageFlags.Priority);
    }

    private void ForwardMouseButton(MouseHookAction action)
    {
        var payload = new byte[4];
        BitConverter.GetBytes((int)action).CopyTo(payload, 0);
        _ = _controlTransport.SendAsync(MessageType.MouseButton, payload, MessageFlags.Priority);
    }

    private void ForwardMouseScroll(MouseHookAction action, int delta)
    {
        var payload = new byte[8];
        BitConverter.GetBytes(action == MouseHookAction.HWheel ? 1 : 0).CopyTo(payload, 0);
        BitConverter.GetBytes(delta).CopyTo(payload, 4);
        _ = _controlTransport.SendAsync(MessageType.MouseScroll, payload, MessageFlags.Priority);
    }

    private void ForwardKeyboardEvent(KeyboardHookEventArgs e)
    {
        var type = e.IsKeyDown ? MessageType.KeyDown : MessageType.KeyUp;
        var payload = new byte[12];
        BitConverter.GetBytes(e.VirtualKeyCode).CopyTo(payload, 0);
        BitConverter.GetBytes(e.ScanCode).CopyTo(payload, 4);
        BitConverter.GetBytes(e.IsExtendedKey ? 1 : 0).CopyTo(payload, 8);
        _ = _controlTransport.SendAsync(type, payload, MessageFlags.Priority);
    }

    private bool IsToggleHotkey(KeyboardHookEventArgs e)
    {
        // Default: Ctrl+Alt+K (VK_K = 0x4B)
        // Check if all modifiers are held + the key matches
        if (e.VirtualKeyCode == 0x4B) // 'K'
        {
            var ctrlDown = (GetAsyncKeyState(0xA2) & 0x8000) != 0  // VK_LCONTROL
                        || (GetAsyncKeyState(0xA3) & 0x8000) != 0; // VK_RCONTROL
            var altDown = (GetAsyncKeyState(0xA4) & 0x8000) != 0   // VK_LMENU
                       || (GetAsyncKeyState(0xA5) & 0x8000) != 0;  // VK_RMENU

            return ctrlDown && altDown;
        }
        return false;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    public void Dispose()
    {
        Stop();
        _hook.Dispose();
    }
}
