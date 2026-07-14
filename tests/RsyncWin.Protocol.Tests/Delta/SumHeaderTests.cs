using RsyncWin.Protocol.Delta;

namespace RsyncWin.Protocol.Tests.Delta;

public class SumHeaderTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    // Worked heads from docs/codec-spec.md §7; the 300000 row is measured ground truth.
    [Theory]
    [InlineData(300000L, "AD 01 00 00 BC 02 00 00 02 00 00 00 90 01 00 00")]
    [InlineData(1L << 30, "00 80 00 00 00 80 00 00 03 00 00 00 00 00 00 00")]
    [InlineData(490001L, "BD 02 00 00 BC 02 00 00 02 00 00 00 01 00 00 00")]
    public void WireForm_MatchesSpecVectors(long fileLength, string wireHex)
    {
        var header = SumHeader.For(BlockSizes.ForFileLength(fileLength));

        Span<byte> wire = stackalloc byte[SumHeader.Size];
        header.Write(wire);
        Assert.Equal(Hex(wireHex), wire.ToArray());

        Assert.Equal(header, SumHeader.Read(wire, digestLength: 16));
    }

    [Fact]
    public void NullHead_IsAFullTransferRequest()
    {
        Span<byte> wire = stackalloc byte[SumHeader.Size];
        SumHeader.Null.Write(wire);

        Assert.All(wire.ToArray(), b => Assert.Equal(0, b));
        Assert.True(SumHeader.Read(wire, 16).IsFullTransferRequest);
    }

    [Fact]
    public void CapturedGeneratorRequest_CarriesTheNullHead()
    {
        // The 16 zero bytes after ndx 1 + iflags 0xA000 in the captured c2s stream — asserted
        // structurally in NdxCodecTests; here decode them as a SumHeader.
        Span<byte> zeros = stackalloc byte[SumHeader.Size];
        var header = SumHeader.Read(zeros, 16);

        Assert.True(header.IsFullTransferRequest);
        Assert.Equal(0, header.Count);
    }

    [Theory]
    [InlineData("FF FF FF FF 00 00 00 00 02 00 00 00 00 00 00 00")] // count -1
    [InlineData("01 00 00 00 01 00 02 00 02 00 00 00 00 00 00 00")] // blength > MAX_BLOCK_SIZE
    [InlineData("01 00 00 00 BC 02 00 00 11 00 00 00 00 00 00 00")] // s2length 17 > digest 16
    [InlineData("01 00 00 00 BC 02 00 00 02 00 00 00 BD 02 00 00")] // remainder > blength
    public void OutOfRangeFields_AreProtocolErrors(string wireHex)
    {
        byte[] wire = Hex(wireHex);
        Assert.Throws<InvalidDataException>(() => SumHeader.Read(wire, digestLength: 16));
    }

    [Fact]
    public void XxHashDigestLength_TightensTheS2LengthBound()
    {
        // s2length 9 is legal under MD5 (16) but not under xxh64 (8).
        byte[] wire = Hex("01 00 00 00 BC 02 00 00 09 00 00 00 00 00 00 00");

        Assert.Equal(9, SumHeader.Read(wire, digestLength: 16).StrongSumLength);
        Assert.Throws<InvalidDataException>(() => SumHeader.Read(wire, digestLength: 8));
    }
}
