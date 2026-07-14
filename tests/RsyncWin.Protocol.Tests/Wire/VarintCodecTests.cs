using RsyncWin.Protocol.Wire;

namespace RsyncWin.Protocol.Tests.Wire;

/// <summary>
/// Vectors from docs/codec-spec.md §2–§3 (multi-source derivation, arithmetic re-verified),
/// plus pins against live captured bytes. rsync's varint is NOT LEB128 — the length indicator
/// is in the first byte and payload bytes are little-endian.
/// </summary>
public class VarintCodecTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    [Theory]
    [InlineData(0x00000000, "00")]
    [InlineData(0x00000001, "01")]
    [InlineData(0x0000007F, "7F")]
    [InlineData(0x00000080, "80 80")]
    [InlineData(0x000000FF, "80 FF")]
    [InlineData(0x00000100, "81 00")]
    [InlineData(0x00003FFF, "BF FF")]
    [InlineData(0x00004000, "C0 00 40")]
    [InlineData(0x0000FFFF, "C0 FF FF")]
    [InlineData(0x001FFFFF, "DF FF FF")]
    [InlineData(0x00200000, "E0 00 00 20")]
    [InlineData(0x00FFFFFF, "E0 FF FF FF")]
    [InlineData(0x0FFFFFFF, "EF FF FF FF")]
    [InlineData(0x10000000, "F0 00 00 00 10")]
    [InlineData(0x7FFFFFFF, "F0 FF FF FF 7F")]
    [InlineData(-1, "F0 FF FF FF FF")]
    public void Varint_RoundTripsSpecVectors(int value, string wireHex)
    {
        byte[] expected = Hex(wireHex);

        Span<byte> buffer = stackalloc byte[VarintCodec.MaxVarintLength];
        int written = VarintCodec.WriteVarint(buffer, value);
        Assert.Equal(expected, buffer[..written].ToArray());

        (int decoded, int consumed) = VarintCodec.ReadVarint(expected);
        Assert.Equal(value, decoded);
        Assert.Equal(expected.Length, consumed);
    }

    [Theory]
    [InlineData(0L, "00 00 00")]
    [InlineData(1L, "00 01 00")]
    [InlineData(127L, "00 7F 00")]
    [InlineData(128L, "00 80 00")]
    [InlineData(255L, "00 FF 00")]
    [InlineData(256L, "00 00 01")]
    [InlineData(1000L, "00 E8 03")]
    [InlineData(65535L, "00 FF FF")]
    [InlineData(65536L, "01 00 00")]
    [InlineData(8388607L, "7F FF FF")]                       // 2^23-1: FLOOR max
    [InlineData(8388608L, "80 00 00 80")]                    // 2^23: grows to 4 bytes
    [InlineData(2147483648L, "C0 00 00 00 80")]              // 2^31
    [InlineData(3735928559L, "C0 EF BE AD DE")]              // 0xDEADBEEF
    [InlineData(5000000000L, "C1 00 F2 05 2A")]
    [InlineData((1L << 58) - 1, "FB FF FF FF FF FF FF FF")]
    [InlineData(1L << 58, "FC 00 00 00 00 00 00 00 04")]     // 8 -> 9 byte roll
    public void VarlongMin3_RoundTripsSpecVectors(long value, string wireHex)
    {
        AssertVarlongRoundTrip(value, minBytes: 3, Hex(wireHex));
    }

    [Theory]
    [InlineData(0L, "00 00 00 00")]
    [InlineData(1577934245L, "5E A5 5D 0D")]                 // 2020-01-02T03:04:05Z — bytes also
                                                             // visible in the captured proto-31 flist
    [InlineData(2147483648L, "80 00 00 00 80")]              // 2^31: grows to 5 bytes (differs from M=3!)
    [InlineData(4300000000L, "81 00 CB 4C 00")]              // > 2^32 mtime (year 2106)
    [InlineData((1L << 59) - 1, "F7 FF FF FF FF FF FF FF")]
    [InlineData(1L << 59, "F8 00 00 00 00 00 00 00 08")]
    [InlineData(-1L, "F8 FF FF FF FF FF FF FF FF")]
    [InlineData(-2082844800L, "F8 80 4F DA 83 FF FF FF FF")] // pre-1970 mtime
    public void VarlongMin4_RoundTripsSpecVectors(long value, string wireHex)
    {
        AssertVarlongRoundTrip(value, minBytes: 4, Hex(wireHex));
    }

    private static void AssertVarlongRoundTrip(long value, int minBytes, byte[] expected)
    {
        Span<byte> buffer = stackalloc byte[VarintCodec.MaxVarlongLength];
        int written = VarintCodec.WriteVarlong(buffer, value, minBytes);
        Assert.Equal(expected, buffer[..written].ToArray());

        (long decoded, int consumed) = VarintCodec.ReadVarlong(expected, minBytes);
        Assert.Equal(value, decoded);
        Assert.Equal(expected.Length, consumed);
    }

    [Fact]
    public void SameValue_EncodesDifferentlyPerMinBytes()
    {
        // 2^31 under M=3 vs M=4 — both sides must agree per-field or the flist desyncs.
        Span<byte> m3 = stackalloc byte[VarintCodec.MaxVarlongLength];
        Span<byte> m4 = stackalloc byte[VarintCodec.MaxVarlongLength];
        int w3 = VarintCodec.WriteVarlong(m3, 1L << 31, 3);
        int w4 = VarintCodec.WriteVarlong(m4, 1L << 31, 4);

        Assert.Equal(Hex("C0 00 00 00 80"), m3[..w3].ToArray());
        Assert.Equal(Hex("80 00 00 00 80"), m4[..w4].ToArray());
    }

    [Fact]
    public void Varint_AcceptsNonMinimalEncodings_LikeRsync()
    {
        (int value, int consumed) = VarintCodec.ReadVarint(Hex("80 05"));
        Assert.Equal(5, value);
        Assert.Equal(2, consumed);
    }

    [Theory]
    [InlineData("F8 00 00 00 00 00")] // 5 extra bytes
    [InlineData("FF 00 00 00 00 00 00 00")] // capped at 6, still > 4
    public void Varint_RejectsHeadersDemandingMoreThanFourExtraBytes(string wireHex)
    {
        byte[] wire = Hex(wireHex);
        Assert.Throws<InvalidDataException>(() => VarintCodec.ReadVarint(wire));
    }

    [Fact]
    public void Varlong_OverflowBoundsDependOnMinBytes()
    {
        // Header 0xFC demands 6 extra bytes: legal at minBytes=3 (total 9), fatal at minBytes=4 (10).
        byte[] nineByteForm = Hex("FC 00 00 00 00 00 00 00 04");

        (long value, _) = VarintCodec.ReadVarlong(nineByteForm, 3);
        Assert.Equal(1L << 58, value);

        Assert.Throws<InvalidDataException>(() => VarintCodec.ReadVarlong(nineByteForm, 4));
    }

    [Fact]
    public void MaximalForms_DiscardResidualHeaderBits()
    {
        // FD + 8 payload bytes decodes identically to FC + the same bytes at minBytes=3:
        // the residual header bit sits beyond the int64 and is discarded.
        byte[] payload = Hex("11 22 33 44 55 66 77 88");
        long viaFc = VarintCodec.ReadVarlong([(byte)0xFC, .. payload], 3).Value;
        long viaFd = VarintCodec.ReadVarlong([(byte)0xFD, .. payload], 3).Value;

        Assert.Equal(viaFc, viaFd);
    }

    // ---- pins against live captured bytes ------------------------------------------------

    [Fact]
    public void CapturedCompatFlags_DecodeTo510_WithIncRecurseClear()
    {
        // s2c bytes 4..5 are the server's compat_flags varint, right after its version int.
        byte[] s2c = TestFixtures.Bytes("ssh31-pull-rt", "s2c.bin");

        (int compatFlags, int consumed) = VarintCodec.ReadVarint(s2c.AsSpan(4));

        Assert.Equal(2, consumed);
        Assert.Equal(510, compatFlags);
        Assert.Equal(0, compatFlags & RsyncConstants.CompatIncRecurse); // server honored --no-inc-recursive
        Assert.NotEqual(0, compatFlags & RsyncConstants.CompatVarintFlistFlags);
        Assert.NotEqual(0, compatFlags & RsyncConstants.CompatChecksumSeedFix);
    }

    [Fact]
    public void Varint_RoundTripsExhaustivelyAroundEveryBoundary()
    {
        Span<byte> buffer = stackalloc byte[VarintCodec.MaxVarintLength];
        foreach (int boundary in (int[])[0, 0x7F, 0x80, 0x3FFF, 0x4000, 0x1FFFFF, 0x200000, 0x0FFFFFFF, 0x10000000, int.MaxValue, int.MinValue])
        {
            for (int delta = -2; delta <= 2; delta++)
            {
                int value = unchecked(boundary + delta);
                int written = VarintCodec.WriteVarint(buffer, value);
                (int decoded, int consumed) = VarintCodec.ReadVarint(buffer[..written]);
                Assert.Equal(value, decoded);
                Assert.Equal(written, consumed);
            }
        }
    }
}
