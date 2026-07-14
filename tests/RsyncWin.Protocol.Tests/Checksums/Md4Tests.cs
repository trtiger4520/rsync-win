using System.Text;
using RsyncWin.Protocol.Checksums;

namespace RsyncWin.Protocol.Tests.Checksums;

public class Md4Tests
{
    // RFC 1320 Appendix A.5 official test suite.
    [Theory]
    [InlineData("", "31d6cfe0d16ae931b73c59d7e0c089c0")]
    [InlineData("a", "bde52cb31de33e46245e05fbdbd6fb24")]
    [InlineData("abc", "a448017aaf21d8525fc10ae87aa6729d")]
    [InlineData("message digest", "d9130a8164549fe818874806e1c7014b")]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "d79e1c308aa5bbcdeea8ed63df412da9")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", "043f8582f241db351ce627e153e7f0e4")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "e33b4ddc9c38f2199c3e7b164fcc0536")]
    public void MatchesRfc1320TestSuite(string input, string expectedHex)
    {
        byte[] digest = Md4.HashData(Encoding.ASCII.GetBytes(input));
        Assert.Equal(expectedHex, Convert.ToHexStringLower(digest));
    }

    [Fact]
    public void MatchesOpenSslLegacyProviderVectors()
    {
        string[] lines = TestFixtures.Lines("md4-openssl-vectors.txt");
        Assert.False(lines[0].StartsWith("openssl legacy provider unavailable"),
            "fixture was generated without the legacy provider; RFC vectors above still gate correctness");

        foreach (string line in lines)
        {
            // format: <hex32>  "<input>"
            int quote = line.IndexOf('"');
            string hex = line[..line.IndexOf(' ')];
            string input = line[(quote + 1)..line.LastIndexOf('"')];

            Assert.Equal(hex, Convert.ToHexStringLower(Md4.HashData(Encoding.ASCII.GetBytes(input))));
        }
    }

    [Fact]
    public void IncrementalAppend_MatchesOneShot_AcrossBlockBoundaries()
    {
        byte[] data = TestFixtures.Bytes("rolling", "blob4k.bin");
        byte[] oneShot = Md4.HashData(data);

        // Feed in awkward chunk sizes that straddle the 64-byte block boundary.
        foreach (int chunk in (int[])[1, 63, 64, 65, 127, 1000])
        {
            var md4 = Md4.Create();
            for (int i = 0; i < data.Length; i += chunk)
                md4.Append(data.AsSpan(i, Math.Min(chunk, data.Length - i)));

            var digest = new byte[Md4.DigestLength];
            md4.Finish(digest);
            Assert.Equal(oneShot, digest);
        }
    }

    [Fact]
    public void MessageLongerThan512Bits_PadsIntoASecondBlock()
    {
        // 56 bytes is the padding edge: content + 0x80 + length no longer fit one block.
        byte[] digest55 = Md4.HashData(new byte[55]);
        byte[] digest56 = Md4.HashData(new byte[56]);
        byte[] digest64 = Md4.HashData(new byte[64]);

        Assert.NotEqual(digest55, digest56);
        Assert.NotEqual(digest56, digest64);
    }
}
