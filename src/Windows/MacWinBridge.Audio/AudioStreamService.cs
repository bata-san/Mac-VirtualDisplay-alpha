// Mac-Win Bridge: Audio streaming service.
// Captures Windows audio and streams it to the Mac companion.

using MacWinBridge.Audio.Capture;
using MacWinBridge.Audio.Processing;
using MacWinBridge.Core.Configuration;
using MacWinBridge.Core.Protocol;
using MacWinBridge.Core.Transport;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Audio;

/// <summary>
/// Orchestrates the audio capture → convert → stream pipeline.
/// Windows audio is captured via WASAPI loopback, converted to the target format,
/// and streamed to the Mac companion where it's mixed with Mac's native audio.
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

    public bool IsStreaming => _capture?.IsCapturing == true;
    public long PacketsSent => _packetsSent;
    public long BytesSent => Interlocked.Read(ref _bytesSent);

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

        _logger.LogInformation("Audio streaming started: {Rate}Hz, {Ch}ch, {Bits}bit",
            _config.Audio.SampleRate, _config.Audio.Channels, _config.Audio.BitsPerSample);
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
        };

        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(config);
        await _audioTransport.SendAsync(MessageType.AudioConfig, json);
        _logger.LogInformation("Sent audio config to Mac companion");
    }

    private async void OnAudioCaptured(object? sender, AudioCapturedEventArgs e)
    {
        if (_converter is null) return;

        try
        {
            // Convert from WASAPI format to streaming format
            var sourceFormat = new NAudio.Wave.WaveFormat(e.SampleRate, e.BitsPerSample, e.Channels);
            
            byte[] streamData;
            if (e.BitsPerSample == 32) // Float32 from WASAPI loopback
            {
                var floatFormat = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(e.SampleRate, e.Channels);
                streamData = _converter.Convert(e.PcmData, floatFormat);
            }
            else
            {
                streamData = _converter.Convert(e.PcmData, sourceFormat);
            }

            // Prepend timestamp for jitter buffer on Mac side
            var header = BitConverter.GetBytes(e.TimestampTicks);
            var payload = new byte[8 + streamData.Length];
            header.CopyTo(payload, 0);
            streamData.CopyTo(payload, 8);

            await _audioTransport.SendAsync(MessageType.AudioData, payload);

            Interlocked.Increment(ref _packetsSent);
            Interlocked.Add(ref _bytesSent, payload.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error streaming audio packet");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        _capture?.Dispose();
        _converter?.Dispose();
    }
}
