// Mac-Win Bridge: Audio streaming service.
// Captures Windows system audio and streams PCM to Mac.

using System.Buffers;
using System.Threading.Channels;
using MacWinBridge.Audio.Capture;
using MacWinBridge.Audio.Processing;
using MacWinBridge.Core.Configuration;
using MacWinBridge.Core.Protocol;
using MacWinBridge.Core.Transport;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Audio;

/// <summary>
/// Orchestrates: WASAPI capture → format convert → TCP stream to Mac.
/// Uses Channel for decoupling capture from send.
/// </summary>
public sealed class AudioStreamService : IAsyncDisposable
{
    private readonly ILogger<AudioStreamService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly BridgeConfig _config;
    private readonly BridgeTransport _audioTransport;

    private WasapiAudioCapture? _capture;
    private AudioFormatConverter? _converter;
    private Channel<byte[]>? _sendChannel;
    private CancellationTokenSource? _cts;
    private Task? _sendTask;

    private long _packetsSent;
    private long _bytesSent;
    private AudioRouting _currentRouting;

    public bool IsStreaming   => _capture?.IsCapturing == true;
    public long PacketsSent   => Interlocked.Read(ref _packetsSent);
    public long BytesSent     => Interlocked.Read(ref _bytesSent);
    public AudioRouting CurrentRouting => _currentRouting;

    public event EventHandler<AudioRouting>? RoutingChanged;

    public AudioStreamService(
        ILogger<AudioStreamService> logger,
        ILoggerFactory loggerFactory,
        BridgeConfig config,
        BridgeTransport audioTransport)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _config = config;
        _audioTransport = audioTransport;
        _currentRouting = config.Audio.Routing;
    }

    public async Task StartAsync()
    {
        if (!_config.Audio.Enabled)
        {
            _logger.LogInformation("Audio streaming is disabled in config");
            return;
        }

        _cts = new CancellationTokenSource();

        _converter = new AudioFormatConverter(
            _loggerFactory.CreateLogger<AudioFormatConverter>(),
            _config.Audio.SampleRate, _config.Audio.Channels, _config.Audio.BitsPerSample);

        // Send channel to decouple capture from network I/O
        _sendChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(10)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        await SendAudioConfigAsync();

        _capture = new WasapiAudioCapture(
            _loggerFactory.CreateLogger<WasapiAudioCapture>());
        _capture.AudioCaptured += OnAudioCaptured;
        _capture.Start();

        _sendTask = Task.Run(() => SendLoopAsync(_cts.Token));

        _logger.LogInformation("Audio started: {Rate}Hz {Ch}ch {Bits}bit, routing={R}",
            _config.Audio.SampleRate, _config.Audio.Channels,
            _config.Audio.BitsPerSample, _currentRouting);
    }

    public async Task SetRoutingAsync(AudioRouting routing)
    {
        _currentRouting = routing;
        _config.Audio.Routing = routing;
        _config.Save();

        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            Routing = routing.ToString(),
        });
        await _audioTransport.SendAsync(MessageType.AudioControl, payload);
        RoutingChanged?.Invoke(this, routing);
        _logger.LogInformation("Audio routing → {R}", routing);
    }

    public void Stop()
    {
        if (_capture is not null)
        {
            _capture.AudioCaptured -= OnAudioCaptured;
            _capture.Stop();
        }
        _cts?.Cancel();
        _sendChannel?.Writer.TryComplete();
        _logger.LogInformation("Audio stopped. Sent {P} pkts, {B} bytes", _packetsSent, _bytesSent);
    }

    private async Task SendAudioConfigAsync()
    {
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            SampleRate    = _config.Audio.SampleRate,
            Channels      = _config.Audio.Channels,
            BitsPerSample = _config.Audio.BitsPerSample,
            BufferMs      = _config.Audio.BufferMs,
            Routing       = _currentRouting.ToString(),
        });
        await _audioTransport.SendAsync(MessageType.AudioConfig, json);
    }

    private void OnAudioCaptured(object? sender, AudioCapturedEventArgs e)
    {
        if (_converter is null || _sendChannel is null) return;
        if (_currentRouting == AudioRouting.MacToWindows) return;

        // Convert format
        byte[] streamData;
        if (e.BitsPerSample == 32)
        {
            var fmt = WaveFormat.CreateIeeeFloatWaveFormat(e.SampleRate, e.Channels);
            streamData = _converter.Convert(e.PcmData, fmt);
        }
        else
        {
            var fmt = new WaveFormat(e.SampleRate, e.BitsPerSample, e.Channels);
            streamData = _converter.Convert(e.PcmData, fmt);
        }

        // Prepend timestamp (8 bytes)
        var packet = new byte[8 + streamData.Length];
        BitConverter.TryWriteBytes(packet.AsSpan(0, 8), e.TimestampTicks);
        Buffer.BlockCopy(streamData, 0, packet, 8, streamData.Length);

        _sendChannel.Writer.TryWrite(packet);
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var packet in _sendChannel!.Reader.ReadAllAsync(ct))
            {
                await _audioTransport.SendAsync(MessageType.AudioData,
                    new ReadOnlyMemory<byte>(packet));
                Interlocked.Increment(ref _packetsSent);
                Interlocked.Add(ref _bytesSent, packet.Length);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        _capture?.Dispose();
        _converter?.Dispose();
        if (_sendTask is not null)
            try { await _sendTask; } catch { }
    }
}
