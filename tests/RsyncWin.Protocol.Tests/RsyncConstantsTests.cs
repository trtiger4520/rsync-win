using RsyncWin.Protocol;
using RsyncWin.Protocol.Mux;

namespace RsyncWin.Protocol.Tests;

/// <summary>
/// P0 sanity checks: internal consistency of the constant table.
/// These catch typos, not protocol errors — the real gate is P1, where every value below is
/// pinned against bytes captured from a live rsync.
/// </summary>
public class RsyncConstantsTests
{
    [Fact]
    public void NegotiableVersionRange_IsWellOrdered()
    {
        Assert.True(RsyncConstants.MinProtocolVersion <= RsyncConstants.ProtocolVersion);
        Assert.True(RsyncConstants.ProtocolVersion <= RsyncConstants.MaxProtocolVersion);
    }

    [Theory]
    // A peer is routinely NEWER than us. Negotiation is min(local, remote), so a modern server
    // must still land on a version we implement rather than being rejected.
    [InlineData(32)] // measured: rsync 3.4.3 advertises protocol 32
    [InlineData(31)]
    [InlineData(30)]
    [InlineData(29)]
    public void PeerVersion_WeSupport_NegotiatesToAVersionWeImplement(int peerVersion)
    {
        Assert.InRange(peerVersion, RsyncConstants.MinProtocolVersion, RsyncConstants.MaxProtocolVersion);

        int negotiated = Math.Min(RsyncConstants.ProtocolVersion, peerVersion);

        Assert.InRange(negotiated, RsyncConstants.MinProtocolVersion, RsyncConstants.ProtocolVersion);
    }

    [Fact]
    public void PeerCeiling_SitsAboveWhatWeImplement_SoNewerServersAreNotRejected()
    {
        // Guards the bug this constant table originally had: a ceiling equal to ProtocolVersion
        // rejects a stock rsync 3.4.x, which claims 32.
        Assert.True(
            RsyncConstants.MaxProtocolVersion > RsyncConstants.ProtocolVersion,
            "MaxProtocolVersion is a sanity ceiling on the peer's claim, not the version we implement.");
    }

    [Fact]
    public void MuxPayloadLength_FitsIn24Bits()
    {
        // The frame header packs length into the low 24 bits and the tag into the high byte.
        Assert.Equal(0x00FFFFFF, RsyncConstants.MaxMuxPayload);
    }

    [Fact]
    public void HighestMessageTag_StillFitsInTheHeaderHighByte()
    {
        // Header high byte = MplexBase + tag, and it must not overflow a byte.
        int highest = Enum.GetValues<MessageTag>().Max(t => (int)t);
        Assert.True(RsyncConstants.MplexBase + highest <= byte.MaxValue);
    }

    [Fact]
    public void DataTag_IsZero_SoAPlainDataFrameHeaderIsJustMplexBase()
    {
        Assert.Equal(0, (int)MessageTag.Data);
    }

    [Fact]
    public void BlockSizeBounds_AreOrderedAndMaxIsTheDocumented128K()
    {
        Assert.True(RsyncConstants.BlockSize < RsyncConstants.MaxBlockSize);
        Assert.Equal(131072, RsyncConstants.MaxBlockSize);
    }

    [Fact]
    public void TruncatedBlockChecksum_NeverExceedsTheFullDigest()
    {
        Assert.True(RsyncConstants.ShortSumLength < RsyncConstants.SumLength);
        Assert.Equal(16, RsyncConstants.SumLength); // MD4/MD5 digest size
    }

    [Fact]
    public void FileListIndexSentinels_AreDistinctAndNegative()
    {
        int[] sentinels =
        [
            RsyncConstants.NdxDone,
            RsyncConstants.NdxFlistEof,
            RsyncConstants.NdxDelStats,
            RsyncConstants.NdxFlistOffset,
        ];

        Assert.All(sentinels, s => Assert.True(s < 0));
        Assert.Equal(sentinels.Length, sentinels.Distinct().Count());
    }

    [Fact]
    public void CompatFlags_AreDistinctSingleBits()
    {
        int[] flags =
        [
            RsyncConstants.CompatIncRecurse,
            RsyncConstants.CompatSymlinkTimes,
            RsyncConstants.CompatSymlinkIconv,
            RsyncConstants.CompatSafeFlist,
            RsyncConstants.CompatAvoidXattrOptim,
            RsyncConstants.CompatChecksumSeedFix,
            RsyncConstants.CompatInplacePartialDir,
            RsyncConstants.CompatVarintFlistFlags,
        ];

        Assert.All(flags, f => Assert.Equal(1, System.Numerics.BitOperations.PopCount((uint)f)));
        Assert.Equal(flags.Length, flags.Distinct().Count());
    }
}
