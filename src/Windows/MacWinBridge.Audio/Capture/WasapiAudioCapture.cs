// Mac-Win Bridge: WASAPI audio capture via NAudio loopback.
// Captures all system audio output for streaming to Mac.

using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MacWinBridge.Audio.Capture;

public sealed class AudioCapturedEventArgs : EventArgs
{
    public required byte[] PcmData       { get; init; }
    public int    SampleRate     { get; init; }
    public int    Channels       { get; init; }
    public int    BitsPerSample  { get; init; }
    public long   TimestampTicks { get; init; }
}

/// <summary>
/// WASAPI loopback capture: records everything Windows plays through the default output device.
/// Includes silence detection to skip sending empty packets.
/// </summary>
public sealed class WasapiAudioCapture : IDisposable
{
    private readonly ILogger<WasapiAudioCapture> _logger;
    private WasapiLoopbackCapture? _capture;
    private bool _isCapturing;

    public bool IsCapturing => _isCapturing;
    public event EventHandler<AudioCapturedEventArgs>? AudioCaptured;

    public WasapiAudioCapture(ILogger<WasapiAudioCapture> logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        if (_isCapturing) return;

        _capture = new WasapiLoopbackCapture
        {
            ShareMode = AudioClientShareMode.Shared,
        };

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
        _capture.StartRecording();
        _isCapturing = true;

        var fmt = _capture.WaveFormat;
        _logger.LogInformation("WASAPI loopback started: {Rate}Hz, {Ch}ch, {Bits}bit ({Encoding})",
            fmt.SampleRate, fmt.Channels, fmt.BitsPerSample, fmt.Encoding);
    }

    public void Stop()
    {
        if (!_isCapturing) return;
        _capture?.StopRecording();
        _isCapturing = false;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        // Silence detection: skip if all bytes are zero (or near-zero)
        if (IsSilent(e.Buffer.AsSpan(0, e.BytesRecorded)))
            return;

        var data = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, data, 0, e.BytesRecorded);

        var fmt = _capture!.WaveFormat;
        AudioCaptured?.Invoke(this, new AudioCapturedEventArgs
        {
            PcmData       = data,
            SampleRate    = fmt.SampleRate,
            Channels      = fmt.Channels,
            BitsPerSample = fmt.BitsPerSample,
            TimestampTicks = Environment.TickCount64,
        });
    }

    private static bool IsSilent(ReadOnlySpan<byte> data)
    {
        // Check as float samples (WASAPI loopback is typically IEEE Float)
        if (data.Length < 4) return true;

        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(data);
        const float threshold = 0.0001f;
        foreach (var sample in floats)
        {
            if (MathF.Abs(sample) > threshold) return false;
        }
        return true;
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            _logger.LogError(e.Exception, "WASAPI recording stopped with error");
        else
            _logger.LogInformation("WASAPI recording stopped");
    }

    public void Dispose()
    {
        Stop();
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            _capture.Dispose();
        }
    }
}
