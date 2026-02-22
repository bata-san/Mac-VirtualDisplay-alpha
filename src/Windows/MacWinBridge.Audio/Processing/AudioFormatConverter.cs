// Mac-Win Bridge: High-performance audio format converter.
// Converts between different PCM formats with zero-copy Span operations.

using System.Buffers;
using System.Runtime.InteropServices;
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
    /// Fast path: convert 32-bit float to 16-bit PCM using Span reinterpret cast.
    /// Zero per-sample BitConverter calls — directly operates on float/short spans.
    /// </summary>
    private byte[] ConvertFloat32ToInt16(byte[] sourceData, WaveFormat sourceFormat)
    {
        var floatSpan = MemoryMarshal.Cast<byte, float>(sourceData.AsSpan());
        var sampleCount = floatSpan.Length;
        var resultSize = sampleCount * 2;
        var result = ArrayPool<byte>.Shared.Rent(resultSize);
        var shortSpan = MemoryMarshal.Cast<byte, short>(result.AsSpan(0, resultSize));

        for (int i = 0; i < sampleCount; i++)
        {
            shortSpan[i] = (short)(Math.Clamp(floatSpan[i], -1.0f, 1.0f) * 32767f);
        }

        byte[] final;
        // Handle sample rate conversion if needed
        if (sourceFormat.SampleRate != _targetSampleRate)
        {
            final = ResamplePcm16(result, resultSize, sourceFormat.SampleRate, sourceFormat.Channels);
        }
        else
        {
            final = new byte[resultSize];
            Buffer.BlockCopy(result, 0, final, 0, resultSize);
        }
        ArrayPool<byte>.Shared.Return(result);
        return final;
    }

    /// <summary>
    /// Fast linear interpolation resampler using Span-based short access.
    /// </summary>
    private byte[] ResamplePcm16(byte[] pcm16Data, int dataLength, int sourceSampleRate, int channels)
    {
        var ratio = (double)_targetSampleRate / sourceSampleRate;
        var sourceSampleCount = dataLength / (2 * channels);
        var targetSampleCount = (int)(sourceSampleCount * ratio);
        var resultSize = targetSampleCount * 2 * channels;
        var result = new byte[resultSize];

        var srcShorts = MemoryMarshal.Cast<byte, short>(pcm16Data.AsSpan(0, dataLength));
        var dstShorts = MemoryMarshal.Cast<byte, short>(result.AsSpan());

        for (int ch = 0; ch < channels; ch++)
        {
            for (int i = 0; i < targetSampleCount; i++)
            {
                var srcPos = i / ratio;
                var srcIndex = (int)srcPos;
                var frac = srcPos - srcIndex;

                if (srcIndex + 1 >= sourceSampleCount)
                    srcIndex = Math.Max(0, sourceSampleCount - 2);

                var s1 = srcShorts[srcIndex * channels + ch];
                var s2 = srcShorts[(srcIndex + 1) * channels + ch];
                dstShorts[i * channels + ch] = (short)(s1 + (s2 - s1) * frac);
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
