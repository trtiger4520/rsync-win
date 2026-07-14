using RsyncWin.Protocol.Mux;

namespace RsyncWin.Protocol.Tests.Mux;

public class MuxHeaderTests
{
    // Headers lifted verbatim from captured protocol-31 sessions (see docs/wire-notes.md):
    //   s2c first frame after seed, c2s first frame after vstring, c2s final goodbye frame.
    [Theory]
    [InlineData(new byte[] { 0xc4, 0x00, 0x00, 0x07 }, MessageTag.Data, 196)]
    [InlineData(new byte[] { 0x04, 0x00, 0x00, 0x07 }, MessageTag.Data, 4)]
    [InlineData(new byte[] { 0x01, 0x00, 0x00, 0x07 }, MessageTag.Data, 1)]
    public void DecodesCapturedHeaders(byte[] wire, MessageTag tag, int length)
    {
        var header = MuxHeader.Read(wire);

        Assert.Equal(tag, header.Tag);
        Assert.Equal(length, header.PayloadLength);
    }

    [Theory]
    [InlineData(MessageTag.Data, 0)]
    [InlineData(MessageTag.Data, 196)]
    [InlineData(MessageTag.Error, 42)]
    [InlineData(MessageTag.ErrorExit, 4)]
    [InlineData(MessageTag.Data, 0x00FFFFFF)]
    public void RoundTrips(MessageTag tag, int length)
    {
        Span<byte> wire = stackalloc byte[MuxHeader.Size];
        new MuxHeader(tag, length).Write(wire);

        Assert.Equal(new MuxHeader(tag, length), MuxHeader.Read(wire));
    }

    [Fact]
    public void ZeroLengthData_IsKeepAlive_NotEof()
    {
        Span<byte> wire = stackalloc byte[MuxHeader.Size];
        new MuxHeader(MessageTag.Data, 0).Write(wire);

        var header = MuxHeader.Read(wire);
        Assert.True(header.IsKeepAlive);
    }

    [Fact]
    public void NonMultiplexedBytes_AreRejectedLoudly()
    {
        // A raw little-endian int 31 (a version word) read as a mux header must not be
        // misinterpreted silently: high byte 0x00 is below MPLEX_BASE.
        byte[] versionWord = [0x1f, 0x00, 0x00, 0x00];

        Assert.Throws<InvalidDataException>(() => MuxHeader.Read(versionWord));
    }

    [Fact]
    public void PayloadAboveTwentyFourBits_IsRejectedOnWrite()
    {
        var oversized = new MuxHeader(MessageTag.Data, RsyncConstants.MaxMuxPayload + 1);
        byte[] wire = new byte[MuxHeader.Size];

        Assert.Throws<ArgumentOutOfRangeException>(() => oversized.Write(wire));
    }
}
