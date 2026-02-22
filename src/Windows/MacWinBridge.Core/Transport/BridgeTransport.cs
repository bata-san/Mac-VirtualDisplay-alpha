// Mac-Win Bridge: Transport layer – manages TCP connections between Win and Mac.

using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using MacWinBridge.Core.Protocol;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Core.Transport;

/// <summary>
/// Event args for received messages.
/// </summary>
public sealed class MessageReceivedEventArgs : EventArgs
{
    public MessageHeader Header { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; }
}

/// <summary>
/// High-performance TCP transport using System.IO.Pipelines.
/// Supports both server (listen) and client (connect) modes.
/// </summary>
public sealed class BridgeTransport : IAsyncDisposable
{
    // ── Default ports ──────────────────────────────────
    public const int ControlPort = 42100;   // handshake, heartbeat, KVM
    public const int VideoPort   = 42101;   // video frames
    public const int AudioPort   = 42102;   // audio stream

    private readonly ILogger<BridgeTransport> _logger;
    private TcpListener? _listener;
    private TcpClient? _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;

    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;

    public bool IsConnected => _client?.Connected == true;

    public BridgeTransport(ILogger<BridgeTransport> logger)
    {
        _logger = logger;
    }

    // ── Server mode ────────────────────────────────────
    public async Task StartListeningAsync(int port, CancellationToken ct = default)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _logger.LogInformation("Listening on port {Port}", port);

        _client = await _listener.AcceptTcpClientAsync(ct);
        _client.NoDelay = true;
        _stream = _client.GetStream();
        _logger.LogInformation("Client connected from {Endpoint}", _client.Client.RemoteEndPoint);

        Connected?.Invoke(this, EventArgs.Empty);
        StartReadLoop();
    }

    // ── Client mode ────────────────────────────────────
    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(host, port, ct);
        _stream = _client.GetStream();
        _logger.LogInformation("Connected to {Host}:{Port}", host, port);

        Connected?.Invoke(this, EventArgs.Empty);
        StartReadLoop();
    }

    // ── Send ───────────────────────────────────────────
    public async ValueTask SendAsync(MessageType type, ReadOnlyMemory<byte> payload,
        MessageFlags flags = MessageFlags.None, CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected");

        var header = new MessageHeader
        {
            Type = type,
            Flags = flags,
            PayloadLength = (uint)payload.Length,
        };

        await _stream.WriteAsync(header.Serialize(), ct);
        if (payload.Length > 0)
            await _stream.WriteAsync(payload, ct);
        await _stream.FlushAsync(ct);
    }

    // ── Read loop using PipeReader ─────────────────────
    private void StartReadLoop()
    {
        _cts = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        if (_stream is null) return;

        var pipe = PipeReader.Create(_stream, new StreamPipeReaderOptions(
            bufferSize: 1024 * 256,  // 256 KB read buffer
            minimumReadSize: MessageHeader.Size));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await pipe.ReadAsync(ct);
                var buffer = result.Buffer;

                while (TryParseMessage(ref buffer, out var header, out var payload))
                {
                    MessageReceived?.Invoke(this, new MessageReceivedEventArgs
                    {
                        Header = header,
                        Payload = payload.ToArray(),
                    });
                }

                pipe.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Read loop error");
        }
        finally
        {
            await pipe.CompleteAsync();
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool TryParseMessage(ref ReadOnlySequence<byte> buffer,
        out MessageHeader header, out ReadOnlySequence<byte> payload)
    {
        header = default;
        payload = default;

        if (buffer.Length < MessageHeader.Size)
            return false;

        // Read header
        Span<byte> headerBuf = stackalloc byte[MessageHeader.Size];
        buffer.Slice(0, MessageHeader.Size).CopyTo(headerBuf);
        header = MessageHeader.Deserialize(headerBuf);

        var totalLength = MessageHeader.Size + header.PayloadLength;
        if ((ulong)buffer.Length < totalLength)
            return false;

        payload = buffer.Slice(MessageHeader.Size, header.PayloadLength);
        buffer = buffer.Slice(totalLength);
        return true;
    }

    // ── Cleanup ────────────────────────────────────────
    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_readLoop is not null)
        {
            try { await _readLoop; } catch { /* swallow */ }
        }
        _stream?.Dispose();
        _client?.Dispose();
        _listener?.Stop();
        _cts?.Dispose();
    }
}
