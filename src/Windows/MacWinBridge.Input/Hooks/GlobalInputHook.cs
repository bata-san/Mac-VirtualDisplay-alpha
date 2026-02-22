// Mac-Win Bridge: Low-level mouse and keyboard hooks via Win32 API.
// Intercepts input events to enable seamless cross-machine control.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Input.Hooks;

/// <summary>
/// Mouse event data from the low-level hook.
/// </summary>
public sealed class MouseHookEventArgs : EventArgs
{
    public int X { get; init; }
    public int Y { get; init; }
    public MouseHookAction Action { get; init; }
    public int WheelDelta { get; init; }
    public int ExtraButton { get; init; }

    /// <summary>Set to true to suppress the event from reaching Windows.</summary>
    public bool Handled { get; set; }
}

public enum MouseHookAction
{
    Move,
    LeftDown, LeftUp,
    RightDown, RightUp,
    MiddleDown, MiddleUp,
    Wheel, HWheel,
    XButtonDown, XButtonUp,
}

/// <summary>
/// Keyboard event data from the low-level hook.
/// </summary>
public sealed class KeyboardHookEventArgs : EventArgs
{
    public int VirtualKeyCode { get; init; }
    public int ScanCode { get; init; }
    public bool IsKeyDown { get; init; }
    public bool IsExtendedKey { get; init; }
    public bool IsAltDown { get; init; }

    /// <summary>Set to true to suppress the event from reaching Windows.</summary>
    public bool Handled { get; set; }
}

/// <summary>
/// Installs global low-level mouse and keyboard hooks using SetWindowsHookEx.
/// These hooks intercept ALL input system-wide, enabling seamless KVM switching.
/// </summary>
public sealed class GlobalInputHook : IDisposable
{
    // ── Win32 Constants and Types ──────────────────────
    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;

    private const int WM_MOUSEMOVE    = 0x0200;
    private const int WM_LBUTTONDOWN  = 0x0201;
    private const int WM_LBUTTONUP    = 0x0202;
    private const int WM_RBUTTONDOWN  = 0x0204;
    private const int WM_RBUTTONUP    = 0x0205;
    private const int WM_MBUTTONDOWN  = 0x0207;
    private const int WM_MBUTTONUP    = 0x0208;
    private const int WM_MOUSEWHEEL   = 0x020A;
    private const int WM_MOUSEHWHEEL  = 0x020E;
    private const int WM_XBUTTONDOWN  = 0x020B;
    private const int WM_XBUTTONUP    = 0x020C;
    private const int WM_KEYDOWN      = 0x0100;
    private const int WM_KEYUP        = 0x0101;
    private const int WM_SYSKEYDOWN   = 0x0104;
    private const int WM_SYSKEYUP     = 0x0105;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
        IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int X, Y;
        public int MouseData;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int VkCode;
        public int ScanCode;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }

    // ── Instance fields ────────────────────────────────
    private readonly ILogger<GlobalInputHook> _logger;
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;
    private readonly HookProc _mouseProc;
    private readonly HookProc _keyboardProc;
    private bool _disposed;

    public event EventHandler<MouseHookEventArgs>? MouseEvent;
    public event EventHandler<KeyboardHookEventArgs>? KeyboardEvent;

    public GlobalInputHook(ILogger<GlobalInputHook> logger)
    {
        _logger = logger;
        // Must keep delegates alive to prevent GC
        _mouseProc = MouseHookCallback;
        _keyboardProc = KeyboardHookCallback;
    }

    /// <summary>
    /// Install global mouse and keyboard hooks.
    /// </summary>
    public void Install()
    {
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule!;
        var hModule = GetModuleHandle(module.ModuleName);

        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hModule, 0);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, hModule, 0);

        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Failed to install hooks: Win32 error {err}");
        }

        _logger.LogInformation("Global input hooks installed");
    }

    /// <summary>
    /// Remove the hooks.
    /// </summary>
    public void Uninstall()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        _logger.LogInformation("Global input hooks removed");
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var action = (int)wParam switch
            {
                WM_MOUSEMOVE   => MouseHookAction.Move,
                WM_LBUTTONDOWN => MouseHookAction.LeftDown,
                WM_LBUTTONUP   => MouseHookAction.LeftUp,
                WM_RBUTTONDOWN => MouseHookAction.RightDown,
                WM_RBUTTONUP   => MouseHookAction.RightUp,
                WM_MBUTTONDOWN => MouseHookAction.MiddleDown,
                WM_MBUTTONUP   => MouseHookAction.MiddleUp,
                WM_MOUSEWHEEL  => MouseHookAction.Wheel,
                WM_MOUSEHWHEEL => MouseHookAction.HWheel,
                WM_XBUTTONDOWN => MouseHookAction.XButtonDown,
                WM_XBUTTONUP   => MouseHookAction.XButtonUp,
                _ => MouseHookAction.Move,
            };

            var args = new MouseHookEventArgs
            {
                X = info.X,
                Y = info.Y,
                Action = action,
                WheelDelta = (short)(info.MouseData >> 16),
                ExtraButton = (info.MouseData >> 16) & 0xFFFF,
            };

            MouseEvent?.Invoke(this, args);

            if (args.Handled)
                return (IntPtr)1; // Suppress the event
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var isKeyDown = (int)wParam is WM_KEYDOWN or WM_SYSKEYDOWN;

            var args = new KeyboardHookEventArgs
            {
                VirtualKeyCode = info.VkCode,
                ScanCode = info.ScanCode,
                IsKeyDown = isKeyDown,
                IsExtendedKey = (info.Flags & 0x01) != 0,
                IsAltDown = (int)wParam is WM_SYSKEYDOWN or WM_SYSKEYUP,
            };

            KeyboardEvent?.Invoke(this, args);

            if (args.Handled)
                return (IntPtr)1; // Suppress the event
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Uninstall();
    }
}
