namespace RsyncWin.Protocol.Delta;

/// <summary>
/// Derives the per-file block sizing the generator sends in the sum header, matching rsync's
/// <c>sum_sizes_sqroot()</c> behavior exactly.
/// </summary>
/// <remarks>
/// <para>
/// Behavior was pinned empirically against rsync 3.4.3 (<c>--debug=deltasum2</c>, 19 file sizes in
/// <c>test-fixtures/vectors/blocksizer_deltasum2.txt</c>). The interesting cases:
/// </para>
/// <list type="bullet">
/// <item><c>flength=999999 → blength=992</c> — a <em>floor</em> sqrt at 8-granularity, not
/// round-to-nearest (sqrt is 999.9995, yet 992, not 1000).</item>
/// <item><c>flength=490001 → blength=700</c> — the bitwise sqrt yields 696, then the 700 floor wins.
/// Note 700 itself is not a multiple of 8; only the sqrt path has 8-granularity.</item>
/// <item><c>flength=21474836480 → blength=131072</c> — the <see cref="RsyncConstants.MaxBlockSize"/> cap.</item>
/// <item><c>s2length</c>: 2 up to ~10 MB, 3 at 123456789 and 1 GiB, 4 at 8 GiB — the
/// <see cref="RsyncConstants.BlockSumBias"/> bit math below reproduces all of it.</item>
/// </list>
/// </remarks>
public readonly record struct BlockSizes(long BlockLength, int StrongSumLength, long Count, long Remainder)
{
    /// <summary>Computes block length, truncated strong-sum length, block count and remainder for a file.</summary>
    public static BlockSizes ForFileLength(long fileLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileLength);

        long blength;
        if (fileLength <= (long)RsyncConstants.BlockSize * RsyncConstants.BlockSize)
        {
            blength = RsyncConstants.BlockSize;
        }
        else
        {
            // Bitwise floor-sqrt built greedily from the top bit, stopping at bit 3 —
            // the result has 8-granularity by construction.
            long c = 1;
            for (long l = fileLength; (l >>= 2) != 0;)
                c <<= 1;

            blength = 0;
            do
            {
                blength |= c;
                if (fileLength < blength * blength)
                    blength &= ~c;
                c >>= 1;
            } while (c >= 8);

            blength = Math.Max(blength, RsyncConstants.BlockSize);
            blength = Math.Min(blength, RsyncConstants.MaxBlockSize);
        }

        // s2length: scale the truncated strong-sum length with file size (more blocks -> more
        // bits needed to keep collision probability negligible), clamped to [2, 16].
        int b = RsyncConstants.BlockSumBias;
        for (long l = fileLength; (l >>= 1) != 0;)
            b += 2;
        for (long cc = blength; (cc >>= 1) != 0 && b != 0;)
            b--;
        int s2Length = Math.Max(2, Math.Min((b + 1 - 32 + 7) / 8, RsyncConstants.SumLength));

        long count = blength == 0 ? 0 : (fileLength + blength - 1) / blength;
        long remainder = blength == 0 ? 0 : fileLength % blength;

        return new BlockSizes(blength, s2Length, count, remainder);
    }
}
