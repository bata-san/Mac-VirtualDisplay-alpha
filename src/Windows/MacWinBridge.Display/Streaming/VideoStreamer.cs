// Mac-Win Bridge: Video frame encoder for network streaming.
// Encodes raw BGRA frames with high-performance delta compression.

using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using MacWinBridge.Core.Protocol;
using MacWinBridge.Core.Transport;
using MacWinBridge.Display.Capture;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Display.Streaming;

/// <summary>
/// High-performance video streaming pipeline:
/// Capture → SIMD XOR Delta → Transmit over BridgeTransport.
/// Uses ArrayPool to eliminate per-frame GC allocations.
/// </summary>
public sealed class VideoStreamer : IAsyncDisposable
{
    private readonly ILogger<VideoStreamer> _logger;
    private readonly DesktopDuplicationCapture _capture;
    private readonly BridgeTransport _transport;

    private CancellationTokenSource? _cts;
    private Task? _streamTask;
    private byte[]? _previousFrame;
    private int _previousFrameLength;
    private int _framesSent;
    private long _bytesSent;
    private long _framesSkipped;

    // Pre-allocated frame header (16 bytes: W,H,Stride,FrameNo)
    private const int FrameHeaderSize = 16;

    public int FramesSent => _framesSent;
    public long BytesSent => Interlocked.Read(ref _bytesSent);
    public long FramesSkipped => Interlocked.Read(ref _framesSkipped);
    public bool IsStreaming => _streamTask is not null && !_streamTask.IsCompleted;

    public VideoStreamer(
        ILogger<VideoStreamer> logger,
        DesktopDuplicationCapture capture,
        BridgeTransport transport)
    {
        _logger = logger;
        _capture = capture;
        _transport = transport;
    }

    /// <summary>
    /// Start streaming captured frames to the Mac companion.
    /// </summary>
    public void Start(int targetFps = 60)
    {
        if (IsStreaming) return;

        _cts = new CancellationTokenSource();
        _capture.FrameCaptured += OnFrameCaptured;
        _streamTask = _capture.RunCaptureLoopAsync(targetFps, _cts.Token);

        _logger.LogInformation("Video streaming started at {Fps} FPS target", targetFps);
    }

    /// <summary>
    /// Stop streaming.
    /// </summary>
    public async Task StopAsync()
    {
        _capture.FrameCaptured -= OnFrameCaptured;
        _cts?.Cancel();

        if (_streamTask is not null)
        {
            try { await _streamTask; }
            catch (OperationCanceledException) { }
        }

        _logger.LogInformation("Video streaming stopped. Sent {Frames} frames, {Bytes} bytes",
            _framesSent, _bytesSent);
    }

    /// <summary>
    /// Send video configuration to the Mac so it can set up its receiver.
    /// </summary>
    public async Task SendVideoConfigAsync()
    {
        var config = new VideoConfigPayload
        {
            Width = _capture.Width,
            Height = _capture.Height,
            PixelFormat = "BGRA",
            Codec = "raw",
        };

        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(config);
        await _transport.SendAsync(MessageType.VideoConfig, json);
        _logger.LogInformation("Sent video config: {W}x{H}", config.Width, config.Height);
    }

