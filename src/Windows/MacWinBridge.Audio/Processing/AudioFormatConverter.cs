// Mac-Win Bridge: Audio format converter.
// Converts between different PCM formats for optimal streaming.

using NAudio.Wave;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Audio.Processing;

/// <summary>
/// Converts captured audio to the target streaming format.
/// WASAPI loopback typically captures 32-bit float; we convert to 16-bit PCM
/// for efficient network transmission.
/// </summary>
public sealed class AudioFormatConverter : IDisposable
{
    private readonly ILogger<AudioFormatConverter> _logger;
    private readonly int _targetSampleRate;
    private readonly int _targetChannels;
    private readonly int _targetBitsPerSample;

    public WaveFormat TargetFormat { get; }

    public AudioFormatConverter(
        ILogger<AudioFormatConverter> logger,
        int sampleRate = 48000,
        int channels = 2,
        int bitsPerSample = 16)
    {
        _logger = logger;
        _targetSampleRate = sampleRate;
        _targetChannels = channels;
        _targetBitsPerSample = bitsPerSample;
        TargetFormat = new WaveFormat(sampleRate, bitsPerSample, channels);
    }

    /// <summary>
    /// Convert audio data from source format to target streaming format.
    /// Handles: float→int16, sample rate conversion, channel mapping.
    /// </summary>
    public byte[] Convert(byte[] sourceData, WaveFormat sourceFormat)
    {
        // Most common case: WASAPI loopback is 32-bit float stereo
        if (sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat
            && sourceFormat.BitsPerSample == 32)
        {
            return ConvertFloat32ToInt16(sourceData, sourceFormat);
        }

        // If already in target format, pass through
        if (sourceFormat.SampleRate == _targetSampleRate
            && sourceFormat.Channels == _targetChannels
            && sourceFormat.BitsPerSample == _targetBitsPerSample)
        {
            return sourceData;
        }

        // Generic conversion using NAudio resampler
        return ConvertGeneric(sourceData, sourceFormat);
    }

    /// <summary>
    /// Fast path: convert 32-bit float stereo to 16-bit PCM stereo.
    /// </summary>
    private byte[] ConvertFloat32ToInt16(byte[] sourceData, WaveFormat sourceFormat)
    {
        var sampleCount = sourceData.Length / 4; // 4 bytes per float32 sample
        var result = new byte[sampleCount * 2];  // 2 bytes per int16 sample

        for (int i = 0; i < sampleCount; i++)
        {
            var floatSample = BitConverter.ToSingle(sourceData, i * 4);

            // Clamp to [-1.0, 1.0] and scale to int16 range
            floatSample = Math.Clamp(floatSample, -1.0f, 1.0f);
            var intSample = (short)(floatSample * short.MaxValue);

            BitConverter.GetBytes(intSample).CopyTo(result, i * 2);
        }

        // Handle sample rate conversion if needed
        if (sourceFormat.SampleRate != _targetSampleRate)
        {
            return ResamplePcm16(result, sourceFormat.SampleRate, sourceFormat.Channels);
        }

        return result;
    }

    /// <summary>
    /// Simple linear interpolation resampler for PCM16 data.
    /// </summary>
    private byte[] ResamplePcm16(byte[] pcm16Data, int sourceSampleRate, int channels)
    {
        var ratio = (double)_targetSampleRate / sourceSampleRate;
        var sourceSampleCount = pcm16Data.Length / (2 * channels);
        var targetSampleCount = (int)(sourceSampleCount * ratio);
        var result = new byte[targetSampleCount * 2 * channels];

        for (int ch = 0; ch < channels; ch++)
        {
            for (int i = 0; i < targetSampleCount; i++)
            {
                var srcPos = i / ratio;
                var srcIndex = (int)srcPos;
                var frac = srcPos - srcIndex;

                if (srcIndex + 1 >= sourceSampleCount)
                    srcIndex = sourceSampleCount - 2;

                var sample1 = BitConverter.ToInt16(pcm16Data, (srcIndex * channels + ch) * 2);
                var sample2 = BitConverter.ToInt16(pcm16Data, ((srcIndex + 1) * channels + ch) * 2);

                var interpolated = (short)(sample1 + (sample2 - sample1) * frac);
                BitConverter.GetBytes(interpolated).CopyTo(result, (i * channels + ch) * 2);
            }
        }

        return result;
    }

    /// <summary>
    /// Generic conversion path using NAudio's built-in converters.
    /// </summary>
    private byte[] ConvertGeneric(byte[] sourceData, WaveFormat sourceFormat)
    {
        using var sourceStream = new RawSourceWaveStream(
            new MemoryStream(sourceData), sourceFormat);

        IWaveProvider provider = sourceStream;

        // Convert to PCM if needed
        if (sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat)
        {
            provider = new Wave32To16Stream(sourceStream);
        }

        // Read all converted data
        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    public void Dispose()
    {
        // No unmanaged resources to clean up
    }
}
