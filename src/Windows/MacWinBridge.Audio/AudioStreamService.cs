// Mac-Win Bridge: Audio streaming service.
// High-performance capture with routing control (Win→Mac, Mac→Win, Both).

using System.Buffers;
using MacWinBridge.Audio.Capture;
using MacWinBridge.Audio.Processing;
using MacWinBridge.Core.Configuration;
using MacWinBridge.Core.Protocol;
using MacWinBridge.Core.Transport;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Audio;

/// <summary>
/// Orchestrates the audio capture → convert → stream pipeline.
/// Supports flexible routing: Windows→Mac, Mac→Windows, or Both.
/// Uses ArrayPool for zero-alloc packet construction.
/// </summary>
public sealed class AudioStreamService : IAsyncDisposable
{
    private readonly ILogger<AudioStreamService> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly BridgeConfig _config;
    private readonly BridgeTransport _audioTransport;

    private WasapiAudioCapture? _capture;
    private AudioFormatConverter? _converter;
    
    private long _packetsSent;
    private long _bytesSent;
    private AudioRouting _currentRouting;

    public bool IsStreaming => _capture?.IsCapturing == true;
    public long PacketsSent => _packetsSent;
    public long BytesSent => Interlocked.Read(ref _bytesSent);
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

    /// <summary>
    /// Start capturing and streaming Windows audio to the Mac.
    /// </summary>
    public async Task StartAsync()
    {
        if (!_config.Audio.Enabled)
        {
            _logger.LogInformation("Audio streaming is disabled in config");
            return;
        }

        // Initialize format converter
        _converter = new AudioFormatConverter(
            _loggerFactory.CreateLogger<AudioFormatConverter>(),
            _config.Audio.SampleRate,
            _config.Audio.Channels,
            _config.Audio.BitsPerSample);

        // Send audio configuration to Mac companion
        await SendAudioConfigAsync();

        // Initialize and start WASAPI capture
        _capture = new WasapiAudioCapture(
            _loggerFactory.CreateLogger<WasapiAudioCapture>());

        _capture.AudioCaptured += OnAudioCaptured;
        _capture.Start();

        _logger.LogInformation("Audio streaming started: {Rate}Hz, {Ch}ch, {Bits}bit, routing={Routing}",
            _config.Audio.SampleRate, _config.Audio.Channels, _config.Audio.BitsPerSample, _currentRouting);
    }

    /// <summary>
    /// Change audio routing at runtime.
    /// </summary>
    public async Task SetRoutingAsync(AudioRouting routing)
    {
        _currentRouting = routing;
        _config.Audio.Routing = routing;
        _config.Save();

        // Notify Mac companion about routing change
        var payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            Routing = routing.ToString(),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        await _audioTransport.SendAsync(MessageType.AudioControl, payload);

        RoutingChanged?.Invoke(this, routing);
        _logger.LogInformation("Audio routing changed to {Routing}", routing);
    }

    /// <summary>
    /// Stop streaming audio.
    /// </summary>
    public void Stop()
    {
        if (_capture is not null)
        {
            _capture.AudioCaptured -= OnAudioCaptured;
            _capture.Stop();
        }

        _logger.LogInformation("Audio streaming stopped. Sent {Packets} packets, {Bytes} bytes",
            _packetsSent, _bytesSent);
    }

    /// <summary>
    /// Send audio format configuration to the Mac companion.
    /// </summary>
    private async Task SendAudioConfigAsync()
    {
        var config = new
        {
            SampleRate = _config.Audio.SampleRate,
            Channels = _config.Audio.Channels,
            BitsPerSample = _config.Audio.BitsPerSample,
            BufferMs = _config.Audio.BufferMs,
            Compressed = _config.Audio.UseOpusCompression,
            Routing = _currentRouting.ToString(),
        };

        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(config);
        await _audioTransport.SendAsync(MessageType.AudioConfig, json);
        _logger.LogInformation("Sent audio config to Mac companion");
    }

    private async void OnAudioCaptured(object? sender, AudioCapturedEventArgs e)
    {
        if (_converter is null) return;

        // Skip sending if routing is Mac→Windows only
        if (_currentRouting == AudioRouting.MacToWindows) return;

        byte[]? rented = null;
        try
        {
            // Convert from WASAPI format to streaming format
            byte[] streamData;
            if (e.BitsPerSample == 32) // Float32 from WASAPI loopback
            {
                var floatFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(e.SampleRate, e.Channels);
                streamData = _converter.Convert(e.PcmData, floatFormat);
            }
            else
            {
                var sourceFormat = new NAudio.Wave.WaveFormat(e.SampleRate, e.BitsPerSample, e.Channels);
                streamData = _converter.Convert(e.PcmData, sourceFormat);
            }

            // Use ArrayPool: timestamp header (8 bytes) + audio data
            var payloadLen = 8 + streamData.Length;
            rented = ArrayPool<byte>.Shared.Rent(payloadLen);

            BitConverter.TryWriteBytes(rented.AsSpan(0, 8), e.TimestampTicks);
            Buffer.BlockCopy(streamData, 0, rented, 8, streamData.Length);

            await _audioTransport.SendAsync(MessageType.AudioData,
                new ReadOnlyMemory<byte>(rented, 0, payloadLen));

            Interlocked.Increment(ref _packetsSent);
            Interlocked.Add(ref _bytesSent, payloadLen);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming audio packet");
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        _capture?.Dispose();
        _converter?.Dispose();
    }
}
