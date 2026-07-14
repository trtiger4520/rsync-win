namespace RsyncWin.Protocol.Wire;

/// <summary>
/// The stateful file-index codec (<c>write_ndx</c>/<c>read_ndx</c>, protocol ≥ 30). This is its own
/// delta-from-previous encoding — <b>not</b> <c>write_int</c>, not a varint. Protocol 29 sends plain
/// 4-byte LE ints instead.
/// </summary>
/// <remarks>
/// <para>
/// Two independent registers per stream direction: <c>prevPositive</c> (starts −1) and
/// <c>prevNegative</c> (starts +1, holds the <em>magnitude</em> of the last negative index). One
/// encoder per outbound stream, one decoder per inbound stream, never reset — state persists across
/// phases, <c>NDX_DONE</c> markers, and mux frame boundaries for the whole session.
/// </para>
/// <para>
/// Specified in <c>docs/codec-spec.md</c> §4. Pinned by capture: a fresh encoder writing ndx 0
/// produces <c>01</c> (first byte of the generator request in <c>vectors/ssh31-pull-rt/c2s.bin</c>),
/// and <c>NDX_DONE</c> is the single <c>00</c> byte closing each captured session.
/// </para>
/// </remarks>
public sealed class NdxCodec
{
    private int _prevPositive = -1;
    private int _prevNegative = 1;

    /// <summary>Longest wire form (0xFF prefix + 0xFE + 4 bytes).</summary>
    public const int MaxLength = 6;

    /// <summary>Encodes <paramref name="ndx"/>; returns the number of bytes written (1–6).</summary>
    public int Write(Span<byte> destination, int ndx)
    {
        int pos = 0;
        int value;
        if (ndx == RsyncConstants.NdxDone)
        {
            destination[0] = 0x00; // state untouched
            return 1;
        }

        int diff;
        if (ndx >= 0)
        {
            value = ndx;
            diff = value - _prevPositive;
            _prevPositive = value;
        }
        else
        {
            destination[pos++] = 0xFF; // negative context prefix
            value = -ndx;
            diff = value - _prevNegative;
            _prevNegative = value;
        }

        if (diff is >= 1 and <= 0xFD)
        {
            destination[pos++] = (byte)diff;
        }
        else if (diff == 0 || diff is >= 0xFE and <= 0x7FFF)
        {
            // 0xFE escape + diff as 2 bytes BIG-endian; the clear top bit discriminates from
            // the absolute form.
            destination[pos++] = 0xFE;
            destination[pos++] = (byte)(diff >> 8);
            destination[pos++] = (byte)diff;
        }
        else
        {
            // Backward jump or a huge leap: 0xFE escape + the ABSOLUTE value in rsync's own
            // byte order — MSB-with-flag, LSB, mid-low, mid-high. Neither LE nor BE.
            destination[pos++] = 0xFE;
            destination[pos++] = (byte)((value >> 24) | 0x80);
            destination[pos++] = (byte)value;
            destination[pos++] = (byte)(value >> 8);
            destination[pos++] = (byte)(value >> 16);
        }
        return pos;
    }

    /// <summary>Decodes one index; returns the value and the number of bytes consumed.</summary>
    /// <exception cref="InvalidDataException">Buffer too short for the form indicated.</exception>
    public (int Ndx, int Consumed) Read(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
            throw new InvalidDataException("ndx: empty buffer");

        int pos = 0;
        byte b0 = source[pos++];

        bool negative = false;
        if (b0 == 0x00)
            return (RsyncConstants.NdxDone, pos); // state untouched
        if (b0 == 0xFF)
        {
            negative = true;
            if (source.Length <= pos)
                throw new InvalidDataException("ndx: truncated after negative prefix");
            b0 = source[pos++];
        }

        int value;
        if (b0 == 0xFE)
        {
            if (source.Length < pos + 2)
                throw new InvalidDataException("ndx: truncated escape form");
            byte x = source[pos++], y = source[pos++];
            if ((x & 0x80) != 0)
            {
                if (source.Length < pos + 2)
                    throw new InvalidDataException("ndx: truncated absolute form");
                byte m = source[pos++], n = source[pos++];
                value = y | (m << 8) | (n << 16) | ((x & 0x7F) << 24); // absolute; register ignored
            }
            else
            {
                value = ((x << 8) | y) + (negative ? _prevNegative : _prevPositive);
            }
        }
        else
        {
            // Conforming writers send 0x01..0xFD here; rsync is lenient about the rest, so are we.
            value = b0 + (negative ? _prevNegative : _prevPositive);
        }

        if (negative)
        {
            _prevNegative = value;
            return (-value, pos);
        }
        _prevPositive = value;
        return (value, pos);
    }
}
