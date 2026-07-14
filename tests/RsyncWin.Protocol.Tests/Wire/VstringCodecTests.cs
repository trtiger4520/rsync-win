using System.Text;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Protocol.Tests.Wire;

public class VstringCodecTests
{
    [Fact]
    public void ShortString_OneLengthByte()
    {
        Span<byte> buffer = stackalloc byte[64];
        int written = VstringCodec.Write(buffer, "foo.txt"u8);

        Assert.Equal(Convert.FromHexString("07666F6F2E747874"), buffer[..written].ToArray());
    }

    [Fact]
    public void LongString_TwoByteBigEndianWithFlag()
    {
        byte[] name = new byte[200];
        Array.Fill(name, (byte)'x');

        Span<byte> buffer = stackalloc byte[256];
        int written = VstringCodec.Write(buffer, name);

        Assert.Equal(0x80, buffer[0]);      // (200 >> 8) | 0x80
        Assert.Equal(0xC8, buffer[1]);      // 200 & 0xFF
        Assert.Equal(202, written);

        (byte[] decoded, int consumed) = VstringCodec.Read(buffer[..written]);
        Assert.Equal(name, decoded);
        Assert.Equal(written, consumed);
    }

    [Fact]
    public void OverCap_IsRejected()
    {
        byte[] tooLong = new byte[VstringCodec.MaxLength + 1];
        byte[] buffer = new byte[VstringCodec.MaxLength + 8];

        Assert.Throws<ArgumentException>(() => VstringCodec.Write(buffer, tooLong));
    }

    [Fact]
    public void RoundTrips_AtTheLengthBoundaries()
    {
        Span<byte> buffer = stackalloc byte[VstringCodec.MaxLength + 2];
        foreach (int length in (int[])[0, 1, 0x7F, 0x80, 0xFF, 0x100, VstringCodec.MaxLength])
        {
            byte[] value = new byte[length];
            new Random(length).NextBytes(value);

            int written = VstringCodec.Write(buffer, value);
            (byte[] decoded, int consumed) = VstringCodec.Read(buffer[..written]);

            Assert.Equal(value, decoded);
            Assert.Equal(written, consumed);
        }
    }

    [Fact]
    public void CapturedClientChecksumOffer_DecodesAsVstring()
    {
        // c2s bytes 4.. hold the client's checksum-choice vstring, right after its version int.
        byte[] c2s = TestFixtures.Bytes("ssh31-pull-rt", "c2s.bin");

        (byte[] value, _) = VstringCodec.Read(c2s.AsSpan(4));

        Assert.Equal("xxh128 xxh3 xxh64 md5 md4", Encoding.ASCII.GetString(value));
    }

    [Fact]
    public void CapturedServerChecksumReply_DecodesAsVstring()
    {
        // s2c: version int (4) + compat varint (2) + the server's checksum vstring.
        byte[] s2c = TestFixtures.Bytes("ssh31-pull-rt", "s2c.bin");

        (byte[] value, _) = VstringCodec.Read(s2c.AsSpan(6));

        Assert.Equal("xxh128 xxh3 xxh64 md5 md4 none", Encoding.ASCII.GetString(value));
    }
}
