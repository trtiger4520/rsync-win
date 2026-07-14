namespace RsyncWin.Protocol.Checksums;

/// <summary>
/// The weak rolling checksum (rsync's <c>get_checksum1</c>): an Adler-style two-part sum where
/// <c>s1</c> is the byte sum and <c>s2</c> weights each byte by its distance from the window end,
/// packed as <c>(s2 &lt;&lt; 16) | (s1 &amp; 0xffff)</c>.
/// </summary>
/// <remarks>
/// <para>
/// Bytes are accumulated as <em>signed</em> values (<see cref="sbyte"/>) — rsync sums
/// <c>signed char</c>s on every mainstream platform, and this is observable in the checksum output.
/// Validated against per-chunk <c>sum1</c> values captured from rsync 3.4.3
/// (<c>test-fixtures/vectors/rolling/*.sums.txt</c>), whose input blobs contain bytes ≥ 0x80.
/// </para>
/// <para>
/// <see cref="RsyncConstants.CharOffset"/> is 0 in all modern builds, so it is omitted from the
/// arithmetic below (it would add to every byte in both sums).
/// </para>
/// </remarks>
public static class RollingChecksum
{
    /// <summary>Computes the checksum of a whole window (the non-rolled reference form).</summary>
    public static uint Compute(ReadOnlySpan<byte> window)
    {
        // s2 accumulates s1 after every byte, which weights window[i] by (length - i).
        int s1 = 0, s2 = 0;
        foreach (byte b in window)
        {
            s1 += (sbyte)b;
            s2 += s1;
        }
        return (uint)(s1 & 0xffff) | (uint)(s2 << 16);
    }

    /// <summary>
    /// Slides a window of <paramref name="windowLength"/> one byte forward in O(1):
    /// removes <paramref name="outgoing"/> (the byte leaving at the front) and adds
    /// <paramref name="incoming"/> (the byte entering at the back).
    /// </summary>
    public static uint Roll(uint checksum, byte outgoing, byte incoming, int windowLength)
    {
        int s1 = unchecked((short)(checksum & 0xffff));
        int s2 = unchecked((short)(checksum >> 16));

        s1 = s1 - (sbyte)outgoing + (sbyte)incoming;
        s2 = s2 - windowLength * (sbyte)outgoing + s1;

        return (uint)(s1 & 0xffff) | (uint)(s2 << 16);
    }
}
