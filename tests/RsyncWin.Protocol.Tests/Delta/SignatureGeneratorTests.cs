using System.Text;
using RsyncWin.Protocol.Checksums;
using RsyncWin.Protocol.Delta;

namespace RsyncWin.Protocol.Tests.Delta;

/// <summary>
/// <see cref="SignatureGenerator"/> coverage: a golden byte-equality test against the real
/// generator request captured in <c>ssh31-pull-delta</c>, plus synthetic shape tests for the
/// edges a 300000-byte capture doesn't exercise (empty basis, an exact-multiple file length,
/// and short reads off the basis stream).
/// </summary>
public class SignatureGeneratorTests
{
    private const int DeltaSeed = unchecked((int)0x6A4D9FD7);

    private static byte[] ReconstructDeltaBasis()
    {
        byte[] basis = TestFixtures.Bytes("ssh31-pull-delta", "result.bin");
        Encoding.ASCII.GetBytes("XXXXXXXX").CopyTo(basis, 1000);
        Encoding.ASCII.GetBytes("YYYY").CopyTo(basis, 150000);
        return basis;
    }

    /// <summary>Never returns more than <paramref name="maxRead"/> bytes per call, to exercise the
    /// short-read loop every real block read must handle.</summary>
    private sealed class DribblingStream(byte[] data, int maxRead) : MemoryStream(data, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) =>
            base.Read(buffer, offset, Math.Min(count, maxRead));

        public override int Read(Span<byte> buffer) =>
            base.Read(buffer[..Math.Min(buffer.Length, maxRead)]);
    }

    [Fact]
    public async Task MatchesSsh31PullDeltaCapture_HeadAndAllBlockEntries()
    {
        byte[] basis = ReconstructDeltaBasis();
        byte[] c2s = TestFixtures.Bytes("ssh31-pull-delta", "c2s.bin");
        byte[] expected = c2s.AsSpan(45, 2635 - 45).ToArray(); // head (16) + 429 * 6-byte entries

        using var stream = new MemoryStream(basis, writable: false);
        SignatureResult result = await SignatureGenerator.GenerateAsync(
            stream, ChecksumAlgorithm.XxHash128, DeltaSeed, checksumSeedFix: false);

        Assert.Equal(429, result.Header.Count);
        Assert.Equal(700, result.Header.BlockLength);
        Assert.Equal(2, result.Header.StrongSumLength);
        Assert.Equal(400, result.Header.Remainder);
        Assert.Equal(expected, result.Wire);
    }

    [Fact]
    public async Task EmptyBasis_ProducesZeroCountHead()
    {
        using var stream = new MemoryStream([], writable: false);
        SignatureResult result = await SignatureGenerator.GenerateAsync(
            stream, ChecksumAlgorithm.Md5, seed: 0, checksumSeedFix: false);

        // Per transfer-spec.md §4: an existing zero-length basis is (count=0, blength=700,
        // s2length=2, remainder=0) — legal, distinct from the all-zero "full transfer" null head.
        Assert.Equal(0, result.Header.Count);
        Assert.Equal(RsyncConstants.BlockSize, result.Header.BlockLength);
        Assert.Equal(2, result.Header.StrongSumLength);
        Assert.Equal(0, result.Header.Remainder);
        Assert.Equal(SumHeader.Size, result.Wire.Length);
    }

    [Fact]
    public async Task RemainderZero_LastBlockIsFullLengthNotTruncated()
    {
        // 1400 = 2 * 700: an exact multiple, so remainder must be 0 and BOTH blocks are full-size
        // (a buggy "last block gets the remainder" special case would wrongly zero-size block 1).
        byte[] data = new byte[1400];
        new Random(42).NextBytes(data);

        using var stream = new MemoryStream(data, writable: false);
        SignatureResult result = await SignatureGenerator.GenerateAsync(
            stream, ChecksumAlgorithm.Md5, seed: 0, checksumSeedFix: false, s2Length: 4);

        Assert.Equal(2, result.Header.Count);
        Assert.Equal(700, result.Header.BlockLength);
        Assert.Equal(0, result.Header.Remainder);
        Assert.Equal(4, result.Header.StrongSumLength);
        Assert.Equal(SumHeader.Size + 2 * (4 + 4), result.Wire.Length);

        // Verify both entries independently via the already-tested primitives — this test's job is
        // SignatureGenerator's block-splitting/offset plumbing, not the checksum math itself.
        Span<byte> expectedDigest = stackalloc byte[16];
        for (int i = 0; i < 2; i++)
        {
            ReadOnlySpan<byte> block = data.AsSpan(i * 700, 700);
            uint expectedWeak = RollingChecksum.Compute(block);
            StrongChecksum.ComputeBlockSum(ChecksumAlgorithm.Md5, 0, false, block, expectedDigest);

            int entryOffset = SumHeader.Size + i * 8;
            uint actualWeak = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                result.Wire.AsSpan(entryOffset, 4));
            Assert.Equal(expectedWeak, actualWeak);
            Assert.Equal(expectedDigest[..4].ToArray(), result.Wire.AsSpan(entryOffset + 4, 4).ToArray());
        }
    }

    [Fact]
    public async Task ShortReadsFromBasis_StillProduceTheSameSignature()
    {
        byte[] data = new byte[1400];
        new Random(7).NextBytes(data);

        using var wholeRead = new MemoryStream(data, writable: false);
        SignatureResult baseline = await SignatureGenerator.GenerateAsync(
            wholeRead, ChecksumAlgorithm.Md5, seed: 0, checksumSeedFix: false, s2Length: 4);

        using var dribbling = new DribblingStream(data, maxRead: 3);
        SignatureResult dribbled = await SignatureGenerator.GenerateAsync(
            dribbling, ChecksumAlgorithm.Md5, seed: 0, checksumSeedFix: false, s2Length: 4);

        Assert.Equal(baseline.Wire, dribbled.Wire);
    }

    [Fact]
    public async Task BasisEndsEarly_ThrowsInvalidData()
    {
        // Sizing computed for a 1400-byte file, but the stream only has 700 bytes to give —
        // block 1's read must fail loudly, not silently short-write.
        byte[] truncated = new byte[700];
        using var stream = new MemoryStream(truncated, writable: false);
        var sizes = BlockSizes.ForFileLength(1400);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await SignatureGenerator.GenerateAsync(
            stream, ChecksumAlgorithm.Md5, seed: 0, checksumSeedFix: false, blockSizes: sizes));
    }

    [Fact]
    public async Task ExplicitS2Length_OverridesDerivedRedoFullDigest()
    {
        // Redo phase per transfer-spec.md §6: s2length = MIN(16, xfer_sum_len) — the FULL digest,
        // not the phase-0 truncation the same file would otherwise get.
        byte[] basis = ReconstructDeltaBasis();
        using var stream = new MemoryStream(basis, writable: false);

        SignatureResult result = await SignatureGenerator.GenerateAsync(
            stream, ChecksumAlgorithm.XxHash128, DeltaSeed, checksumSeedFix: false, s2Length: 16);

        Assert.Equal(16, result.Header.StrongSumLength);
        Assert.Equal(SumHeader.Size + 429 * (4 + 16), result.Wire.Length);
    }
}
