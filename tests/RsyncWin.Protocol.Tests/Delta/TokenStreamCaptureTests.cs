using System.IO.Pipelines;
using RsyncWin.Protocol.Delta;
using RsyncWin.Protocol.FileList;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Protocol.Tests.Delta;

/// <summary>
/// Walks the captured delta transfer (basis file present → real sum head + block references) and
/// pins the token semantics: sign convention, block-index mapping, the remainder-length rule for
/// the last block, and the trailer position.
/// </summary>
public class TokenStreamCaptureTests
{
    [Fact]
    public async Task DeltaCapture_TokenStream_ReconstructsExactly300000Bytes()
    {
        var reader = new MultiplexReader(PipeReader.Create(new MemoryStream(
            TestFixtures.Bytes("ssh31-pull-delta", "s2c.bin")[41..])));
        FileListResult flist = await FileListReader.ReadAsync(
            reader, new FileListOptions { Protocol = 31, Id0Names = true });
        Assert.Equal("b003_300k.bin", Assert.Single(flist.Entries).Name);

        var ndxCodec = new NdxCodec();
        Assert.Equal(0, await reader.ReadNdxAsync(ndxCodec));
        Assert.Equal((ItemFlags)0x8008, await reader.ReadItemFlagsAsync()); // TRANSFER|REPORT_TIME

        // Sum-head echo: the exact measured sizing for a 300000-byte basis.
        var head = SumHeader.Read(await reader.ReadDataExactlyAsync(SumHeader.Size), 16);
        Assert.Equal(new SumHeader(429, 700, 2, 400), head);

        int literalTokens = 0, blockReferences = 0;
        long literalBytes = 0, matchedBytes = 0;
        while (true)
        {
            Token token = await Token.ReadAsync(reader);
            if (token.IsEnd)
                break;

            if (token.IsLiteral)
            {
                literalTokens++;
                literalBytes += token.LiteralLength;
                await reader.ReadDataExactlyAsync(token.LiteralLength);
            }
            else
            {
                blockReferences++;
                Assert.InRange(token.BlockIndex, 0, head.Count - 1);
                matchedBytes += token.BlockIndex == head.Count - 1 ? head.Remainder : head.BlockLength;
            }
        }

        Assert.Equal(2, literalTokens);
        Assert.Equal(1400, literalBytes);
        Assert.Equal(427, blockReferences);
        Assert.Equal(300000, literalBytes + matchedBytes); // remainder rule on block 428
        Assert.Equal(16, (await reader.ReadDataExactlyAsync(16)).Length); // xxh128 trailer
    }
}
