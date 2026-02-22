// Mac-Win Bridge: Video frame encoder for network streaming.
// Architecture: Channel<T> producer/consumer decouples capture thread from encode+send thread.
//  - Capture thread:  fires FrameCaptured → EnqueueFrame() writes to bounded channel
//  - Sender task:     ReadAllAsync drains channel → SIMD delta → TCP send
//  - Bounded(capacity=2) + DropOldest = constant 1-frame latency under any network condition
//  - No async void, no SpinWait, no sleep-based pacing, no lock contention hot path

using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using MacWinBridge.Core.Protocol;
using MacWinBridge.Core.Transport;
using MacWinBridge.Display.Capture;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Display.Streaming;

public sealed class VideoStreamer : IAsyncDisposable
{
    private readonly ILogger<VideoStreamer> _logger;
    private readonly DesktopDuplicationCapture _capture;
    private readonly BridgeTransport _transport;

    // Bounded channel: capacity=2, DropOldest keeps latency constant when network is slow.
    private readonly Channel<FrameCapturedEventArgs> _frames =
        Channel.CreateBounded<FrameCapturedEventArgs>(new BoundedChannelOptions(2)
        {
            FullMode                      = BoundedChannelFullMode.DropOldest,
            SingleReader                  = true,
            SingleWriter                  = false,
            AllowSynchronousContinuations = false,
        });

    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private Task? _senderTask;

    private byte[]? _previousFrame;
    private int     _previousFrameLength;

    private int  _framesSent;
    private long _bytesSent;
    private long _framesDropped;

    private const int FrameHeaderSize = 16;

    public int  FramesSent    => _framesSent;
    public long BytesSent     => Interlocked.Read(ref _bytesSent);
    public long FramesDropped => Interlocked.Read(ref _framesDropped);
    public bool IsStreaming   => _captureTask is not null && !_captureTask.IsCompleted;

    public VideoStreamer(
        ILogger<VideoStreamer> logger,
        DesktopDuplicationCapture capture,
        BridgeTransport transport)
    {
        _logger    = logger;
        _capture   = capture;
        _transport = transport;
    }

