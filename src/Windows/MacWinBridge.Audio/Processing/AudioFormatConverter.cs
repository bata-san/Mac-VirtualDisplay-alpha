// Mac-Win Bridge: Audio format converter.
// Converts from WASAPI capture format (IEEE Float32) to streaming format (Int16 PCM).

using Microsoft.Extensions.Logging;
using NAudio.Wave;

namespace MacWinBridge.Audio.Processing;

/// <summary>
/// Converts captured audio between formats.
/// Primary use: WASAPI loopback Float32 → Int16 PCM for network streaming.
/// </summary>
public sealed class AudioFormatConverter : IDisposable
{
    private readonly ILogger<AudioFormatConverter> _logger;
    private readonly int _targetSampleRate;
    private readonly int _targetChannels;
    private readonly int _targetBitsPerSample;
    private readonly WaveFormat _targetFormat;

    public AudioFormatConverter(ILogger<AudioFormatConverter> logger,
                                int sampleRate, int channels, int bitsPerSample)
    {
        _logger = logger;
        _targetSampleRate = sampleRate;
        _targetChannels = channels;
        _targetBitsPerSample = bitsPerSample;
        _targetFormat = new WaveFormat(sampleRate, bitsPerSample, channels);
    }

    /// <summary>
    /// Convert audio data from source format to target format.
    /// </summary>
    public byte[] Convert(byte[] sourceData, WaveFormat sourceFormat)
    {
        // Fast path: formats already match
        if (sourceFormat.SampleRate == _targetSampleRate
            && sourceFormat.Channels == _targetChannels
            && sourceFormat.BitsPerSample == _targetBitsPerSample)
        {
            return sourceData;
        }

        // Float32 → Int16 conversion (most common path from WASAPI loopback)
        if (sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat && _targetBitsPerSample == 16)
        {
            return ConvertFloat32ToInt16(sourceData, sourceFormat);
        }

        // General case: use NAudio resampler
        using var sourceStream = new RawSourceWaveStream(sourceData, 0, sourceData.Length, sourceFormat);
        using var resampler = new MediaFoundationResampler(sourceStream, _targetFormat);
        resampler.ResamplerQuality = 30; // Low quality for speed

        using var ms = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
        {
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private byte[] ConvertFloat32ToInt16(byte[] floatData, WaveFormat sourceFormat)
    {
        var floatSamples = floatData.Length / 4;
        var output = new byte[floatSamples * 2]; // 16-bit = 2 bytes per sample

        var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(floatData);
        var shorts = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(output);

        for (int i = 0; i < floats.Length && i < shorts.Length; i++)
        {
            // Clamp and convert
            var sample = Math.Clamp(floats[i], -1.0f, 1.0f);
            shorts[i] = (short)(sample * 32767);
        }

        return output;
    }

    public void Dispose() { }
}
