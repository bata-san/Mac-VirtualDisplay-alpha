// Mac-Win Bridge: Core communication protocol definitions.
// Defines message types, flags, headers, and payload structures
// for all communication between Windows and Mac.
//
// Wire format matches bridge_protocol.md specification.
// Message type values are shared with the Mac Swift companion.

using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace MacWinBridge.Core.Protocol;

/// <summary>
/// All message types exchanged over the bridge.
/// Values MUST match the Swift <c>MessageType</c> enum in BridgeService.swift.
/// Grouped by function: Control (0x00xx), Video (0x01xx), Audio (0x02xx), Input (0x03xx).
/// </summary>
public enum MessageType : ushort
{
    // ── Control (0x00xx) ─────────────────────────────
    Handshake       = 0x0001,
    HandshakeAck    = 0x0002,
    Heartbeat       = 0x0003,
    Disconnect      = 0x0004,

    // ── Video (0x01xx) ───────────────────────────────
    VideoFrame      = 0x0100,
    VideoConfig     = 0x0101,
    DisplaySwitch   = 0x0102,
    DisplayStatus   = 0x0103,
    VideoKeyRequest = 0x0104,   // Windows requests a keyframe from Mac

    // ── Audio (0x02xx) ───────────────────────────────
    AudioData       = 0x0200,
    AudioConfig     = 0x0201,
    AudioControl    = 0x0202,

    // ── Input / KVM (0x03xx) ─────────────────────────
    MouseMove       = 0x0300,
    MouseButton     = 0x0301,
    MouseScroll     = 0x0302,
    CursorReturn    = 0x0303,   // Mac→Win: cursor left Mac screen
    KeyDown         = 0x0310,
    KeyUp           = 0x0311,
    ClipboardSync   = 0x0320,
    KvmConfig       = 0x0330,   // Exchange screen resolution for coordinate mapping
}

/// <summary>
/// Flags embedded in every message header.
/// </summary>
[Flags]
public enum MessageFlags : ushort
{
    None       = 0,
    Compressed = 1 << 0,
    Encrypted  = 1 << 1,
    Priority   = 1 << 2,
    KeyFrame   = 1 << 3,    // Video: this frame is an IDR (keyframe)
    IsIDR      = KeyFrame,  // Alias
}

/// <summary>
/// Fixed 8-byte header for every message on the wire.
///   [Type : 2 bytes][Flags : 2 bytes][PayloadLength : 4 bytes]
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly record struct MessageHeader
{
    public const int Size = 8;

    public MessageType   Type          { get; init; }
    public MessageFlags  Flags         { get; init; }
    public uint          PayloadLength { get; init; }

    public byte[] Serialize()
    {
        var buf = new byte[Size];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0), (ushort)Type);
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2), (ushort)Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4), PayloadLength);
        return buf;
    }

    public static MessageHeader Deserialize(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
            throw new ArgumentException($"Need at least {Size} bytes, got {data.Length}");

        return new MessageHeader
        {
            Type          = (MessageType)BinaryPrimitives.ReadUInt16LittleEndian(data),
            Flags         = (MessageFlags)BinaryPrimitives.ReadUInt16LittleEndian(data[2..]),
            PayloadLength = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
        };
    }
}

// ── Payload structures ───────────────────────────────

/// <summary>
/// Sent during initial handshake to exchange capabilities.
/// </summary>
public sealed class HandshakePayload
{
    public required string AppVersion   { get; init; }
    public required string MachineName  { get; init; }
    public int ScreenWidth  { get; init; }
    public int ScreenHeight { get; init; }
}

/// <summary>
/// Video configuration sent by Mac before streaming starts.
/// </summary>
public sealed class VideoConfigPayload
{
    public int    Width      { get; init; }
    public int    Height     { get; init; }
    public int    Fps        { get; init; }
    public int    Bitrate    { get; init; }  // bps
    public string Codec      { get; init; } = "H264";
    public string Profile    { get; init; } = "Main";
    public int    GopSize    { get; init; } = 30;
}

/// <summary>
/// Per-frame header prepended to H.264 NAL data inside a VideoFrame message payload.
///   [Width:4][Height:4][Codec:1][FrameType:1][PTS:8][DataLength:4]  →  22 bytes
/// </summary>
public static class VideoFrameHeader
{
    public const int Size = 22;

    public static void Write(Span<byte> buf, int width, int height,
                             byte codec, byte frameType, long pts, int dataLen)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buf,       width);
        BinaryPrimitives.WriteInt32LittleEndian(buf[4..],  height);
        buf[8]  = codec;        // 0 = H.264, 1 = H.265
        buf[9]  = frameType;    // 0 = P-frame, 1 = IDR, 2 = B-frame
        BinaryPrimitives.WriteInt64LittleEndian(buf[10..], pts);
        BinaryPrimitives.WriteInt32LittleEndian(buf[18..], dataLen);
    }

    public static (int Width, int Height, byte Codec, byte FrameType, long Pts, int DataLen)
        Read(ReadOnlySpan<byte> buf)
    {
        return (
            Width:     BinaryPrimitives.ReadInt32LittleEndian(buf),
            Height:    BinaryPrimitives.ReadInt32LittleEndian(buf[4..]),
            Codec:     buf[8],
            FrameType: buf[9],
            Pts:       BinaryPrimitives.ReadInt64LittleEndian(buf[10..]),
            DataLen:   BinaryPrimitives.ReadInt32LittleEndian(buf[18..])
        );
    }
}

/// <summary>
/// KVM configuration exchanged so both sides know each other's screen dimensions.
/// </summary>
public sealed class KvmConfigPayload
{
    public int ScreenWidth   { get; init; }
    public int ScreenHeight  { get; init; }
    public int ScaleFactor   { get; init; } = 100;  // percent, e.g. 200 for Retina
}

/// <summary>
/// Sent by Mac when cursor leaves its screen, so Windows can reclaim control.
/// Includes the edge and position so Windows can restore cursor at correct location.
/// </summary>
public sealed class CursorReturnPayload
{
    /// <summary>Edge the cursor left from (relative to Mac screen): Left, Right, Top, Bottom.</summary>
    public required string Edge     { get; init; }
    /// <summary>Position along the edge as a percentage (0.0–1.0).</summary>
    public double Position { get; init; }
}

/// <summary>
/// Available codec identifiers for the video pipeline.
/// </summary>
public enum VideoCodec : byte
{
    H264 = 0,
    H265 = 1,
}

/// <summary>
/// Frame type identifiers matching H.264 NAL unit types.
/// </summary>
public enum FrameType : byte
{
    PFrame = 0,
    IDR    = 1,
    BFrame = 2,
}
