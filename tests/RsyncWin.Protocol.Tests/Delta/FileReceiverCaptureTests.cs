using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using RsyncWin.Protocol.Checksums;
using RsyncWin.Protocol.Delta;
using RsyncWin.Protocol.FileList;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Protocol.Tests.Delta;

/// <summary>
/// Reconstructs the real captured delta transfer end-to-end through <see cref="FileReceiver"/>: demux
/// <c>s2c.bin</c> up to the token stream exactly like <see cref="TokenStreamCaptureTests"/>, then feed
/// the token stream plus the basis the capture actually ran against — <c>result.bin</c> with the two
/// regions that became literal tokens patched back to their pre-transfer content — through the new
/// block-reference path.
/// </summary>
public class FileReceiverCaptureTests
{
    // checksum_seed measured off this capture's handshake (docs/wire-notes.md §"Checksum negotiation"):
    // wire bytes "3f 93 4d 6a" LE. Irrelevant to the actual trailer value here — xxh128 whole-file
    // sums ignore the session seed (ChecksumAlgorithm.XxHash128 always resets to 0) — but passed
    // through faithfully rather than a stand-in zero.
    private const int CaptureSeed = 0x6a4d933f;

    private const string ExpectedSha256 =
        "417198a0fb41a4c8c340fdd4a58ac90be823a30df37f0386b7773a4246dea8db";

    private static async Task<MultiplexReader> OpenTokenStreamAsync()
    {
        var reader = new MultiplexReader(PipeReader.Create(new MemoryStream(
            TestFixtures.Bytes("ssh31-pull-delta", "s2c.bin")[41..])));
        FileListResult flist = await FileListReader.ReadAsync(
            reader, new FileListOptions { Protocol = 31, Id0Names = true });
        Assert.Equal("b003_300k.bin", Assert.Single(flist.Entries).Name);

        Assert.Equal(0, await reader.ReadNdxAsync(new NdxCodec()));
        Assert.Equal((ItemFlags)0x8008, await reader.ReadItemFlagsAsync()); // TRANSFER|REPORT_TIME
        return reader;
    }

    /// <summary>
    /// The exact basis the capture ran against: <c>result.bin</c> (the post-transfer content) with
    /// the two regions the capture actually sent as literals patched back to their pre-transfer
    /// bytes, per the task's measured recipe.
    /// </summary>
    private static MemoryStream BuildBasis()
    {
        byte[] basis = TestFixtures.Bytes("ssh31-pull-delta", "result.bin");
        Encoding.ASCII.GetBytes("XXXXXXXX").CopyTo(basis, 1000);
        Encoding.ASCII.GetBytes("YYYY").CopyTo(basis, 150000);
        return new MemoryStream(basis);
    }

    [Fact]
    public async Task DeltaCapture_ReconstructsExactFile_ViaBasisBlockReferences()
    {
        MultiplexReader reader = await OpenTokenStreamAsync();
        using MemoryStream basis = BuildBasis();
        using var output = new MemoryStream();

        FileReceiveResult result = await FileReceiver.ReceiveAsync(
            reader, output, ChecksumAlgorithm.XxHash128, CaptureSeed, protocol: 31, trailerLength: 16,
            cancellationToken: default, basis: basis);

        Assert.True(result.ChecksumMatches);
        Assert.Equal(300000, result.LiteralBytes + result.MatchedBytes);
        // Only two small literal regions (1000..1008 and 150000..150004, per capture-vectors recipe);
        // everything else must come from the basis.
        Assert.True(result.MatchedBytes > 290000,
            $"expected the vast majority of the 300000 bytes to be matched, got {result.MatchedBytes}");

        Assert.Equal(ExpectedSha256, Convert.ToHexStringLower(SHA256.HashData(output.ToArray())));
    }
}
