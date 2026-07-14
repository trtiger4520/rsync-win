namespace RsyncWin.Protocol.Wire;

/// <summary>
/// rsync's length-prefixed byte string (<c>write_vstring</c>): lengths ≤ 0x7F take one byte;
/// 0x80–0x7FFF take two bytes as <c>(len &gt;&gt; 8) | 0x80</c>, <c>len &amp; 0xFF</c>
/// (big-endian-with-flag); longer is a protocol error. No NUL terminator.
/// </summary>
/// <remarks>
/// Used for the handshake negotiation strings and iflags xnames. Pinned by capture: the client
/// checksum vstring in <c>vectors/ssh31-pull-rt/c2s.bin</c> is <c>19</c> + 25 ASCII bytes.
/// Note this and the ndx 2-byte diff are the only big-endian-ish encodings in the protocol.
/// </remarks>
public static class VstringCodec
{
    public const int MaxLength = 0x7FFF;

    /// <summary>Encodes the length prefix + raw bytes; returns total bytes written.</summary>
    public static int Write(Span<byte> destination, ReadOnlySpan<byte> value)
    {
        if (value.Length > MaxLength)
            throw new ArgumentException($"vstring: {value.Length} bytes exceeds the 0x7FFF wire cap", nameof(value));

        int pos = 0;
        if (value.Length > 0x7F)
        {
            destination[pos++] = (byte)((value.Length >> 8) | 0x80);
            destination[pos++] = (byte)value.Length;
        }
        else
        {
            destination[pos++] = (byte)value.Length;
        }
        value.CopyTo(destination[pos..]);
        return pos + value.Length;
    }

    /// <summary>Decodes one vstring; returns the payload and the total bytes consumed.</summary>
    /// <exception cref="InvalidDataException">Truncated buffer.</exception>
    public static (byte[] Value, int Consumed) Read(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
            throw new InvalidDataException("vstring: empty buffer");

        int pos = 0;
        int length = source[pos++];
        if ((length & 0x80) != 0)
        {
            if (source.Length <= pos)
                throw new InvalidDataException("vstring: truncated two-byte length");
            length = (length & 0x7F) << 8 | source[pos++];
        }
        if (source.Length < pos + length)
            throw new InvalidDataException($"vstring: need {length} payload bytes, have {source.Length - pos}");

        return (source.Slice(pos, length).ToArray(), pos + length);
    }
}
