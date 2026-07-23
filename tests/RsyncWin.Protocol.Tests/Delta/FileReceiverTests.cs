using System.Buffers.Binary;
using System.IO.Pipelines;
using RsyncWin.Protocol.Checksums;
using RsyncWin.Protocol.Delta;
using RsyncWin.Protocol.Mux;

namespace RsyncWin.Protocol.Tests.Delta;

/// <summary>
/// Synthetic (non-capture) coverage for <see cref="FileReceiver"/>'s block-reference path: the
/// changed-file degrade paths (no basis, truncated basis) a real capture would never exercise, the
/// still-hostile out-of-range index path, plus the remainder-length rule and literal/matched byte
/// accounting on a small hand-built token stream.
/// </summary>
public class FileReceiverTests
{
    private static byte[] Frame(MessageTag tag, byte[] payload)
    {
        byte[] frame = new byte[MuxHeader.Size + payload.Length];
        new MuxHeader(tag, payload.Length).Write(frame);
        payload.CopyTo(frame, MuxHeader.Size);
        return frame;
    }

    private static byte[] Int32Le(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static MultiplexReader ReaderOver(byte[] payload) =>
        new(PipeReader.Create(new MemoryStream(Frame(MessageTag.Data, payload))));

    [Fact]
    public async Task BlockReference_WithNoBasisProvided_ZeroFillsAndTrailerMismatches()
    {
        // count=1, blength=8, s2length=2, remainder=0 — not a full-transfer request (non-null head).
        // The basis was deleted between signing and this reply: a changed-file window, not a
        // protocol error (see FileReceiver.ReceiveAsync's basis param doc). The receiver must
        // zero-fill rather than throw, and let the whole-file trailer comparison catch the mismatch.
        byte[] sumHead = [.. Int32Le(1), .. Int32Le(8), .. Int32Le(2), .. Int32Le(0)];
        byte[] tokens = [.. Int32Le(-1), .. Int32Le(0)]; // block reference to index 0, then EOF

        // Trailer computed over the TRUE (pre-deletion) content, as the real sender would send it —
        // the receiver has no basis at all here, so its zero-filled reconstruction must not match.
        var hasher = StrongChecksum.CreateFileSum(ChecksumAlgorithm.Md5, seed: 0, protocol: 31);
        hasher.Append("ABCDEFGH"u8);
        byte[] trailer = new byte[16];
        hasher.Finish(trailer);

        byte[] payload = [.. sumHead, .. tokens, .. trailer];
        var reader = ReaderOver(payload);
        using var output = new MemoryStream();

        FileReceiveResult result = await FileReceiver.ReceiveAsync(
            reader, output, ChecksumAlgorithm.Md5, seed: 0, protocol: 31, trailerLength: 16,
            cancellationToken: default, basis: null);

        Assert.False(result.ChecksumMatches);
        Assert.Equal(0, result.LiteralBytes);
        Assert.Equal(8, result.MatchedBytes);
        Assert.Equal(new byte[8], output.ToArray());
    }

    [Fact]
    public async Task BlockReference_BasisShorterThanSignature_ZeroFillsTailAndMismatches()
    {
        // count=1, blength=8 — the signature claims an 8-byte block, but the basis was truncated to
        // 4 bytes in the window between signing and this reply. ReadBasisBlockAsync must zero-fill
        // the unread tail instead of throwing.
        byte[] sumHead = [.. Int32Le(1), .. Int32Le(8), .. Int32Le(2), .. Int32Le(0)];
        byte[] tokens = [.. Int32Le(-1), .. Int32Le(0)]; // block reference to index 0, then EOF

        // Trailer computed over the TRUE (pre-truncation) content, so the zero-tail reconstruction
        // mismatches.
        var hasher = StrongChecksum.CreateFileSum(ChecksumAlgorithm.Md5, seed: 0, protocol: 31);
        hasher.Append("ABCDEFGH"u8);
        byte[] trailer = new byte[16];
        hasher.Finish(trailer);

        byte[] payload = [.. sumHead, .. tokens, .. trailer];
        var reader = ReaderOver(payload);
        using var basis = new MemoryStream("ABCD"u8.ToArray());
        using var output = new MemoryStream();

        FileReceiveResult result = await FileReceiver.ReceiveAsync(
            reader, output, ChecksumAlgorithm.Md5, seed: 0, protocol: 31, trailerLength: 16,
            cancellationToken: default, basis: basis);

        Assert.False(result.ChecksumMatches);
        Assert.Equal(0, result.LiteralBytes);
        Assert.Equal(8, result.MatchedBytes);
        Assert.Equal([.. "ABCD"u8.ToArray(), 0, 0, 0, 0], output.ToArray());
    }

    [Fact]
    public async Task BlockReference_IndexOutOfRange_Throws()
    {
        // count=1 — only index 0 is legal; the token references index 5.
        byte[] sumHead = [.. Int32Le(1), .. Int32Le(8), .. Int32Le(2), .. Int32Le(0)];
        byte[] payload = [.. sumHead, .. Int32Le(-6)]; // -(5+1)
        var reader = ReaderOver(payload);
        using var basis = new MemoryStream(new byte[8]);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await FileReceiver.ReceiveAsync(
            reader, Stream.Null, ChecksumAlgorithm.Md5, seed: 0, protocol: 31, trailerLength: 16,
            cancellationToken: default, basis: basis));
    }

