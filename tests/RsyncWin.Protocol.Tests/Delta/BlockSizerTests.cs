using System.Text.RegularExpressions;
using RsyncWin.Protocol.Delta;

namespace RsyncWin.Protocol.Tests.Delta;

/// <summary>
/// Golden-vector gate: every (count, rem, blength, s2length) rsync 3.4.3 printed via
/// --debug=deltasum2 must be reproduced exactly. A single mismatch here means every block
/// checksum in a real transfer would silently disagree.
/// </summary>
public partial class BlockSizerTests
{
    [GeneratedRegex(@"^count=(\d+) rem=(\d+) blength=(\d+) s2length=(\d+) flength=(\d+)$")]
    private static partial Regex DeltasumLine();

    public static TheoryData<long, long, long, int> CapturedSizes()
    {
        var data = new TheoryData<long, long, long, int>();
        foreach (string line in TestFixtures.Lines("blocksizer_deltasum2.txt"))
        {
            Match m = DeltasumLine().Match(line);
            Assert.True(m.Success, $"unparseable fixture line: '{line}' — regenerate with capture.sh");
            data.Add(
                long.Parse(m.Groups[5].Value),   // flength
                long.Parse(m.Groups[3].Value),   // blength
                long.Parse(m.Groups[1].Value),   // count
                int.Parse(m.Groups[4].Value));   // s2length
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CapturedSizes))]
    public void MatchesEveryCapturedDeltasum2Line(long fileLength, long blength, long count, int s2Length)
    {
        var sizes = BlockSizes.ForFileLength(fileLength);

        Assert.Equal(blength, sizes.BlockLength);
        Assert.Equal(count, sizes.Count);
        Assert.Equal(s2Length, sizes.StrongSumLength);
        // rem is derivable but assert it anyway — it is part of the wire sum header.
        Assert.Equal(fileLength % blength, sizes.Remainder);
    }

    [Fact]
    public void EmptyFile_HasZeroBlocks()
    {
        var sizes = BlockSizes.ForFileLength(0);

        Assert.Equal(RsyncConstants.BlockSize, sizes.BlockLength);
        Assert.Equal(0, sizes.Count);
        Assert.Equal(0, sizes.Remainder);
        Assert.Equal(2, sizes.StrongSumLength);
    }

    [Fact]
    public void StrongSumLength_NeverLeavesItsClamp()
    {
        foreach (long len in (long[])[0, 1, 700, 1 << 20, 1L << 34, 1L << 44, long.MaxValue / 2])
        {
            var sizes = BlockSizes.ForFileLength(len);
            Assert.InRange(sizes.StrongSumLength, 2, RsyncConstants.SumLength);
            Assert.InRange(sizes.BlockLength, RsyncConstants.BlockSize, RsyncConstants.MaxBlockSize);
        }
    }
}
