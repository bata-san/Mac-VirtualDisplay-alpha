// Mac-Win Bridge: Fullscreen renderer for the 2nd monitor.
// Creates a borderless window and renders decoded video textures via D3D11 swap chain.

using System.Runtime.InteropServices;
using MacWinBridge.Display.Monitor;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace MacWinBridge.Display.Rendering;

/// <summary>
/// Full-screen borderless window renderer for the target (2nd) monitor.
/// Uses D3D11 SwapChain for hardware-accelerated texture presentation.
/// </summary>
public sealed class FullScreenRenderer : IDisposable
{
    private readonly ILogger<FullScreenRenderer> _logger;

    private ID3D11Device?        _device;
    private ID3D11DeviceContext?  _context;
    private IDXGISwapChain1?     _swapChain;
    private ID3D11RenderTargetView? _rtv;
    private IntPtr               _hwnd;

    private int _targetWidth;
    private int _targetHeight;
    private bool _initialized;
    private long _framesPresented;

    public ID3D11Device?       Device  => _device;
    public ID3D11DeviceContext? Context => _context;
    public int Width  => _targetWidth;
    public int Height => _targetHeight;
    public bool IsActive => _initialized;
    public long FramesPresented => Interlocked.Read(ref _framesPresented);

    public FullScreenRenderer(ILogger<FullScreenRenderer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initialize the renderer: create D3D11 device, fullscreen window, swap chain.
    /// </summary>
    public void Initialize(MonitorInfo monitor)
    {
        _targetWidth  = monitor.Width;
        _targetHeight = monitor.Height;

        // Create D3D11 device
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0],
            out _device!, out _context!);

        // Create borderless fullscreen window on the target monitor
        _hwnd = CreateFullScreenWindow(monitor);

        // Create swap chain
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        using var factory = adapter.GetParent<IDXGIFactory2>();

        var desc = new SwapChainDescription1
        {
            Width       = _targetWidth,
            Height      = _targetHeight,
            Format      = Format.B8G8R8A8_UNorm,
            Stereo      = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            Scaling     = Scaling.Stretch,
            SwapEffect  = SwapEffect.FlipDiscard,
            AlphaMode   = AlphaMode.Ignore,
            Flags       = SwapChainFlags.None,
        };

        _swapChain = factory.CreateSwapChainForHwnd(_device, _hwnd, desc);

        // Disable Alt+Enter fullscreen toggle (we manage our own window)
        factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAltEnter);

        // Create render target view
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device.CreateRenderTargetView(backBuffer);

        _initialized = true;
        ShowWindow(_hwnd, 5); // SW_SHOW
        _logger.LogInformation("Renderer initialized on monitor: {W}x{H}", _targetWidth, _targetHeight);
    }

    /// <summary>
    /// Present a decoded texture to the screen.
    /// </summary>
    public void Present(ID3D11Texture2D sourceTexture)
    {
        if (!_initialized || _swapChain is null || _context is null) return;

        // Copy source texture to swap chain back buffer
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _context.CopyResource(backBuffer, sourceTexture);

        // Present with VSync (1) or immediate (0)
        _swapChain.Present(1, PresentFlags.None);
        Interlocked.Increment(ref _framesPresented);
    }

    /// <summary>
    /// Show the fullscreen window (when switching to Mac mode).
    /// </summary>
    public void Show()
    {
        if (_hwnd != IntPtr.Zero)
        {
            ShowWindow(_hwnd, 5); // SW_SHOW
            SetForegroundWindow(_hwnd);
        }
    }

    /// <summary>
    /// Hide the fullscreen window (when switching back to Windows mode).
    /// </summary>
    public void Hide()
    {
        if (_hwnd != IntPtr.Zero)
            ShowWindow(_hwnd, 0); // SW_HIDE
    }

    // ── Win32 Window Creation ────────────────────────

    private IntPtr CreateFullScreenWindow(MonitorInfo monitor)
    {
        var className = "MacWinBridge_RendererWindow";

        var wndClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = className,
            hCursor = LoadCursor(IntPtr.Zero, 32512), // IDC_ARROW
        };

        RegisterClassEx(ref wndClass);

        // WS_POPUP for borderless
        const uint WS_POPUP = 0x80000000;
        const uint WS_VISIBLE = 0x10000000;

        var hwnd = CreateWindowEx(
            0x00000008, // WS_EX_TOPMOST
            className,
            "Mac Display",
            WS_POPUP | WS_VISIBLE,
            monitor.Left, monitor.Top, monitor.Width, monitor.Height,
            IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create window: {Marshal.GetLastWin32Error()}");

        _logger.LogInformation("Fullscreen window created at ({X},{Y}) {W}x{H}",
            monitor.Left, monitor.Top, monitor.Width, monitor.Height);

        return hwnd;
    }

    // Static delegate to prevent GC collection
    private static readonly WndProcDelegate _wndProc = WndProc;

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ── Win32 Interop ────────────────────────────────

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll")] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll")] private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? moduleName);

    public void Dispose()
    {
        _rtv?.Dispose();
        _swapChain?.Dispose();
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        _context?.Dispose();
        _device?.Dispose();
        _initialized = false;
        _logger.LogInformation("Renderer disposed");
    }
}