    public void Start(int targetFps = 60)
    {
        if (IsStreaming) return;

        _cts = new CancellationTokenSource();

        // Hook capture event on capture thread (non-blocking TryWrite)
        _capture.FrameCaptured += EnqueueFrame;

        // Capture: dedicated LongRunning thread (≠ ThreadPool) to avoid starvation
        _captureTask = Task.Factory.StartNew(
            () => _capture.RunCaptureLoopAsync(targetFps, _cts.Token).GetAwaiter().GetResult(),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // Sender: dedicated LongRunning thread for encode + TCP send
        _senderTask = Task.Factory.StartNew(
            () => SendLoopAsync(_cts.Token).GetAwaiter().GetResult(),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        _logger.LogInformation("Video streaming started (target {Fps} fps)", targetFps);
    }

    public async Task StopAsync()
    {
        _capture.FrameCaptured -= EnqueueFrame;
        _cts?.Cancel();
        _frames.Writer.TryComplete();

        foreach (var task in new[] { _captureTask, _senderTask })
        {
            if (task is null) continue;
            try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch { /* best-effort */ }
        }

        _logger.LogInformation(
            "Video streaming stopped — sent {F}, dropped {D}, {B} bytes",
            _framesSent, _framesDropped, _bytesSent);
    }

    public async Task SendVideoConfigAsync()
    {
        var cfg  = new VideoConfigPayload { Width = _capture.Width, Height = _capture.Height };
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(cfg);
        await _transport.SendAsync(MessageType.VideoConfig, json);
        _logger.LogInformation("Sent video config: {W}x{H}", cfg.Width, cfg.Height);
    }

    // ── Capture thread → Channel ──────────────────────────────────────────────
    private void EnqueueFrame(object? sender, FrameCapturedEventArgs e)
    {
        // TryWrite is lock-free with DropOldest; excess frames self-manage
        if (!_frames.Writer.TryWrite(e))
        {
            Interlocked.Increment(ref _framesDropped);
            if (e.IsPooled) ArrayPool<byte>.Shared.Return(e.PixelData);
        }
    }

    // ── Sender task: encode + TCP ──────────────────────────────────────────────
    private async Task SendLoopAsync(CancellationToken ct)
    {
        await foreach (var frame in _frames.Reader.ReadAllAsync(ct))
        {
            byte[]? delta   = null;
            byte[]? payload = null;
            try
            {
                var pixelLen = frame.PixelDataLength > 0 ? frame.PixelDataLength : frame.PixelData.Length;
                ReadOnlyMemory<byte> dataToSend;
                var flags = MessageFlags.None;

                if (_previousFrame is not null && _previousFrameLength == pixelLen)
                {
                    delta = ArrayPool<byte>.Shared.Rent(pixelLen);
                    if (!XorDeltaCheck(frame.PixelData, _previousFrame, delta, pixelLen))
                    {
                        // Identical frame — skip
                        Interlocked.Increment(ref _framesDropped);
                        continue;
                    }
                    dataToSend = new ReadOnlyMemory<byte>(delta, 0, pixelLen);
                    flags      = MessageFlags.Compressed;
                }
                else
                {
                    dataToSend = new ReadOnlyMemory<byte>(frame.PixelData, 0, pixelLen);
                    flags      = MessageFlags.KeyFrame;
                }

                int totalLen = FrameHeaderSize + dataToSend.Length;
                payload = ArrayPool<byte>.Shared.Rent(totalLen);

                BitConverter.TryWriteBytes(payload.AsSpan(0,  4), frame.Width);
                BitConverter.TryWriteBytes(payload.AsSpan(4,  4), frame.Height);
                BitConverter.TryWriteBytes(payload.AsSpan(8,  4), frame.Stride);
                BitConverter.TryWriteBytes(payload.AsSpan(12, 4), frame.FrameNumber);
                dataToSend.Span.CopyTo(payload.AsSpan(FrameHeaderSize));

                await _transport.SendAsync(
                    MessageType.VideoFrame,
                    new ReadOnlyMemory<byte>(payload, 0, totalLen),
                    flags);

                // Update previous frame reference buffer
                if (_previousFrame is null || _previousFrame.Length < pixelLen)
                {
                    if (_previousFrame is not null)
                        ArrayPool<byte>.Shared.Return(_previousFrame);
                    _previousFrame = ArrayPool<byte>.Shared.Rent(pixelLen);
                }
                Buffer.BlockCopy(frame.PixelData, 0, _previousFrame, 0, pixelLen);
                _previousFrameLength = pixelLen;

                Interlocked.Increment(ref _framesSent);
                Interlocked.Add(ref _bytesSent, totalLen);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SendLoop: error on frame {F}", frame.FrameNumber);
            }
            finally
            {
                if (delta   is not null) ArrayPool<byte>.Shared.Return(delta);
                if (payload is not null) ArrayPool<byte>.Shared.Return(payload);
                if (frame.IsPooled)      ArrayPool<byte>.Shared.Return(frame.PixelData);
            }
        }
    }

    // ── SIMD XOR delta + zero-check in one pass ────────────────────────────────
    private static bool XorDeltaCheck(byte[] cur, byte[] prev, byte[] res, int len)
    {
        var curV  = MemoryMarshal.Cast<byte, Vector<byte>>(cur.AsSpan(0, len));
        var prevV = MemoryMarshal.Cast<byte, Vector<byte>>(prev.AsSpan(0, len));
        var resV  = MemoryMarshal.Cast<byte, Vector<byte>>(res.AsSpan(0, len));
        bool any  = false;

        for (int i = 0; i < curV.Length; i++)
        {
            var x = curV[i] ^ prevV[i];
            resV[i] = x;
            if (x != Vector<byte>.Zero) any = true;
        }

        for (int i = len - (len % Vector<byte>.Count); i < len; i++)
        {
            res[i] = (byte)(cur[i] ^ prev[i]);
            if (res[i] != 0) any = true;
        }

        return any;
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

internal sealed class VideoConfigPayload
{
    public int    Width       { get; init; }
    public int    Height      { get; init; }
    public string PixelFormat { get; init; } = "BGRA";
    public string Codec       { get; init; } = "raw";
}

