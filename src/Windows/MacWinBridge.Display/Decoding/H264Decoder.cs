// Mac-Win Bridge: H.264 decoder using Media Foundation Transform.
// Receives H.264 NAL units and decodes them to ID3D11Texture2D for rendering.

using System.Runtime.InteropServices;
using MacWinBridge.Core.Protocol;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;
using Vortice.Direct3D;

namespace MacWinBridge.Display.Decoding;

/// <summary>
/// Hardware-accelerated H.264 decoder via Media Foundation.
/// Accepts raw H.264 NAL units, outputs decoded BGRA textures.
/// </summary>
public sealed class H264Decoder : IDisposable
{
    private readonly ILogger<H264Decoder> _logger;

    private ID3D11Device?        _device;
    private ID3D11DeviceContext?  _context;
    private IMFTransform?        _decoder;
    private IMFDXGIDeviceManager? _dxgiManager;

    private int _width;
    private int _height;
    private bool _initialized;
    private long _framesDecoded;

    public int Width  => _width;
    public int Height => _height;
    public long FramesDecoded => Interlocked.Read(ref _framesDecoded);

    /// <summary>Fires when a frame has been decoded and is ready for rendering.</summary>
    public event Action<ID3D11Texture2D>? FrameDecoded;

    public H264Decoder(ILogger<H264Decoder> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Initialize the decoder with D3D11 device and expected dimensions.
    /// </summary>
    public void Initialize(ID3D11Device device, ID3D11DeviceContext context, int width, int height)
    {
        _device  = device;
        _context = context;
        _width   = width;
        _height  = height;

        // Create DXGI Device Manager for hardware-accelerated decoding
        _dxgiManager = MediaFactory.MFCreateDXGIDeviceManager();
        _dxgiManager!.ResetDevice(device);

        // Find and create H.264 decoder MFT
        CreateDecoderTransform();
        _initialized = true;

        _logger.LogInformation("H264 decoder initialized: {W}x{H}", width, height);
    }

    private void CreateDecoderTransform()
    {
        // Enumerate H.264 decoders
        var inputType = new RegisterTypeInfo
        {
            GuidMajorType = MediaTypeGuids.Video,
            GuidSubtype   = VideoFormatGuids.H264,
        };

        // MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER = 0x04 | 0x40 = 0x44
        // MFTEnumEx returns void; results come via out IntPtr (array of IMFActivate*)
        MediaFactory.MFTEnumEx(
            TransformCategoryGuids.VideoDecoder,
            0x00000044u,   // Hardware | SortAndFilter
            inputType, null,
            out IntPtr activateArray,
            out uint activateCount);

        if (activateCount == 0)
        {
            if (activateArray != IntPtr.Zero) Marshal.FreeCoTaskMem(activateArray);
            _logger.LogWarning("No hardware H.264 decoder found, trying software");
            // MFT_ENUM_FLAG_SYNCMFT = 0x01
            MediaFactory.MFTEnumEx(
                TransformCategoryGuids.VideoDecoder,
                0x00000001u,   // SyncMFT
                inputType, null,
                out activateArray,
                out activateCount);
        }

        if (activateCount == 0)
        {
            if (activateArray != IntPtr.Zero) Marshal.FreeCoTaskMem(activateArray);
            throw new InvalidOperationException("No H.264 decoder available on this system");
        }

        // Marshal the first COM activate pointer; release the rest
        var firstPtr = Marshal.ReadIntPtr(activateArray);
        for (uint i = 1; i < activateCount; i++)
            Marshal.Release(Marshal.ReadIntPtr(activateArray, (int)(i * (uint)IntPtr.Size)));
        Marshal.FreeCoTaskMem(activateArray);

        var activate = new IMFActivate(firstPtr);
        _decoder = activate.ActivateObject<IMFTransform>();

        // Enable D3D11 acceleration
        if (_dxgiManager is not null)
        {
            // Try to set the D3D manager on the MFT for hardware-accelerated decoding
            try
            {
                _decoder.ProcessMessage(TMessageType.MessageSetD3DManager,
                    (nuint)(nint)_dxgiManager.NativePointer);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to set D3D manager on decoder, falling back to software");
            }
        }

        // Set input type: H.264
        using var inputMediaType = MediaFactory.MFCreateMediaType();
        inputMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputMediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.H264);
        inputMediaType.Set(MediaTypeAttributeKeys.FrameSize, PackSize(_width, _height));
        _decoder.SetInputType(0, inputMediaType, 0);

        // Set output type: NV12 (GPU-friendly) or BGRA
        using var outputMediaType = MediaFactory.MFCreateMediaType();
        outputMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputMediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.NV12);
        outputMediaType.Set(MediaTypeAttributeKeys.FrameSize, PackSize(_width, _height));
        _decoder.SetOutputType(0, outputMediaType, 0);

