using System.Buffers.Binary;

namespace RsyncWin.Protocol.Mux;

/// <summary>
/// The 4-byte multiplex frame header: a little-endian word whose low 24 bits carry the payload
/// length and whose high byte carries <see cref="RsyncConstants.MplexBase"/> + <see cref="MessageTag"/>.
/// </summary>
/// <remarks>
/// Pinned by captured frames (rsync 3.4.3, protocol 31): <c>c4 00 00 07</c> is a 196-byte MSG_DATA
/// frame, <c>04 00 00 07</c> a 4-byte one. A header with a zero length and tag
/// <see cref="MessageTag.Data"/> is a keep-alive, not end-of-stream.
/// </remarks>
public readonly record struct MuxHeader(MessageTag Tag, int PayloadLength)
{
    public const int Size = 4;

    /// <summary>Decodes a header from the first 4 bytes of <paramref name="source"/>.</summary>
    public static MuxHeader Read(ReadOnlySpan<byte> source)
    {
        uint word = BinaryPrimitives.ReadUInt32LittleEndian(source);
        int tagByte = (int)(word >> 24) - RsyncConstants.MplexBase;
        if (tagByte < 0)
            throw new InvalidDataException(
                $"multiplex header high byte 0x{word >> 24:x2} is below MPLEX_BASE — stream is desynced or not multiplexed");
        return new MuxHeader((MessageTag)tagByte, (int)(word & 0x00FFFFFF));
    }

    /// <summary>Encodes this header into the first 4 bytes of <paramref name="destination"/>.</summary>
    public void Write(Span<byte> destination)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(PayloadLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PayloadLength, RsyncConstants.MaxMuxPayload);
        uint word = (uint)PayloadLength | (uint)(RsyncConstants.MplexBase + (int)Tag) << 24;
        BinaryPrimitives.WriteUInt32LittleEndian(destination, word);
    }

    /// <summary>A zero-length MSG_DATA frame — the peer is only proving liveness.</summary>
    public bool IsKeepAlive => Tag == MessageTag.Data && PayloadLength == 0;
}
