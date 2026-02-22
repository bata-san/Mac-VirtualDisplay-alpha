// Mac-Win Bridge: High-performance TCP transport layer.
// Uses System.IO.Pipelines for zero-copy receive and ArrayPool for send.

using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using MacWinBridge.Core.Protocol;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Core.Transport;

public sealed class MessageReceivedEventArgs : EventArgs
{
    public required MessageHeader Header  { get; init; }
    public required byte[]       Payload  { get; init; }
}

/// <summary>
/// Full-duplex TCP transport with automatic reconnection.
/// Supports both server-listen and client-connect modes.
/// </summary>
public sealed class BridgeTransport : IAsyncDisposable
{
    private readonly ILogger<BridgeTransport> _logger;
    private readonly int _port;

    private TcpListener? _listener;
    private TcpClient?   _client;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readTask;

    // ── Statistics ────────────────────────────────────
    private long _bytesSent;
    private long _bytesReceived;
    private long _messagesSent;
    private long _messagesReceived;

    public long BytesSent         => Interlocked.Read(ref _bytesSent);
    public long BytesReceived     => Interlocked.Read(ref _bytesReceived);
    public long MessagesSent      => Interlocked.Read(ref _messagesSent);
    public long MessagesReceived  => Interlocked.Read(ref _messagesReceived);
    public bool IsConnected       => _client?.Connected == true;

    public event EventHandler<MessageReceivedEventArgs>? MessageReceived;
    public event EventHandler? Connected;
    public event EventHandler? Disconnected;

    public BridgeTransport(ILogger<BridgeTransport> logger, int port)
    {
        _logger = logger;
        _port = port;
    }

    public BridgeTransport(ILogger<BridgeTransport> logger) : this(logger, 0) { }

    /// <summary>Reset byte counters (used by stats polling).</summary>
    public void ResetCounters()
    {
        Interlocked.Exchange(ref _bytesSent, 0);
        Interlocked.Exchange(ref _bytesReceived, 0);
    }

    // ── Server mode: listen for incoming connections ──

    public async Task ListenAsync(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _logger.LogInformation("Listening on port {Port}", _port);

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                ConfigureSocket(client);
                await AttachClientAsync(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Listen error on port {Port}", _port);
        }
    }

    // ── Client mode: connect to remote host ──────────

    public async Task ConnectAsync(string host, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var client = new TcpClient();
        ConfigureSocket(client);

        _logger.LogInformation("Connecting to {Host}:{Port}", host, _port);
        await client.ConnectAsync(host, _port, _cts.Token);
        await AttachClientAsync(client);
    }

    /// <summary>Connect to a specific host and port (overrides constructor port).</summary>
    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var client = new TcpClient();
        ConfigureSocket(client);

        _logger.LogInformation("Connecting to {Host}:{Port}", host, port);
        await client.ConnectAsync(host, port, _cts.Token);
        await AttachClientAsync(client);
    }

    /// <summary>
    /// Connect with automatic retry on failure.
    /// </summary>
    public async Task ConnectWithRetryAsync(string host, int maxRetries = 5,
                                            TimeSpan? delay = null, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var retryDelay = delay ?? TimeSpan.FromSeconds(2);

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var client = new TcpClient();
                ConfigureSocket(client);
                await client.ConnectAsync(host, _port, _cts.Token);
                _logger.LogInformation("Connected to {Host}:{Port} on attempt {N}", host, _port, attempt);
                await AttachClientAsync(client);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Connect attempt {N}/{Max} to {Host}:{Port} failed",
                    attempt, maxRetries, host, _port);

                if (attempt == maxRetries) throw;
                await Task.Delay(retryDelay, _cts.Token);
            }
        }
    }

    // ── Send ─────────────────────────────────────────

    public async Task SendAsync(MessageType type, ReadOnlyMemory<byte> payload,
                                MessageFlags flags = MessageFlags.None,
                                CancellationToken ct = default)
    {
        if (_stream is null) throw new InvalidOperationException("Not connected");

        var header = new MessageHeader
        {
            Type = type,
            Flags = flags,
            PayloadLength = (uint)payload.Length,
        };

        // Combine header + payload into single write for Nagle efficiency
        var totalLen = MessageHeader.Size + payload.Length;
        var buf = ArrayPool<byte>.Shared.Rent(totalLen);
        try
        {
            header.Serialize().CopyTo(buf, 0);
            if (payload.Length > 0)
                payload.Span.CopyTo(buf.AsSpan(MessageHeader.Size));

            await _stream.WriteAsync(buf.AsMemory(0, totalLen), ct);

            Interlocked.Add(ref _bytesSent, totalLen);
            Interlocked.Increment(ref _messagesSent);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>Convenience overload for small payloads.</summary>
    public Task SendAsync(MessageType type, byte[] payload, CancellationToken ct = default)
        => SendAsync(type, new ReadOnlyMemory<byte>(payload), MessageFlags.None, ct);

    // ── Disconnect ───────────────────────────────────

    public void Disconnect()
    {
        _cts?.Cancel();
        _stream?.Dispose();
        _client?.Dispose();
        _listener?.Stop();
        _stream = null;
        _client = null;
        Disconnected?.Invoke(this, EventArgs.Empty);
        _logger.LogInformation("Disconnected from port {Port}", _port);
    }

    // ── Internals ────────────────────────────────────

    private async Task AttachClientAsync(TcpClient client)
    {
        // Close any existing connection
        _stream?.Dispose();
        _client?.Dispose();

        _client = client;
        _stream = client.GetStream();
        Connected?.Invoke(this, EventArgs.Empty);

        _readTask = Task.Run(() => ReadLoopAsync(_cts!.Token));
        _logger.LogInformation("Client attached on port {Port} ({Remote})",
            _port, client.Client.RemoteEndPoint);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        if (_stream is null) return;

        var pipe = PipeReader.Create(_stream, new StreamPipeReaderOptions(
            bufferSize: 65536,
            minimumReadSize: MessageHeader.Size));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await pipe.ReadAsync(ct);
                var buffer = result.Buffer;

                while (TryParseMessage(ref buffer, out var header, out var payload))
                {
                    var payloadBytes = payload.ToArray();
                    Interlocked.Add(ref _bytesReceived, MessageHeader.Size + payloadBytes.Length);
                    Interlocked.Increment(ref _messagesReceived);

                    MessageReceived?.Invoke(this, new MessageReceivedEventArgs
                    {
                        Header = header,
                        Payload = payloadBytes,
                    });
                }

                pipe.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Read loop error on port {Port}", _port);
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
        Span<byte> headerSpan = stackalloc byte[MessageHeader.Size];
        buffer.Slice(0, MessageHeader.Size).CopyTo(headerSpan);
        header = MessageHeader.Deserialize(headerSpan);

        var totalNeeded = MessageHeader.Size + header.PayloadLength;
        if (buffer.Length < totalNeeded)
            return false;

        payload = buffer.Slice(MessageHeader.Size, header.PayloadLength);
        buffer = buffer.Slice(totalNeeded);
        return true;
    }

    private static void ConfigureSocket(TcpClient client)
    {
        client.NoDelay = true;                          // Disable Nagle for low latency
        client.ReceiveBufferSize = 256 * 1024;          // 256 KB
        client.SendBufferSize = 256 * 1024;
        client.Client.SetSocketOption(
            SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
    }

    public async ValueTask DisposeAsync()
    {
        Disconnect();
        if (_readTask is not null)
        {
            try { await _readTask; } catch { }
        }
    }
}