        _decoder.ProcessMessage(TMessageType.MessageNotifyBeginStreaming, UIntPtr.Zero);

        _logger.LogInformation("H.264 decoder MFT created: {Name}", activate.FriendlyName);
    }

    /// <summary>
    /// Feed a H.264 NAL unit to the decoder.
    /// Decoded frames will fire the FrameDecoded event.
    /// </summary>
    public void Decode(ReadOnlySpan<byte> nalData, bool isKeyFrame)
    {
        if (!_initialized || _decoder is null) return;

        // Create input sample
        using var buffer = MediaFactory.MFCreateMemoryBuffer(nalData.Length);
        unsafe
        {
            buffer.Lock(out var ptr, out _, out _);
            nalData.CopyTo(new Span<byte>((void*)ptr, nalData.Length));
            buffer.Unlock();
        }
        buffer.CurrentLength = nalData.Length;

        using var sample = MediaFactory.MFCreateSample();
        sample.AddBuffer(buffer);
        sample.SampleTime = 0;

        // Feed to decoder
        _decoder.ProcessInput(0, sample, 0);

        // Try to get output
        DrainOutput();
    }

    private void DrainOutput()
    {
        if (_decoder is null) return;

        while (true)
        {
            var streamInfo = _decoder.GetOutputStreamInfo(0);

            IMFSample? outputSample = null;
            IMFMediaBuffer? outputBuffer = null;
            // MFOUTPUTSTREAMINFO_PROVIDES_SAMPLES = 0x00000100 (not exposed in Vortice enum)
            bool allocateOutput = (((int)streamInfo.Flags) & 0x100) == 0;

            if (allocateOutput)
            {
                outputBuffer = MediaFactory.MFCreateMemoryBuffer(streamInfo.Size > 0 ? streamInfo.Size : _width * _height * 4);
                outputSample = MediaFactory.MFCreateSample();
                outputSample.AddBuffer(outputBuffer);
            }

            var outputDataBuffer = new OutputDataBuffer
            {
                StreamID = 0,
                Sample = outputSample,
            };

            var hr = _decoder.ProcessOutput(ProcessOutputFlags.None, 1, ref outputDataBuffer, out _);

            if (hr.Failure)
            {
                outputSample?.Dispose();
                outputBuffer?.Dispose();
                break;
            }

            // Extract texture from output sample
            var resultSample = outputDataBuffer.Sample;
            if (resultSample is not null)
            {
                ProcessOutputSample(resultSample);
                Interlocked.Increment(ref _framesDecoded);

                if (allocateOutput)
                    resultSample.Dispose();
            }

            outputBuffer?.Dispose();
        }
    }

    private void ProcessOutputSample(IMFSample sample)
    {
        // Try to get D3D11 texture directly from MFT output
        using var mediaBuffer = sample.ConvertToContiguousBuffer();

        // Try IMFDXGIBuffer path for GPU textures
        // IMFDXGIBuffer inherits from IMFMediaBuffer but is a separate COM interface,
        // so we need to use QueryInterface
        try
        {
            var dxgiBuffer = mediaBuffer.QueryInterface<IMFDXGIBuffer>();
            try
            {
                var resourcePtr = dxgiBuffer.GetResource(typeof(ID3D11Texture2D).GUID);
                if (resourcePtr != IntPtr.Zero)
                {
                    var texture = new ID3D11Texture2D(resourcePtr);
                    FrameDecoded?.Invoke(texture);
                    return;
                }
            }
            finally
            {
                dxgiBuffer.Dispose();
            }
        }
        catch
        {
            // Not a DXGI buffer, fall through to CPU path
        }

        // Fallback: create texture from CPU buffer
        mediaBuffer.Lock(out var ptr, out _, out var currentLen);
        try
        {
            var stagingDesc = new Texture2DDescription
            {
                Width  = _width,
                Height = _height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage  = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            };

            var data = new SubresourceData
            {
                DataPointer = ptr,
                RowPitch    = _width * 4,
            };

            var texture = _device!.CreateTexture2D(stagingDesc, [data]);
            FrameDecoded?.Invoke(texture);
        }
        finally
        {
            mediaBuffer.Unlock();
        }
    }

    private static ulong PackSize(int w, int h) => ((ulong)(uint)w << 32) | (uint)h;

    public void Dispose()
    {
        _decoder?.ProcessMessage(TMessageType.MessageNotifyEndOfStream, UIntPtr.Zero);
        _decoder?.Dispose();
        _dxgiManager?.Dispose();
        _initialized = false;
        _logger.LogInformation("H264 decoder disposed");
    }
}
