// Mac-Win Bridge: Video frame encoder for network streaming.
// Encodes raw BGRA frames to H.264/H.265 for efficient transmission.

using MacWinBridge.Core.Protocol;
using MacWinBridge.Core.Transport;
using MacWinBridge.Display.Capture;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Display.Streaming;

/// <summary>
/// Manages the video streaming pipeline:
/// Capture → Encode → Transmit over BridgeTransport.
/// 
/// For the initial version, frames are transmitted as raw BGRA with optional 
/// simple delta compression. A proper H.264/H.265 encoder (via Media Foundation 
/// or FFmpeg) can be plugged in later.
/// </summary>
public sealed class VideoStreamer : IAsyncDisposable
{
    private readonly ILogger<VideoStreamer> _logger;
    private readonly DesktopDuplicationCapture _capture;
    private readonly BridgeTransport _transport;

    private CancellationTokenSource? _cts;
    private Task? _streamTask;
    private byte[]? _previousFrame;
    private int _framesSent;
    private long _bytesSent;
    private readonly object _statsLock = new();

    public int FramesSent => _framesSent;
    public long BytesSent => Interlocked.Read(ref _bytesSent);
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
        try
        {
            // Simple delta encoding: XOR with previous frame, then check if changed
            byte[] payload;
            var flags = MessageFlags.None;

            if (_previousFrame is not null && _previousFrame.Length == e.PixelData.Length)
            {
                // Delta frame: XOR with previous
                payload = DeltaEncode(e.PixelData, _previousFrame);
                
                // Check if frame actually changed (skip if identical)
                if (IsAllZeros(payload))
                    return;

                flags = MessageFlags.Compressed;
            }
            else
            {
                // Key frame: send full frame
                payload = e.PixelData;
                flags = MessageFlags.KeyFrame;
            }

            // Prepend a small header with frame metadata
            var frameHeader = new byte[16];
            BitConverter.GetBytes(e.Width).CopyTo(frameHeader, 0);
            BitConverter.GetBytes(e.Height).CopyTo(frameHeader, 4);
            BitConverter.GetBytes(e.Stride).CopyTo(frameHeader, 8);
            BitConverter.GetBytes(e.FrameNumber).CopyTo(frameHeader, 12);

            var fullPayload = new byte[frameHeader.Length + payload.Length];
            frameHeader.CopyTo(fullPayload, 0);
            payload.CopyTo(fullPayload, frameHeader.Length);

            await _transport.SendAsync(MessageType.VideoFrame, fullPayload, flags);

            _previousFrame = e.PixelData;
            Interlocked.Increment(ref _framesSent);
            Interlocked.Add(ref _bytesSent, fullPayload.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending video frame {Frame}", e.FrameNumber);
        }
    }

    /// <summary>
    /// XOR delta encoding between current and previous frame.
    /// </summary>
    private static byte[] DeltaEncode(byte[] current, byte[] previous)
    {
        var result = new byte[current.Length];
        
        // Use SIMD-friendly loop (JIT will vectorize)
        for (int i = 0; i < current.Length; i++)
        {
            result[i] = (byte)(current[i] ^ previous[i]);
        }

        return result;
    }

    /// <summary>
    /// Check if a byte array is all zeros (frame unchanged).
    /// </summary>
    private static bool IsAllZeros(byte[] data)
    {
        // Check in 8-byte chunks for performance
        int i = 0;
        int longCount = data.Length / 8;
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(data.AsSpan());
        
        for (int j = 0; j < span.Length; j++)
        {
            if (span[j] != 0) return false;
        }

        // Check remaining bytes
        for (i = span.Length * 8; i < data.Length; i++)
        {
            if (data[i] != 0) return false;
        }

        return true;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts?.Dispose();
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
