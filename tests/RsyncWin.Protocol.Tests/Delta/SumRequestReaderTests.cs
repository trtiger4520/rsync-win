using System.IO.Pipelines;
using System.Linq;
using System.Text;
using RsyncWin.Protocol.Checksums;
using RsyncWin.Protocol.Delta;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Protocol.Tests.Delta;

/// <summary>
/// Replays real captured push-direction generator requests (server = generator+receiver on a push)
/// through <see cref="SumRequestReader"/>: the fresh-push/directory/root shapes in
/// <c>ssh31-push-rt</c>, the real block-sum head in <c>ssh31-push-delta</c> (spot-checked against
/// <see cref="SignatureGenerator"/> over the exact patched basis the capture ran against), and the
/// persistent-ndx-state redo re-request in <c>ssh31-push-redo</c>. See
/// <c>test-fixtures/capture/capture-push-p7.sh</c> for the capture recipe.
/// </summary>
public class SumRequestReaderTests
{
    private const int Proto31PrologueLength = 41;

    // xxh128 digest length — the negotiated xfer_sum_len for all three push captures (same offer
    // list "xxh128 xxh3 xxh64 md5 md4 none" as the pull captures, same winner).
    private const int DigestLength = 16;

    private static MultiplexReader CaptureReader(string vector) =>
        new(PipeReader.Create(new MemoryStream(TestFixtures.Bytes(vector, "s2c.bin")[Proto31PrologueLength..])));

    [Fact]
    public async Task PushRt_ParsesRootDirectoryAndFreshFileRequests_InOrder()
    {
        var reader = CaptureReader("ssh31-push-rt");
        var ndxCodec = new NdxCodec();

        // Root '.', six fresh files, one subdir, one nested fresh file — matches the sorted flist
        // order pinned by FileListReaderTests.PullRt_DecodesAndSorts_TheGoldenTree (same tree).
        (int Ndx, ItemFlags Iflags)[] expectedAttributeOnly = [(0, (ItemFlags)0x0008), (7, (ItemFlags)0x6000)];
        int[] expectedFreshFiles = [1, 2, 3, 4, 5, 6, 8];

        var seen = new List<SumRequest>();
        while (true)
        {
            SumRequest request = await SumRequestReader.ReadAsync(reader, ndxCodec, DigestLength);
            if (request.IsDone)
                break;
            seen.Add(request);
        }

        Assert.Equal(9, seen.Count);

        foreach (var (ndx, iflags) in expectedAttributeOnly)
        {
            SumRequest request = seen.Single(r => r.Ndx == ndx);
            Assert.Equal(iflags, request.Iflags);
            Assert.Null(request.SumHead);
            Assert.Null(request.BlockSums);
        }

        foreach (int ndx in expectedFreshFiles)
        {
            SumRequest request = seen.Single(r => r.Ndx == ndx);
            Assert.Equal((ItemFlags)0xA000, request.Iflags);
            Assert.True(request.Iflags.HasFlag(ItemFlags.Transfer));
            Assert.Equal(SumHeader.Null, request.SumHead);
            Assert.Empty(request.BlockSums!);
        }

        // Five NDX_DONE markers close the request phase (DONE#1, then the #2/#3/#4 burst, then the
        // goodbye #5 — docs/wire-notes.md "Push direction" section).
        for (int i = 1; i < 5; i++)
            Assert.True((await SumRequestReader.ReadAsync(reader, ndxCodec, DigestLength)).IsDone);
    }

    [Fact]
    public async Task PushDelta_ParsesRealSumHead_AndBlockSumsMatchSignatureGenerator()
    {
        var reader = CaptureReader("ssh31-push-delta");
        var ndxCodec = new NdxCodec();

        SumRequest request = await SumRequestReader.ReadAsync(reader, ndxCodec, DigestLength);

        Assert.Equal(0, request.Ndx);
        Assert.Equal((ItemFlags)0x8008, request.Iflags); // TRANSFER|REPORT_TIME
        Assert.Equal(new SumHeader(429, 700, 2, 400), request.SumHead);
        Assert.Equal(429, request.BlockSums!.Count);

        // The exact basis the capture ran against: the pushed file's post-transfer content
        // (result.bin, == the source tree's b003_300k.bin) with the two literal regions patched
        // back to their pre-transfer bytes (capture-push-p7.sh E2 recipe).
        byte[] basis = TestFixtures.Bytes("ssh31-push-delta", "result.bin");
        Encoding.ASCII.GetBytes("XXXXXXXX").CopyTo(basis, 1000);
        Encoding.ASCII.GetBytes("YYYY").CopyTo(basis, 150000);

        // checksum_seed measured off this capture's own handshake (s2c.bin bytes 37..41 LE).
        const int captureSeed = unchecked((int)0x6a51ee1c);
        using var basisStream = new MemoryStream(basis, writable: false);
        SignatureResult signature = await SignatureGenerator.GenerateAsync(
            basisStream, ChecksumAlgorithm.XxHash128, captureSeed, checksumSeedFix: false);

        Assert.Equal(request.SumHead, signature.Header);

        BlockSumEntry first = request.BlockSums[0];
        BlockSumEntry last = request.BlockSums[^1];
        int entrySize = 4 + signature.Header.StrongSumLength;
        ReadOnlySpan<byte> expectedFirst = signature.Wire.AsSpan(SumHeader.Size, entrySize);
        ReadOnlySpan<byte> expectedLast = signature.Wire.AsSpan(
            SumHeader.Size + entrySize * (signature.Header.Count - 1), entrySize);

        Assert.Equal(BitConverter.ToUInt32(expectedFirst), first.WeakSum);
        Assert.Equal(expectedFirst[4..].ToArray(), first.StrongSum);
        Assert.Equal(BitConverter.ToUInt32(expectedLast), last.WeakSum);
        Assert.Equal(expectedLast[4..].ToArray(), last.StrongSum);
    }

