// Mac-Win Bridge: Video receiver pipeline.
// Receives H.264 frames from Mac, decodes via MF, and renders to 2nd monitor.

using System.Buffers;
using System.Threading.Channels;
using MacWinBridge.Core.Protocol;
using MacWinBridge.Core.Transport;
using MacWinBridge.Display.Decoding;
using MacWinBridge.Display.Rendering;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Display.Streaming;

/// <summary>
/// Incoming video frame data from TCP.
/// </summary>
public sealed class ReceivedFrame
{
    public required byte[] NalData    { get; init; }
    public int    Width       { get; init; }
    public int    Height      { get; init; }
    public bool   IsKeyFrame  { get; init; }
    public long   Pts         { get; init; }
    public int    DataLength  { get; init; }
}

/// <summary>
/// Pipeline: TCP receive → Channel → H264Decoder → FullScreenRenderer.
/// Uses a bounded channel to decouple network I/O from decode + render.
/// </summary>
public sealed class VideoReceiver : IAsyncDisposable
{
    private readonly ILogger<VideoReceiver> _logger;
    private readonly BridgeTransport _transport;
    private readonly H264Decoder _decoder;
    private readonly FullScreenRenderer _renderer;

    private readonly Channel<ReceivedFrame> _frameChannel;
    private CancellationTokenSource? _cts;
    private Task? _decodeTask;

    // ── Statistics ────────────────────────────────────
    private long _framesReceived;
    private long _framesDropped;
    private long _bytesReceived;
    private long _lastFpsTimestamp = Environment.TickCount64;
    private long _lastFpsFrameCount;
    private double _currentFps;
    private double _totalDecodeMs;
    private long _decodeCount;

    public long FramesReceived => Interlocked.Read(ref _framesReceived);
    public long FramesDropped  => Interlocked.Read(ref _framesDropped);
    public long BytesReceived  => Interlocked.Read(ref _bytesReceived);
    public bool IsReceiving    => _cts is not null && !_cts.IsCancellationRequested;

    /// <summary>Approximate FPS computed from frame arrival rate (updated each second).</summary>
    public double CurrentFps => _currentFps;

    /// <summary>Average decode time in milliseconds.</summary>
    public double AverageDecodeMs => _decodeCount > 0 ? _totalDecodeMs / _decodeCount : 0;

    public VideoReceiver(
        ILogger<VideoReceiver> logger,
        BridgeTransport videoTransport,
        H264Decoder decoder,
        FullScreenRenderer renderer)
    {
        _logger    = logger;
        _transport = videoTransport;
        _decoder   = decoder;
        _renderer  = renderer;

        // Bounded channel: drop old frames to maintain low latency
        _frameChannel = Channel.CreateBounded<ReceivedFrame>(
            new BoundedChannelOptions(3)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
            });
    }

    /// <summary>
    /// Start listening for video frames and decoding them.
    /// </summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();

        // Wire up transport message handler
        _transport.MessageReceived += OnMessageReceived;

        // Start decode/render loop
        _decodeTask = Task.Run(() => DecodeLoopAsync(_cts.Token));

        // Wire up decoder output to renderer
        _decoder.FrameDecoded += OnFrameDecoded;

        _logger.LogInformation("Video receiver started");
    }

    /// <summary>
    /// Stop receiving and decoding.
    /// </summary>
    public void Stop()
    {
        _transport.MessageReceived -= OnMessageReceived;
        _decoder.FrameDecoded -= OnFrameDecoded;
        _cts?.Cancel();
        _frameChannel.Writer.TryComplete();
        _logger.LogInformation("Video receiver stopped. Received: {F} frames, {B} bytes, Dropped: {D}",
            _framesReceived, _bytesReceived, _framesDropped);
    }

    /// <summary>
    /// Request a keyframe from Mac (e.g. after packet loss or startup).
    /// </summary>
    public async Task RequestKeyFrameAsync()
    {
        await _transport.SendAsync(MessageType.VideoKeyRequest, Array.Empty<byte>());
        _logger.LogInformation("Keyframe requested from Mac");
    }

    // ── Internal handlers ────────────────────────────

    private void OnMessageReceived(object? sender, MessageReceivedEventArgs e)
    {
        if (e.Header.Type != MessageType.VideoFrame) return;

        var payload = e.Payload;
        if (payload.Length < VideoFrameHeader.Size) return;

        var (width, height, codec, frameType, pts, dataLen) =
            VideoFrameHeader.Read(payload.AsSpan(0, VideoFrameHeader.Size));

        if (dataLen <= 0 || VideoFrameHeader.Size + dataLen > payload.Length) return;

        // Extract NAL data
        var nalData = new byte[dataLen];
        Buffer.BlockCopy(payload, VideoFrameHeader.Size, nalData, 0, dataLen);

        var frame = new ReceivedFrame
        {
            NalData   = nalData,
            Width     = width,
            Height    = height,
            IsKeyFrame = frameType == (byte)FrameType.IDR,
            Pts       = pts,
            DataLength = dataLen,
        };

        if (!_frameChannel.Writer.TryWrite(frame))
        {
            Interlocked.Increment(ref _framesDropped);
        }

        Interlocked.Increment(ref _framesReceived);
        Interlocked.Add(ref _bytesReceived, payload.Length);

        // Update FPS counter
        var now = Environment.TickCount64;
        var elapsed = now - Interlocked.Read(ref _lastFpsTimestamp);
        if (elapsed >= 1000)
        {
            var frames = Interlocked.Read(ref _framesReceived);
            _currentFps = (frames - _lastFpsFrameCount) * 1000.0 / elapsed;
            _lastFpsFrameCount = frames;
            Interlocked.Exchange(ref _lastFpsTimestamp, now);
        }
    }

    private async Task DecodeLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _frameChannel.Reader.ReadAllAsync(ct))
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    _decoder.Decode(frame.NalData, frame.IsKeyFrame);
                    sw.Stop();
                    _totalDecodeMs += sw.Elapsed.TotalMilliseconds;
                    Interlocked.Increment(ref _decodeCount);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Decode error");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnFrameDecoded(Vortice.Direct3D11.ID3D11Texture2D texture)
    {
        try
        {
            _renderer.Present(texture);
        }
        finally
        {
            texture.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_decodeTask is not null)
        {
            try { await _decodeTask; } catch { }
        }
    }
}