    [Fact]
    public async Task ThreeBlockStream_RemainderLastBlock_AndLiteralMatchedAccounting()
    {
        // 10-byte basis, blength=4 → blocks [ABCD][EFGH][IJ] (last block short by the remainder rule).
        byte[] basisBytes = "ABCDEFGHIJ"u8.ToArray();
        byte[] sumHead = [.. Int32Le(3), .. Int32Le(4), .. Int32Le(2), .. Int32Le(2)]; // count,blength,s2len,remainder

        // Wire order: 3 literal bytes "XYZ", then block refs 0, 1, 2 (remainder block last), then EOF.
        byte[] literal = "XYZ"u8.ToArray();
        byte[] tokens = [.. Int32Le(literal.Length), .. literal, .. Int32Le(-1), .. Int32Le(-2), .. Int32Le(-3), .. Int32Le(0)];

        var hasher = StrongChecksum.CreateFileSum(ChecksumAlgorithm.Md5, seed: 0, protocol: 31);
        hasher.Append(literal);
        hasher.Append("ABCD"u8);
        hasher.Append("EFGH"u8);
        hasher.Append("IJ"u8);
        byte[] trailer = new byte[16];
        hasher.Finish(trailer);

        byte[] payload = [.. sumHead, .. tokens, .. trailer];
        var reader = ReaderOver(payload);
        using var basis = new MemoryStream(basisBytes);
        using var output = new MemoryStream();

        FileReceiveResult result = await FileReceiver.ReceiveAsync(
            reader, output, ChecksumAlgorithm.Md5, seed: 0, protocol: 31, trailerLength: 16,
            cancellationToken: default, basis: basis);

        Assert.True(result.ChecksumMatches);
        Assert.Equal(3, result.LiteralBytes);
        Assert.Equal(10, result.MatchedBytes);
        Assert.Equal("XYZABCDEFGHIJ"u8.ToArray(), output.ToArray());
    }

    [Fact]
    public async Task ProgressCallback_ReportsEveryReconstructedByte_LiteralThenEachBlock()
    {
        // Same 3-byte literal + three-block stream as above: the progress callback must fire once per
        // literal chunk and once per copied basis block, and its running sum must equal the file's
        // literal + matched byte total (the invariant the --progress bar relies on).
        byte[] basisBytes = "ABCDEFGHIJ"u8.ToArray();
        byte[] sumHead = [.. Int32Le(3), .. Int32Le(4), .. Int32Le(2), .. Int32Le(2)];
        byte[] literal = "XYZ"u8.ToArray();
        byte[] tokens = [.. Int32Le(literal.Length), .. literal, .. Int32Le(-1), .. Int32Le(-2), .. Int32Le(-3), .. Int32Le(0)];

        var hasher = StrongChecksum.CreateFileSum(ChecksumAlgorithm.Md5, seed: 0, protocol: 31);
        hasher.Append(literal);
        hasher.Append("ABCD"u8);
        hasher.Append("EFGH"u8);
        hasher.Append("IJ"u8);
        byte[] trailer = new byte[16];
        hasher.Finish(trailer);

        byte[] payload = [.. sumHead, .. tokens, .. trailer];
        var reader = ReaderOver(payload);
        using var basis = new MemoryStream(basisBytes);
        using var output = new MemoryStream();

        long advanced = 0;
        var chunks = new List<long>();
        void OnAdvance(long b) { advanced += b; chunks.Add(b); }

        FileReceiveResult result = await FileReceiver.ReceiveAsync(
            reader, output, ChecksumAlgorithm.Md5, seed: 0, protocol: 31, trailerLength: 16,
            cancellationToken: default, basis: basis, onBytesAdvanced: OnAdvance);

        Assert.True(result.ChecksumMatches);
        Assert.Equal(result.LiteralBytes + result.MatchedBytes, advanced);
        Assert.Equal(13, advanced);
        // literal run (3), then blocks 0,1,2 with the remainder rule on the last (4, 4, 2).
        Assert.Equal([3L, 4L, 4L, 2L], chunks);
    }
}