    private async void OnFrameCaptured(object? sender, FrameCapturedEventArgs e)
    {
        byte[]? rentedDelta = null;
        byte[]? rentedPayload = null;
        try
        {
            var pixelLen = e.PixelDataLength > 0 ? e.PixelDataLength : e.PixelData.Length;
            var flags = MessageFlags.None;
            ReadOnlyMemory<byte> dataToSend;

            if (_previousFrame is not null && _previousFrameLength == pixelLen)
            {
                // Rent a buffer for XOR delta
                rentedDelta = ArrayPool<byte>.Shared.Rent(pixelLen);

                // SIMD-accelerated XOR delta + zero check in single pass
                if (!DeltaEncodeAndCheck(e.PixelData, _previousFrame, rentedDelta, pixelLen))
                {
                    // Frame unchanged — skip entirely
                    Interlocked.Increment(ref _framesSkipped);
                    return;
                }
                dataToSend = new ReadOnlyMemory<byte>(rentedDelta, 0, pixelLen);
                flags = MessageFlags.Compressed;
            }
            else
            {
                // Key frame: send full
                dataToSend = new ReadOnlyMemory<byte>(e.PixelData, 0, pixelLen);
                flags = MessageFlags.KeyFrame;
            }

            // Single allocation: header + payload combined
            var totalLen = FrameHeaderSize + dataToSend.Length;
            rentedPayload = ArrayPool<byte>.Shared.Rent(totalLen);

            // Write frame header directly
            BitConverter.TryWriteBytes(rentedPayload.AsSpan(0, 4), e.Width);
            BitConverter.TryWriteBytes(rentedPayload.AsSpan(4, 4), e.Height);
            BitConverter.TryWriteBytes(rentedPayload.AsSpan(8, 4), e.Stride);
            BitConverter.TryWriteBytes(rentedPayload.AsSpan(12, 4), e.FrameNumber);
            dataToSend.Span.CopyTo(rentedPayload.AsSpan(FrameHeaderSize));

            await _transport.SendAsync(MessageType.VideoFrame,
                new ReadOnlyMemory<byte>(rentedPayload, 0, totalLen), flags);

            // Update previous frame (reuse buffer if same size)
            if (_previousFrame is null || _previousFrame.Length < pixelLen)
            {
                if (_previousFrame is not null)
                    ArrayPool<byte>.Shared.Return(_previousFrame);
                _previousFrame = ArrayPool<byte>.Shared.Rent(pixelLen);
            }
            Buffer.BlockCopy(e.PixelData, 0, _previousFrame, 0, pixelLen);
            _previousFrameLength = pixelLen;

            Interlocked.Increment(ref _framesSent);
            Interlocked.Add(ref _bytesSent, totalLen);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending video frame {Frame}", e.FrameNumber);
        }
        finally
        {
            if (rentedDelta is not null) ArrayPool<byte>.Shared.Return(rentedDelta);
            if (rentedPayload is not null) ArrayPool<byte>.Shared.Return(rentedPayload);
            // Return the pooled pixel data from capture
            if (e.IsPooled) ArrayPool<byte>.Shared.Return(e.PixelData);
        }
    }

    /// <summary>
    /// SIMD-accelerated XOR delta encoding + all-zeros check in a single pass.
    /// Returns true if any pixel changed (i.e. delta is non-zero).
    /// </summary>
    private static bool DeltaEncodeAndCheck(byte[] current, byte[] previous, byte[] result, int length)
    {
        bool hasChange = false;
        var curSpan = current.AsSpan(0, length);
        var prevSpan = previous.AsSpan(0, length);
        var resSpan = result.AsSpan(0, length);

        // Use Vector<byte> for SIMD (auto-vectorized by .NET JIT)
        int vecSize = Vector<byte>.Count;
        int vecEnd = length - (length % vecSize);
        var zeroVec = Vector<byte>.Zero;

        var curVecs = MemoryMarshal.Cast<byte, Vector<byte>>(curSpan);
        var prevVecs = MemoryMarshal.Cast<byte, Vector<byte>>(prevSpan);
        var resVecs = MemoryMarshal.Cast<byte, Vector<byte>>(resSpan);

        for (int i = 0; i < curVecs.Length; i++)
        {
            var xor = curVecs[i] ^ prevVecs[i];
            resVecs[i] = xor;
            if (xor != zeroVec) hasChange = true;
        }

        // Handle remaining bytes
        for (int i = vecEnd; i < length; i++)
        {
            resSpan[i] = (byte)(curSpan[i] ^ prevSpan[i]);
            if (resSpan[i] != 0) hasChange = true;
        }

        return hasChange;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
        if (_previousFrame is not null)
        {
            ArrayPool<byte>.Shared.Return(_previousFrame);
            _previousFrame = null;
        }
    }
}

/// <summary>
/// Video configuration payload sent to the Mac companion.
/// </summary>
internal sealed class VideoConfigPayload
{
    public int Width { get; init; }
    public int Height { get; init; }
    public string PixelFormat { get; init; } = "BGRA";
    public string Codec { get; init; } = "raw";
}
