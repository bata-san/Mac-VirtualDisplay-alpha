// Mac-Win Bridge: WASAPI audio capture from Windows default audio output.
// Captures all system audio using WASAPI loopback and prepares for streaming.

using NAudio.CoreAudioApi;
using NAudio.Wave;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Audio.Capture;

/// <summary>
/// Event args for captured audio data.
/// </summary>
public sealed class AudioCapturedEventArgs : EventArgs
{
    public byte[] PcmData { get; init; } = Array.Empty<byte>();
    public int SampleRate { get; init; }
    public int Channels { get; init; }
    public int BitsPerSample { get; init; }
    public long TimestampTicks { get; init; }
}

/// <summary>
/// Captures Windows system audio via WASAPI loopback.
/// This acts as the "virtual speaker" – it intercepts all audio output
/// so it can be streamed to the Mac.
/// </summary>
public sealed class WasapiAudioCapture : IDisposable
{
    private readonly ILogger<WasapiAudioCapture> _logger;
    private WasapiLoopbackCapture? _capture;
    private bool _disposed;

    public event EventHandler<AudioCapturedEventArgs>? AudioCaptured;

    public WaveFormat? CaptureFormat => _capture?.WaveFormat;
    public bool IsCapturing { get; private set; }

    public WasapiAudioCapture(ILogger<WasapiAudioCapture> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initialize and start capturing system audio via WASAPI loopback.
    /// WASAPI loopback captures everything playing through the default audio endpoint.
    /// </summary>
    public void Start()
    {
        if (IsCapturing) return;

        _capture = new WasapiLoopbackCapture();

        _logger.LogInformation("WASAPI Loopback: {Format}", _capture.WaveFormat);

        _capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded == 0) return;

            var data = new byte[e.BytesRecorded];
            Buffer.BlockCopy(e.Buffer, 0, data, 0, e.BytesRecorded);

            AudioCaptured?.Invoke(this, new AudioCapturedEventArgs
            {
                PcmData = data,
                SampleRate = _capture.WaveFormat.SampleRate,
                Channels = _capture.WaveFormat.Channels,
                BitsPerSample = _capture.WaveFormat.BitsPerSample,
                TimestampTicks = DateTime.UtcNow.Ticks,
            });
        };

        _capture.RecordingStopped += (_, e) =>
        {
            IsCapturing = false;
            if (e.Exception is not null)
                _logger.LogError(e.Exception, "WASAPI capture stopped with error");
            else
                _logger.LogInformation("WASAPI capture stopped");
        };

        _capture.StartRecording();
        IsCapturing = true;
        _logger.LogInformation("Audio capture started (WASAPI loopback)");
    }

    /// <summary>
    /// Stop capturing audio.
    /// </summary>
    public void Stop()
    {
        if (!IsCapturing) return;

        _capture?.StopRecording();
        IsCapturing = false;
        _logger.LogInformation("Audio capture stopped");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _capture?.StopRecording();
        _capture?.Dispose();
    }
}
