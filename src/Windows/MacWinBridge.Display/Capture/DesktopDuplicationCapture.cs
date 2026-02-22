// Mac-Win Bridge: DXGI Desktop Duplication screen capture engine.
// Key design decisions:
//  - AcquireNextFrame timeout controls pacing; no SpinWait, no Task.Delay, no Thread.Sleep.
//  - Only frames with actual desktop image updates (LastPresentTime != 0) are delivered.
//  - access-lost errors trigger automatic recreation of the duplication object.
//  - Frame delivery is synchronous via event; caller must decouple encode/send to a Channel.

using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace MacWinBridge.Display.Capture;

public sealed class FrameCapturedEventArgs : EventArgs
{
    public byte[] PixelData       { get; init; } = Array.Empty<byte>();
    public int    PixelDataLength { get; init; }
    public bool   IsPooled        { get; init; }
    public int    Width           { get; init; }
    public int    Height          { get; init; }
    public int    Stride          { get; init; }
    public long   TimestampTicks  { get; init; }
    public int    FrameNumber     { get; init; }
}

public sealed class DesktopDuplicationCapture : IDisposable
{
    private readonly ILogger<DesktopDuplicationCapture> _logger;
    private readonly int _outputIndex;

    private ID3D11Device?           _device;
    private ID3D11DeviceContext?    _context;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D?        _stagingTexture;

    private int  _width;
    private int  _height;
    private int  _frameCount;
    private bool _disposed;

    // DXGI_ERROR_WAIT_TIMEOUT — no new frame returned, not a real error
    private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
    // DXGI_ERROR_ACCESS_LOST — resolution change / desktop switch
    private const int DXGI_ERROR_ACCESS_LOST  = unchecked((int)0x887A0026);

    public event EventHandler<FrameCapturedEventArgs>? FrameCaptured;

    public int  Width         => _width;
    public int  Height        => _height;
    public bool IsInitialized => _duplication is not null;

    public DesktopDuplicationCapture(ILogger<DesktopDuplicationCapture> logger, int outputIndex = 1)
    {
        _logger      = logger;
        _outputIndex = outputIndex;
    }

    public void Initialize()
    {
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            null,
            out _device,
            out _context);

        if (_device is null || _context is null)
            throw new InvalidOperationException("Failed to create D3D11 device");

        CreateDuplication();
        _logger.LogInformation("Desktop Duplication initialized for output {Index} ({W}x{H})",
            _outputIndex, _width, _height);
    }

    private void CreateDuplication()
    {
        if (_device is null) return;

        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter    = dxgiDevice.GetAdapter();

        adapter.EnumOutputs(_outputIndex, out var output);
        if (output is null)
            throw new InvalidOperationException($"Monitor at index {_outputIndex} not found");

        var desc = output.Description;
        _width  = desc.DesktopCoordinates.Right  - desc.DesktopCoordinates.Left;
        _height = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;

        using var output1 = output.QueryInterface<IDXGIOutput1>();
        _duplication = output1.DuplicateOutput(_device);

        // (Re)create staging texture whenever duplication is (re)created
        _stagingTexture?.Dispose();
        _stagingTexture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width             = _width,
            Height            = _height,
            MipLevels         = 1,
            ArraySize         = 1,
            Format            = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage             = ResourceUsage.Staging,
            CPUAccessFlags    = CpuAccessFlags.Read,
        });
    }

    /// <summary>
    /// Acquire one frame from DXGI.
    /// Blocks for up to <paramref name="timeoutMs"/> ms — no extra sleep needed in the loop.
    /// Returns true if a new frame was delivered via FrameCaptured.
    /// Must NOT be called concurrently.
    /// </summary>
    public bool CaptureFrame(int timeoutMs = 50)
    {
        if (_duplication is null || _context is null || _stagingTexture is null)
            return false;

        IDXGIResource? resource = null;
        try
        {
            var result = _duplication.AcquireNextFrame(timeoutMs, out var info, out resource);

            if (result.Code == DXGI_ERROR_WAIT_TIMEOUT) return false;
            if (result.Code == DXGI_ERROR_ACCESS_LOST)
            {
                _logger.LogWarning("DXGI access lost – recreating duplication");
                TryRecreate();
                return false;
            }
            if (result.Failure || resource is null) return false;

            // Skip cursor-only updates that carry no new desktop image
            if (info.LastPresentTime == 0) return false;

            using var tex = resource.QueryInterface<ID3D11Texture2D>();
            _context.CopyResource(_stagingTexture, tex);

            var mapped = _context.Map(_stagingTexture, 0, MapMode.Read);
            try
            {
                var stride   = (int)mapped.RowPitch;
                var dataSize = stride * _height;
                var buf      = ArrayPool<byte>.Shared.Rent(dataSize);

                unsafe
                {
                    fixed (byte* dst = buf)
                        Buffer.MemoryCopy((byte*)mapped.DataPointer, dst, dataSize, dataSize);
                }

                _frameCount++;
                FrameCaptured?.Invoke(this, new FrameCapturedEventArgs
                {
                    PixelData       = buf,
                    PixelDataLength = dataSize,
                    IsPooled        = true,
                    Width           = _width,
                    Height          = _height,
                    Stride          = stride,
                    TimestampTicks  = info.LastPresentTime,
                    FrameNumber     = _frameCount,
                });
            }
            finally
            {
                _context.Unmap(_stagingTexture, 0);
            }

            return true;
        }
        catch (Exception ex) when (!_disposed)
        {
            _logger.LogWarning(ex, "CaptureFrame error – attempting recovery");
            TryRecreate();
            return false;
        }
        finally
        {
            resource?.Dispose();
            try { _duplication?.ReleaseFrame(); } catch { }
        }
    }

    /// <summary>
    /// Capture loop paced entirely by AcquireNextFrame — no SpinWait, no Sleep.
    /// </summary>
    public async Task RunCaptureLoopAsync(int targetFps, CancellationToken ct)
    {
        int timeoutMs = Math.Max(16, 1000 / targetFps + 4);
        _logger.LogInformation("Capture loop started (target {Fps} fps, DXGI timeout {T} ms)",
            targetFps, timeoutMs);

        await Task.Yield();  // yield so caller gets the Task handle before blocking starts

        while (!ct.IsCancellationRequested)
            CaptureFrame(timeoutMs);

        _logger.LogInformation("Capture loop stopped after {N} frames", _frameCount);
    }

    private void TryRecreate()
    {
        try
        {
            _duplication?.Dispose();
            _duplication = null;
            CreateDuplication();
            _logger.LogInformation("Duplication recreated");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recreate duplication – streaming may degrade");
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
