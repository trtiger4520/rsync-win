using System.Text.RegularExpressions;
using RsyncWin.Protocol.Checksums;

namespace RsyncWin.Protocol.Tests.Checksums;

/// <summary>
/// Golden-vector gate: per-chunk weak checksums (sum1) captured from rsync 3.4.3 via
/// --debug=deltasum4 over blobs whose exact bytes are checked in. The blobs contain bytes
/// ≥ 0x80, so these vectors also pin the signed-char accumulation.
/// </summary>
public partial class RollingChecksumTests
{
    [GeneratedRegex(@"^chunk\[(\d+)\] offset=(\d+) len=(\d+) sum1=([0-9a-f]{8})$")]
    private static partial Regex ChunkLine();

    public static TheoryData<string> Blobs() => new() { "blob4k", "blob64k", "blob300k" };

    [Theory]
    [MemberData(nameof(Blobs))]
    public void MatchesEveryCapturedChunkSum(string blob)
    {
        byte[] bytes = TestFixtures.Bytes("rolling", $"{blob}.bin");
        int chunksSeen = 0;

        foreach (string line in TestFixtures.Lines("rolling", $"{blob}.sums.txt"))
        {
            Match m = ChunkLine().Match(line);
            if (!m.Success)
                continue; // the count= header line

            int offset = int.Parse(m.Groups[2].Value);
            int len = int.Parse(m.Groups[3].Value);
            uint expected = Convert.ToUInt32(m.Groups[4].Value, 16);

            uint actual = RollingChecksum.Compute(bytes.AsSpan(offset, len));

            Assert.True(expected == actual,
                $"{blob} chunk[{m.Groups[1].Value}] offset={offset} len={len}: " +
                $"expected {expected:x8}, got {actual:x8}");
            chunksSeen++;
        }

        Assert.True(chunksSeen > 0, $"no chunk lines parsed for {blob} — fixture regeneration needed?");
    }

    [Fact]
    public void Roll_AgreesWithDirectComputation_AcrossTheWholeBlob()
    {
        byte[] bytes = TestFixtures.Bytes("rolling", "blob4k.bin");
        const int window = 700;

        uint rolling = RollingChecksum.Compute(bytes.AsSpan(0, window));
        for (int i = 1; i + window <= bytes.Length; i++)
        {
            rolling = RollingChecksum.Roll(rolling, bytes[i - 1], bytes[i + window - 1], window);

            uint direct = RollingChecksum.Compute(bytes.AsSpan(i, window));
            Assert.True(direct == rolling, $"roll diverged from direct at offset {i}: {rolling:x8} vs {direct:x8}");
        }
    }

    [Fact]
    public void HighBitBytes_AccumulateAsSignedChar()
    {
        // 0xFF as signed char is -1: s1 = -1 -> 0xffff, s2 = -1 -> 0xffff.
        Assert.Equal(0xffffffffu, RollingChecksum.Compute([0xFF]));

        // Two 0x80 bytes: s1 = -256 (0xff00); s2 = -128 + -256 = -384 (0xfe80).
        Assert.Equal(0xfe80ff00u, RollingChecksum.Compute([0x80, 0x80]));
    }
}