    [Fact]
    public async Task PushRedo_PersistsNdxState_AndCarriesFullLengthSignatureOnReRequest()
    {
        var reader = CaptureReader("ssh31-push-redo");
        var ndxCodec = new NdxCodec();

        SumRequest phase0 = await SumRequestReader.ReadAsync(reader, ndxCodec, DigestLength);
        Assert.Equal(0, phase0.Ndx);
        Assert.Equal((ItemFlags)0x8008, phase0.Iflags);
        Assert.Equal(new SumHeader(2048, 2048, 2, 0), phase0.SumHead);
        Assert.Equal(2048, phase0.BlockSums!.Count);

        SumRequest done1 = await SumRequestReader.ReadAsync(reader, ndxCodec, DigestLength);
        Assert.True(done1.IsDone);

        // Phase-1 redo re-request for the SAME ndx, on the SAME persistent codec state: the wire
        // re-encodes ndx 0 as the escape form (FE 00 00), never a fresh 01 — the codec must decode
        // it back to 0, proving state carried across the DONE boundary (docs/transfer-spec.md §6).
        SumRequest redo = await SumRequestReader.ReadAsync(reader, ndxCodec, DigestLength);
        Assert.Equal(0, redo.Ndx);
        Assert.Equal((ItemFlags)0x8008, redo.Iflags);
        Assert.Equal(new SumHeader(2048, 2048, 16, 0), redo.SumHead);
        Assert.Equal(2048, redo.BlockSums!.Count);
        Assert.All(redo.BlockSums, entry => Assert.Equal(16, entry.StrongSum.Length));

        for (int i = 0; i < 4; i++)
            Assert.True((await SumRequestReader.ReadAsync(reader, ndxCodec, DigestLength)).IsDone);
    }

    [Fact]
    public async Task MalformedSumHead_ThrowsInvalidDataException()
    {
        // ndx 0 (fresh codec -> byte 01), iflags TRANSFER only, then a head with a negative block
        // count — a desynced/hostile stream, never silently truncated or skipped.
        byte[] payload =
        [
            0x01, 0x00, 0x80, // ndx=0, iflags=0x8000 (TRANSFER)
            0xFF, 0xFF, 0xFF, 0xFF, // count = -1
            0x00, 0x00, 0x00, 0x00, // blength = 0
            0x00, 0x00, 0x00, 0x00, // s2length = 0
            0x00, 0x00, 0x00, 0x00, // remainder = 0
        ];
        var reader = SyntheticReader(payload);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SumRequestReader.ReadAsync(reader, new NdxCodec(), DigestLength));
    }

    [Fact]
    public async Task HostileHugeBlockCount_ThrowsInvalidDataException_InsteadOfAllocating()
    {
        // ndx 0, iflags TRANSFER, a head with count=0x7FFFFFFF and blength=700 — the min-block-size
        // shape that would otherwise drive `new BlockSumEntry[header.Count]` to ~32 GB before a
        // single entry is read. Must throw promptly (exit-12 semantics) instead of allocating.
        byte[] payload =
        [
            0x01, 0x00, 0x80, // ndx=0, iflags=0x8000 (TRANSFER)
            0xFF, 0xFF, 0xFF, 0x7F, // count = 0x7FFFFFFF
            0xBC, 0x02, 0x00, 0x00, // blength = 700
            0x02, 0x00, 0x00, 0x00, // s2length = 2
            0x00, 0x00, 0x00, 0x00, // remainder = 0
        ];
        var reader = SyntheticReader(payload);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SumRequestReader.ReadAsync(reader, new NdxCodec(), DigestLength));
    }

    [Fact]
    public async Task UnsupportedXnamePayload_ThrowsInsteadOfSilentlyDesyncing()
    {
        // ndx 0, iflags 0x9800 (TRANSFER|XNAME_FOLLOWS) — this reader does not parse the xname
        // vstring payload, so it must fail loudly rather than misread the head that follows.
        byte[] payload = [0x01, 0x00, 0x98];
        var reader = SyntheticReader(payload);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SumRequestReader.ReadAsync(reader, new NdxCodec(), DigestLength));
    }

    private static MultiplexReader SyntheticReader(byte[] payload)
    {
        byte[] header = new byte[MuxHeader.Size];
        new MuxHeader(MessageTag.Data, payload.Length).Write(header);
        var stream = new MemoryStream([.. header, .. payload]);
        return new MultiplexReader(PipeReader.Create(stream));
    }
}
