using System.Buffers.Binary;
using System.Numerics;

namespace RsyncWin.Protocol.Wire;

/// <summary>
/// rsync's variable-length integer encodings for protocol ≥ 30: <c>write_varint</c>/<c>read_varint</c>
/// (int32) and <c>write_varlong</c>/<c>read_varlong</c> (int64 with a <c>minBytes</c> floor).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not LEB128.</b> The FIRST byte is a header whose count of leading 1-bits equals the
/// number of extra bytes that follow; the header's remaining low bits carry the value's
/// most-significant bits, and the extra bytes carry the low-order bytes little-endian.
/// Specified in <c>docs/codec-spec.md</c> §1–§3 (multi-source derivation, arithmetic re-verified);
/// pinned against live bytes: the captured server compat_flags <c>81 fe</c> decodes to 510, and the
/// captured flist mtime bytes <c>5e a5 5d 0d</c> decode to 1577934245 under minBytes=4.
/// </para>
/// <para>
/// The decoder deliberately does <b>not</b> enforce minimal encodings (<c>80 05</c> decodes to 5) —
/// rsync accepts them, so we must. Protocol &lt; 30 does not use these at all; the *30 fallback to
/// plain fixed-width ints lives with the callers.
/// </para>
/// </remarks>
public static class VarintCodec
{
    /// <summary>Longest varint wire form (header + 4 bytes).</summary>
    public const int MaxVarintLength = 5;

    /// <summary>Longest varlong wire form (header + 8 bytes).</summary>
    public const int MaxVarlongLength = 9;

    /// <summary>
    /// Extra-byte count for a header byte, at <c>header &gt;&gt; 2</c> granularity: the number of
    /// consecutive 1-bits from bit 7 downward, capped at 6. (rsync's <c>int_byte_extra</c> table —
    /// generated from this rule, per the provenance policy never copied from GPL source.)
    /// </summary>
    private static int ExtraBytes(byte header) =>
        Math.Min(BitOperations.LeadingZeroCount(~((uint)header << 24)), 6);

    /// <summary>
    /// Total varint wire length (1–5) implied by its first byte — for stream readers that must
    /// consume exactly one varint without over-reading.
    /// </summary>
    /// <exception cref="InvalidDataException">Header demands more than 4 extra bytes.</exception>
    public static int WireLength(byte header)
    {
        int extra = ExtraBytes(header);
        if (extra > 4)
            throw new InvalidDataException($"varint: header 0x{header:x2} demands {extra} extra bytes (max 4)");
        return 1 + extra;
    }

    // ---- varint (int32) ----------------------------------------------------------------

    /// <summary>Encodes <paramref name="value"/>; returns the number of bytes written (1–5).</summary>
    public static int WriteVarint(Span<byte> destination, int value)
    {
        Span<byte> le = stackalloc byte[MaxVarintLength];
        BinaryPrimitives.WriteUInt32LittleEndian(le, (uint)value);
        return WriteCommon(destination, le, minBytes: 1, totalBytes: 4);
    }

    /// <summary>Decodes a varint; returns the value and the number of bytes consumed.</summary>
    /// <exception cref="InvalidDataException">Header demands more than 4 extra bytes, or the buffer
    /// is too short — both are protocol-stream errors (exit 12), not I/O errors.</exception>
    public static (int Value, int Consumed) ReadVarint(ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty)
            throw new InvalidDataException("varint: empty buffer");

        byte header = source[0];
        int extra = WireLength(header) - 1;
        if (source.Length < 1 + extra)
            throw new InvalidDataException($"varint: need {1 + extra} bytes, have {source.Length}");

        Span<byte> le = stackalloc byte[4];
        le.Clear();
        source.Slice(1, extra).CopyTo(le);
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(le);

        // The residual header bits are the value's high bits — except in the maximal form,
        // where they land at byte offset 4, outside the int32, and are discarded.
        if (extra < 4)
            v |= (uint)(header & ((1 << (8 - extra)) - 1)) << (8 * extra);

        return ((int)v, 1 + extra);
    }

    // ---- varlong (int64, minBytes floor) -----------------------------------------------

    /// <summary>
    /// Encodes <paramref name="value"/> with a <paramref name="minBytes"/> floor (3 for flist file
    /// sizes, 4 for mtimes); returns the number of bytes written (minBytes–9).
    /// </summary>
    public static int WriteVarlong(Span<byte> destination, long value, int minBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minBytes, 8);

        Span<byte> le = stackalloc byte[MaxVarlongLength];
        BinaryPrimitives.WriteUInt64LittleEndian(le, (ulong)value);
        return WriteCommon(destination, le, minBytes, totalBytes: 8);
    }

    /// <summary>Decodes a varlong with the same <paramref name="minBytes"/> the writer used.</summary>
    /// <exception cref="InvalidDataException">The header demands a form longer than 9 bytes
    /// (possible only for minBytes ≥ 4), or the buffer is too short.</exception>
    public static (long Value, int Consumed) ReadVarlong(ReadOnlySpan<byte> source, int minBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minBytes, 8);
        if (source.Length < minBytes)
            throw new InvalidDataException($"varlong: need at least {minBytes} bytes, have {source.Length}");

        byte header = source[0];
        int extra = ExtraBytes(header);
        if (minBytes + extra > MaxVarlongLength)
            throw new InvalidDataException(
                $"varlong: header 0x{header:x2} with minBytes={minBytes} demands {minBytes + extra} bytes (max {MaxVarlongLength})");
        int total = minBytes + extra;
        if (source.Length < total)
            throw new InvalidDataException($"varlong: need {total} bytes, have {source.Length}");

        Span<byte> le = stackalloc byte[8];
        le.Clear();
        source.Slice(1, total - 1).CopyTo(le);
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(le);

        int payloadBytes = total - 1;
        if (payloadBytes < 8)
            v |= (ulong)(header & ((1 << (8 - extra)) - 1)) << (8 * payloadBytes);

        return ((long)v, total);
    }

    /// <summary>
    /// Shared encode: <paramref name="le"/> holds the value's little-endian bytes (with one spare
    /// byte of headroom); the wire form is header + low bytes.
    /// </summary>
    private static int WriteCommon(Span<byte> destination, Span<byte> le, int minBytes, int totalBytes)
    {
        // cnt = index (1-based) of the highest nonzero byte, floored at minBytes.
        int cnt = totalBytes;
        while (cnt > minBytes && le[cnt - 1] == 0)
            cnt--;

        int bit = 1 << (7 - (cnt - minBytes));
        if (le[cnt - 1] >= bit)
        {
            // ESCALATE: the top byte collides with the tag bits; push it into the payload.
            cnt++;
            le[cnt - 1] = (byte)~(bit - 1);
        }
        else if (cnt > minBytes)
        {
            // FOLD: the top byte shares the header with the tag.
            le[cnt - 1] |= (byte)~(2 * bit - 1);
        }
        // else FLOOR: top byte is the header as-is (its high bit is clear by construction).

        if (destination.Length < cnt)
            throw new ArgumentException($"destination too small: need {cnt} bytes", nameof(destination));

        destination[0] = le[cnt - 1];
        for (int i = 1; i < cnt; i++)
            destination[i] = le[i - 1];
        return cnt;
    }
}
