// Mac-Win Bridge: DXGI Desktop Duplication screen capture engine.
// Captures frames from a specified monitor using the Windows Desktop Duplication API.

using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MacWinBridge.Display.Capture;

/// <summary>
/// Event args containing a captured frame's pixel data.
/// Uses ArrayPool for zero-alloc frame delivery.
/// </summary>
public sealed class FrameCapturedEventArgs : EventArgs
{
    public byte[] PixelData { get; init; } = Array.Empty<byte>();
    /// <summary>Actual data length (may be smaller than PixelData.Length if pooled).</summary>
    public int PixelDataLength { get; init; }
    /// <summary>True if PixelData is rented from ArrayPool and must be returned by consumer.</summary>
    public bool IsPooled { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Stride { get; init; }
    public long TimestampTicks { get; init; }
    public int FrameNumber { get; init; }
}

/// <summary>
/// Captures desktop frames using DXGI Output Duplication (Desktop Duplication API).
/// This is the most efficient way to capture the screen on Windows 8+.
/// </summary>
public sealed class DesktopDuplicationCapture : IDisposable
{
    private readonly ILogger<DesktopDuplicationCapture> _logger;
    private readonly int _outputIndex;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _stagingTexture;

    private int _width;
    private int _height;
    private int _frameCount;
    private bool _disposed;

    public event EventHandler<FrameCapturedEventArgs>? FrameCaptured;

    public int Width => _width;
    public int Height => _height;
    public bool IsInitialized => _duplication is not null;

    public DesktopDuplicationCapture(ILogger<DesktopDuplicationCapture> logger, int outputIndex = 1)
    {
        _logger = logger;
        _outputIndex = outputIndex;
    }

    /// <summary>
    /// Initialize DXGI resources for the target output (monitor).
    /// </summary>
    public void Initialize()
    {
        // Create D3D11 device
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null,
            out _device,
            out _context);

        if (_device is null || _context is null)
            throw new InvalidOperationException("Failed to create D3D11 device");

        // Get DXGI adapter and output
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();

        adapter.EnumOutputs(_outputIndex, out var output);
        if (output is null)
            throw new InvalidOperationException($"Monitor at index {_outputIndex} not found");

        var outputDesc = output.Description;
        _width = outputDesc.DesktopCoordinates.Right - outputDesc.DesktopCoordinates.Left;
        _height = outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top;

        _logger.LogInformation("Targeting output {Index}: {Name} ({W}x{H})",
            _outputIndex, outputDesc.DeviceName, _width, _height);

        // Create desktop duplication
        using var output1 = output.QueryInterface<IDXGIOutput1>();
        _duplication = output1.DuplicateOutput(_device);

        // Create staging texture for CPU readback
        var stagingDesc = new Texture2DDescription
        {
            Width = _width,
            Height = _height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        };
        _stagingTexture = _device.CreateTexture2D(stagingDesc);

        _logger.LogInformation("Desktop Duplication initialized for output {Index}", _outputIndex);
    }

    /// <summary>
    /// Capture a single frame. Returns true if a new frame was acquired.
    /// </summary>
    public bool CaptureFrame(TimeSpan timeout)
    {
        if (_duplication is null || _context is null || _stagingTexture is null)
            return false;

        IDXGIResource? desktopResource = null;
        try
        {
            var result = _duplication.AcquireNextFrame(
                (int)timeout.TotalMilliseconds,
                out var frameInfo,
                out desktopResource);

            if (result.Failure || desktopResource is null)
                return false;

            // Copy desktop texture to staging texture
            using var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>();
            _context.CopyResource(_stagingTexture, desktopTexture);

            // Map staging texture for CPU read
            var mapped = _context.Map(_stagingTexture, 0, MapMode.Read);
            try
            {
                var stride = (int)mapped.RowPitch;
                var dataSize = stride * _height;

                // Rent from ArrayPool to avoid per-frame GC allocation
                var pixelData = ArrayPool<byte>.Shared.Rent(dataSize);
                
                unsafe
                {
                    var srcPtr = (byte*)mapped.DataPointer;
                    // Direct copy: single memcpy
                    fixed (byte* dst = pixelData)
                    {
                        Buffer.MemoryCopy(srcPtr, dst, dataSize, dataSize);
                    }
                }

                _frameCount++;
                FrameCaptured?.Invoke(this, new FrameCapturedEventArgs
                {
                    PixelData = pixelData,
                    PixelDataLength = dataSize,
                    IsPooled = true,
                    Width = _width,
                    Height = _height,
                    Stride = stride,
                    TimestampTicks = frameInfo.LastPresentTime,
                    FrameNumber = _frameCount,
                });
            }
            finally
            {
                _context.Unmap(_stagingTexture, 0);
            }

            return true;
        }
        finally
        {
            desktopResource?.Dispose();
            _duplication?.ReleaseFrame();
        }
    }

    /// <summary>
    /// Run a continuous capture loop with high-precision timing.
    /// </summary>
    public async Task RunCaptureLoopAsync(int targetFps, CancellationToken ct)
    {
        var frameInterval = TimeSpan.FromMilliseconds(1000.0 / targetFps);
        _logger.LogInformation("Starting capture loop at {Fps} FPS", targetFps);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            var frameStart = sw.Elapsed;
            
            CaptureFrame(TimeSpan.FromMilliseconds(100));

            var elapsed = sw.Elapsed - frameStart;
            var sleepTime = frameInterval - elapsed;
            if (sleepTime > TimeSpan.FromMilliseconds(1))
            {
                // SpinWait for sub-ms precision when close to deadline
                if (sleepTime.TotalMilliseconds > 2)
                    await Task.Delay(sleepTime - TimeSpan.FromMilliseconds(1), ct);
                while (sw.Elapsed - frameStart < frameInterval)
                    Thread.SpinWait(100);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _duplication?.Dispose();
        _stagingTexture?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
    }
}
