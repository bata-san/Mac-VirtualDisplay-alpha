// Mac-Win Bridge: Global mouse/keyboard hooks for input interception.
// When isFocusOnMac is true, all input is captured and forwarded to Mac via TCP.

using System.Diagnostics;
using System.Runtime.InteropServices;
using MacWinBridge.Core.Protocol;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Input;

/// <summary>
/// Windows global low-level hooks for mouse and keyboard.
/// When focus is on Mac (isFocusOnMac=true), intercepts all input and fires
/// events for TCP forwarding. When focus is on Windows, passes through normally.
/// </summary>
public sealed class GlobalInputHook : IDisposable
{
    private readonly ILogger<GlobalInputHook> _logger;

    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;

    // Must be stored as fields to prevent GC collection
    private readonly LowLevelProc _mouseProc;
    private readonly LowLevelProc _keyboardProc;

    private volatile bool _isFocusOnMac;
    private volatile bool _isInstalled;

    // Modifier key state tracking
    private bool _shiftDown, _ctrlDown, _altDown, _winDown;

    public bool IsFocusOnMac
    {
        get => _isFocusOnMac;
        set
        {
            _isFocusOnMac = value;
            _logger.LogDebug("Focus changed: {Side}", value ? "Mac" : "Windows");
        }
    }

    // ── Events ───────────────────────────────────────
    public event Action<int, int>?      MouseMoved;        // x, y (relative to edge)
    public event Action<int>?           MouseButtonAction; // action code
    public event Action<int, int>?      MouseScrolled;     // deltaX, deltaY
    public event Action<int>?           KeyPressed;        // vkCode
    public event Action<int>?           KeyReleased;       // vkCode
    public event Action?                EdgeCrossed;       // cursor crossed the edge to Mac

    public GlobalInputHook(ILogger<GlobalInputHook> logger)
    {
        _logger = logger;
        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;
    }

    public void Install()
    {
        if (_isInstalled) return;

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        var hModule = GetModuleHandle(module.ModuleName);

        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hModule, 0);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, hModule, 0);
        _isInstalled = true;

        _logger.LogInformation("Global input hooks installed");
    }

    public void Uninstall()
    {
        if (!_isInstalled) return;

        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        _mouseHook = IntPtr.Zero;
        _keyboardHook = IntPtr.Zero;
        _isInstalled = false;

        _logger.LogInformation("Global input hooks uninstalled");
    }

    // ── Mouse Hook ───────────────────────────────────

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;

            if (_isFocusOnMac)
            {
                // Forward mouse events to Mac
                switch (msg)
                {
                    case WM_MOUSEMOVE:
                        MouseMoved?.Invoke(info.pt.x, info.pt.y);
                        break;
                    case WM_LBUTTONDOWN:
                        MouseButtonAction?.Invoke(1);
                        break;
                    case WM_LBUTTONUP:
                        MouseButtonAction?.Invoke(2);
                        break;
                    case WM_RBUTTONDOWN:
                        MouseButtonAction?.Invoke(3);
                        break;
                    case WM_RBUTTONUP:
                        MouseButtonAction?.Invoke(4);
                        break;
                    case WM_MBUTTONDOWN:
                        MouseButtonAction?.Invoke(5);
                        break;
                    case WM_MBUTTONUP:
                        MouseButtonAction?.Invoke(6);
                        break;
                    case WM_MOUSEWHEEL:
                        short delta = (short)(info.mouseData >> 16);
                        MouseScrolled?.Invoke(0, delta);
                        break;
                }

                // Block the event from reaching Windows
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    // ── Keyboard Hook ────────────────────────────────

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;

            // Track modifier state always
            UpdateModifierState(info.vkCode, msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);

            // Check for force-toggle hotkey (Ctrl+Alt+K)
            if (info.vkCode == 0x4B && _ctrlDown && _altDown) // K
            {
                if (msg == WM_KEYDOWN)
                {
                    EdgeCrossed?.Invoke();
                    return (IntPtr)1;
                }
            }

            if (_isFocusOnMac)
            {
                // Forward keyboard events to Mac
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    KeyPressed?.Invoke(info.vkCode);
                }
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    KeyReleased?.Invoke(info.vkCode);
                }

                // Block from reaching Windows
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void UpdateModifierState(int vkCode, bool isDown)
    {
        switch (vkCode)
        {
            case 0x10: case 0xA0: case 0xA1: _shiftDown = isDown; break; // VK_SHIFT/LSHIFT/RSHIFT
            case 0x11: case 0xA2: case 0xA3: _ctrlDown  = isDown; break; // VK_CONTROL
            case 0x12: case 0xA4: case 0xA5: _altDown   = isDown; break; // VK_MENU (Alt)
            case 0x5B: case 0x5C:            _winDown   = isDown; break; // VK_LWIN/RWIN
        }
    }

    // ── Win32 Interop ────────────────────────────────

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    private const int WH_MOUSE_LL    = 14;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_SYSKEYUP    = 0x0105;
    private const int WM_MOUSEMOVE   = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP   = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP   = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP   = 0x0208;
    private const int WM_MOUSEWHEEL  = 0x020A;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string lpModuleName);

    public void Dispose() => Uninstall();
}
