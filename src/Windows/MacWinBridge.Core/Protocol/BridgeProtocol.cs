// Mac-Win Bridge: Core Protocol Definitions
// Defines the binary wire protocol for all communication between Windows and Mac.

namespace MacWinBridge.Core.Protocol;

/// <summary>
/// Message types exchanged between Windows host and Mac companion.
/// Each message is prefixed with a 8-byte header: [Type:2][Flags:2][Length:4]
/// </summary>
public enum MessageType : ushort
{
    // ── Connection ──────────────────────────────────────
    Handshake       = 0x0001,
    HandshakeAck    = 0x0002,
    Heartbeat       = 0x0003,
    Disconnect      = 0x0004,

    // ── Display (0x01xx) ────────────────────────────────
    VideoFrame      = 0x0100,
    VideoConfig     = 0x0101,
    DisplaySwitch   = 0x0102,
    DisplayStatus   = 0x0103,

    // ── Audio (0x02xx) ──────────────────────────────────
    AudioData       = 0x0200,
    AudioConfig     = 0x0201,
    AudioControl    = 0x0202,

    // ── Input / KVM (0x03xx) ────────────────────────────
    MouseMove       = 0x0300,
    MouseButton     = 0x0301,
    MouseScroll     = 0x0302,
    KeyDown         = 0x0310,
    KeyUp           = 0x0311,
    ClipboardSync   = 0x0320,
}

[Flags]
public enum MessageFlags : ushort
{
    None        = 0,
    Compressed  = 1 << 0,
    Encrypted   = 1 << 1,
    Priority    = 1 << 2,
    KeyFrame    = 1 << 3,
}

/// <summary>
/// Fixed-size 8-byte message header.
/// </summary>
public readonly struct MessageHeader
{
    public const int Size = 8;

    public MessageType Type { get; init; }
    public MessageFlags Flags { get; init; }
    public uint PayloadLength { get; init; }

    public byte[] Serialize()
    {
        var buf = new byte[Size];
        BitConverter.GetBytes((ushort)Type).CopyTo(buf, 0);
        BitConverter.GetBytes((ushort)Flags).CopyTo(buf, 2);
        BitConverter.GetBytes(PayloadLength).CopyTo(buf, 4);
        return buf;
    }

    public static MessageHeader Deserialize(ReadOnlySpan<byte> data)
    {
        return new MessageHeader
        {
            Type = (MessageType)BitConverter.ToUInt16(data[..2]),
            Flags = (MessageFlags)BitConverter.ToUInt16(data[2..4]),
            PayloadLength = BitConverter.ToUInt32(data[4..8]),
        };
    }
}

/// <summary>
/// Handshake payload exchanged on connection establishment.
/// </summary>
public class HandshakePayload
{
    public string AppVersion { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string Platform { get; init; } = "";  // "Windows" or "macOS"
    public int DisplayWidth { get; init; }
    public int DisplayHeight { get; init; }
    public int RefreshRate { get; init; }
    public bool SupportsAudio { get; init; }
    public bool SupportsInput { get; init; }
}
