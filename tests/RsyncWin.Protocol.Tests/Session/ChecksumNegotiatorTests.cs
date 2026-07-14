using RsyncWin.Protocol.Checksums;
using RsyncWin.Protocol.Session;

namespace RsyncWin.Protocol.Tests.Session;

public class ChecksumNegotiatorTests
{
    // Measured lists from the ssh31-pull-rt capture (rsync 3.4.3 both sides).
    private const string StockClientOffer = "xxh128 xxh3 xxh64 md5 md4";
    private const string StockServerList = "xxh128 xxh3 xxh64 md5 md4 none";

    [Fact]
    public void ClientListOrderWins()
        // Source-verified compat.c rule: first name in the CLIENT's list the server also lists.
        => Assert.Equal("xxh128", ChecksumNegotiator.NegotiateName(StockClientOffer, StockServerList));

    [Fact]
    public void OurDefaultOffer_LandsOnMd5_AgainstAStockServer()
        => Assert.Equal("md5", ChecksumNegotiator.NegotiateName(ChecksumNegotiator.DefaultOffer, StockServerList));

    [Fact]
    public void ServerPreferenceOrder_IsIrrelevant()
        // Even if the server lists md4 first, our list's order decides.
        => Assert.Equal("md5", ChecksumNegotiator.NegotiateName("md5 md4", "md4 md5"));

    [Fact]
    public void NoCommonName_FailsWithExit4()
    {
        // rsync exits RERR_UNSUPPORTED (4) on a failed negotiation — not 2, not 12.
        var exception = Assert.Throws<ProtocolException>(
            () => ChecksumNegotiator.NegotiateName("md5", "xxh128 none"));
        Assert.Equal(RsyncExitCode.UnsupportedAction, exception.ExitCode);
    }

    [Theory]
    [InlineData("md4", ChecksumAlgorithm.Md4)]
    [InlineData("md5", ChecksumAlgorithm.Md5)]
    [InlineData("xxh64", ChecksumAlgorithm.XxHash64)]
    [InlineData("xxh128", ChecksumAlgorithm.XxHash128)] // whole-file sums only until P6
    public void Map_CoversTheImplementedNames(string name, ChecksumAlgorithm expected)
        => Assert.Equal(expected, ChecksumNegotiator.Map(name));

    [Theory]
    [InlineData("xxh3")]
    [InlineData("none")]
    public void Map_RejectsEverythingWeMustNeverOffer(string name)
        => Assert.Throws<ArgumentException>(() => ChecksumNegotiator.Map(name));

    [Fact]
    public void PreNegotiationDefaults_AreMd5At30_AndMd4At29()
    {
        Assert.Equal(ChecksumAlgorithm.Md5, ChecksumNegotiator.Default(31));
        Assert.Equal(ChecksumAlgorithm.Md5, ChecksumNegotiator.Default(30));
        Assert.Equal(ChecksumAlgorithm.Md4, ChecksumNegotiator.Default(29));
    }
}
