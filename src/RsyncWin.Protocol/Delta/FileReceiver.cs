using RsyncWin.Protocol.Checksums;
using RsyncWin.Protocol.Mux;

namespace RsyncWin.Protocol.Delta;

public sealed record FileReceiveResult(
    SumHeader SumHeadEcho,
    long BytesWritten,
    long MatchedBytes,
    byte[] SenderFileSum,
    byte[] ComputedFileSum)
{
    public bool ChecksumMatches => SenderFileSum.AsSpan().SequenceEqual(ComputedFileSum);

    /// <summary>Alias for <see cref="BytesWritten"/>: literal bytes received over the wire, excluding
    /// bytes copied from the basis via block-reference tokens (see <see cref="MatchedBytes"/>).</summary>
    public long LiteralBytes => BytesWritten;
}

/// <summary>
/// Receives one file's transfer body from the data channel: the sender's sum-head echo, the token
/// stream, and the whole-file checksum trailer. Reconstructed bytes go to
/// <paramref name="destination"/> as they arrive.
/// </summary>
public static class FileReceiver
{
    /// <param name="destination">Sink for the reconstructed file bytes.</param>
    /// <param name="algorithm">Negotiated transfer checksum, for our own whole-file computation.</param>
    /// <param name="seed">Session checksum seed (whole-file seed rules live in <see cref="WholeFileChecksum"/>).</param>
    /// <param name="protocol">Negotiated protocol (selects the proto-29 MD4 seed rule).</param>
    /// <param name="trailerLength">Digest length of the sender's whole-file sum on the wire.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="basis">
    /// Seekable basis stream for block-reference tokens (a real delta transfer). Null for a full
    /// transfer, where every token is expected to be a literal. A block reference with no basis
    /// while the echoed head is NOT a full-transfer request is not a protocol error — it means the
    /// basis file was deleted, locked, or truncated in the window between signing and this reply.
    /// We degrade the same way canonical rsync's <c>map_ptr</c> does for a changed file: zero-fill
    /// the missing bytes so the running whole-file checksum ends up wrong and the caller's trailer
    /// comparison routes the file to redo instead of aborting the whole session. Only a
    /// block-reference token inside an actual full-transfer request, or an out-of-range block
    /// index, is treated as a hostile/desynced peer and still throws. The block sizing
    /// (blength/remainder/count) needed to resolve a reference comes from <c>sumHead</c>, the
    /// echoed sum head we already read off the wire above — not from a separate parameter.
    /// </param>
    public static async ValueTask<FileReceiveResult> ReceiveAsync(
        MultiplexReader input,
        Stream destination,
        ChecksumAlgorithm algorithm,
        int seed,
        int protocol,
        int trailerLength,
        CancellationToken cancellationToken = default,
        Stream? basis = null,
        CompressionMethod compression = CompressionMethod.None)
    {
        var sumHead = SumHeader.Read(
            await input.ReadDataExactlyAsync(SumHeader.Size, cancellationToken), trailerLength);

        var hasher = StrongChecksum.CreateFileSum(algorithm, seed, protocol);

        (long written, long matched) = compression == CompressionMethod.Zlibx
            ? await ReceiveZlibxTokensAsync(input, destination, hasher, sumHead, basis, cancellationToken)
            : await ReceivePlainTokensAsync(input, destination, hasher, sumHead, basis, cancellationToken);

        byte[] senderSum = await input.ReadDataExactlyAsync(trailerLength, cancellationToken);
        byte[] computed = new byte[16];
        int computedLength = hasher.Finish(computed);

        return new FileReceiveResult(sumHead, written, matched, senderSum, computed[..computedLength]);
    }

    private static async ValueTask<(long Written, long Matched)> ReceivePlainTokensAsync(
        MultiplexReader input, Stream destination, WholeFileChecksum hasher, SumHeader sumHead,
        Stream? basis, CancellationToken cancellationToken)
    {
        long written = 0;
        long matched = 0;

        while (true)
        {
            Token token = await Token.ReadAsync(input, cancellationToken);
            if (token.IsEnd)
                break;

            if (token.IsBlockReference)
            {
                matched += await CopyBasisBlockAsync(token.BlockIndex, sumHead, basis, destination, hasher, cancellationToken);
                continue;
            }

            for (int remaining = token.LiteralLength; remaining > 0;)
            {
                int chunk = Math.Min(remaining, RsyncConstants.ChunkSize);
                byte[] literal = await input.ReadDataExactlyAsync(chunk, cancellationToken);
                hasher.Append(literal);
                await destination.WriteAsync(literal, cancellationToken);
                remaining -= chunk;
            }
            written += token.LiteralLength;
        }

        return (written, matched);
    }

    /// <summary>One decoded zlibx operation, in output order: either a literal run (an index into the
    /// buffered <c>runs</c> list) or a matched-block reference (start block + run length).</summary>
    private readonly record struct ZlibxOp(bool IsLiteral, int RunIndex, int BlockStart, int BlockCount);

    /// <summary>
    /// Decodes the zlibx (<c>-z</c>) compressed token stream (docs/transfer-spec.md §2a). Because
    /// rsync's zlibx sender keeps ONE deflate window across all literal runs (only matched blocks are
    /// excluded), a later run can back-reference an earlier one (verified against real rsync,
    /// <c>ssh31-pull-z-crossrun</c>) — so runs cannot be inflated in isolation. We buffer every run's
    /// compressed payload and the interleaving operation list, then inflate the run stream continuously
    /// (<see cref="ZlibxTokenCodec.InflateRuns"/>): a prefix inflate of the first <c>k</c> runs gives
    /// the cumulative literal length ending run <c>k-1</c>, so each run's slice of the fully-inflated
    /// literal buffer is exactly bounded. Matched blocks are then copied from the basis in output order.
    /// </summary>
    private static async ValueTask<(long Written, long Matched)> ReceiveZlibxTokensAsync(
        MultiplexReader input, Stream destination, WholeFileChecksum hasher, SumHeader sumHead,
        Stream? basis, CancellationToken cancellationToken)
    {
        var runs = new List<byte[]>();
        var currentRun = new List<byte>();
        var ops = new List<ZlibxOp>();
        // Running block cursor for the relative token arithmetic: a token's start block is
        // previousBlock + delta, and previousBlock advances to the run's last block. rsync's encoder
        // initializes this cursor to 0 (capture-pinned by ssh31-pull-z-delta: block 0, then runs
        // 2..213 and 215..428 decode exactly under this rule).
        int previousBlock = 0;

        void CloseRun()
        {
            if (currentRun.Count == 0)
                return; // no DEFLATED_DATA since the last match/start: no literal op for this gap
            ops.Add(new ZlibxOp(IsLiteral: true, RunIndex: runs.Count, BlockStart: 0, BlockCount: 0));
            runs.Add(currentRun.ToArray());
            currentRun.Clear();
        }

        while (true)
        {
            byte flag = await input.ReadDataByteAsync(cancellationToken);
            if (flag == ZlibxTokenCodec.EndFlag)
            {
                CloseRun();
                break;
            }

            if ((flag & 0xC0) == ZlibxTokenCodec.DeflatedData)
            {
                int length = ((flag & 0x3F) << 8) | await input.ReadDataByteAsync(cancellationToken);
                byte[] chunk = await input.ReadDataExactlyAsync(length, cancellationToken);
                currentRun.AddRange(chunk);
                continue;
            }

            // A match token: close any pending literal run first, then record the block reference.
            CloseRun();
            (int startBlock, int count) = await ReadMatchTokenAsync(flag, input, previousBlock, cancellationToken);
            ops.Add(new ZlibxOp(IsLiteral: false, RunIndex: 0, BlockStart: startBlock, BlockCount: count));
            previousBlock = startBlock + count - 1;
        }

        // Continuous inflation: cumulative[k] = total literal bytes of runs [0, k). The k == runs.Count
        // pass yields the whole (correctly cross-referenced) literal buffer; earlier passes only need
        // their length. For a full transfer there is exactly one run, so this is a single inflate.
        int[] cumulative = new int[runs.Count + 1];
        byte[] allLiterals = [];
        for (int k = 1; k <= runs.Count; k++)
        {
            byte[] decoded = ZlibxTokenCodec.InflateRuns(runs, k);
            cumulative[k] = decoded.Length;
            if (k == runs.Count)
                allLiterals = decoded;
        }

        long written = 0;
        long matched = 0;
        foreach (ZlibxOp op in ops)
        {
            if (op.IsLiteral)
            {
                ReadOnlyMemory<byte> slice = allLiterals.AsMemory(cumulative[op.RunIndex], cumulative[op.RunIndex + 1] - cumulative[op.RunIndex]);
                hasher.Append(slice.Span);
                await destination.WriteAsync(slice, cancellationToken);
                written += slice.Length;
            }
            else
            {
                for (int i = 0; i < op.BlockCount; i++)
                    matched += await CopyBasisBlockAsync(op.BlockStart + i, sumHead, basis, destination, hasher, cancellationToken);
            }
        }

        return (written, matched);
    }

    /// <summary>
    /// Decodes one match token into (startBlock, runCount). TOKEN_REL/TOKENRUN_REL carry a low-6-bit
    /// delta: <c>startBlock = previousBlock + delta</c> (the running cursor, advanced to each run's
    /// last block). TOKENRUN adds a 2-byte extra count — the run covers <c>count = extra + 1</c>
    /// consecutive blocks. *_LONG carry an absolute 4-byte block number for deltas beyond 6 bits —
    /// spec'd from rsync behavior, not capture-pinned (our vectors stay within the relative range).
    /// Pinned byte-exact for the relative forms by <c>ssh31-pull-z-delta</c>: flags <c>80/c2/c2</c>
    /// with deltas 0/2/2 decode to block 0, run 2..213, run 215..428.
    /// </summary>
    private static async ValueTask<(int StartBlock, int Count)> ReadMatchTokenAsync(
        byte flag, MultiplexReader input, int previousBlock, CancellationToken cancellationToken)
    {
        if (flag == ZlibxTokenCodec.TokenLong || flag == ZlibxTokenCodec.TokenRunLong)
        {
            byte[] b = await input.ReadDataExactlyAsync(4, cancellationToken);
            int block = b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24);
            int count = 1;
            if (flag == ZlibxTokenCodec.TokenRunLong)
                count = await ReadRunExtraAsync(input, cancellationToken) + 1;
            return (block, count);
        }

        int delta = flag & 0x3F;
        int start = previousBlock + delta;
        int runCount = 1;
        if ((flag & 0xC0) == ZlibxTokenCodec.TokenRunRel)
            runCount = await ReadRunExtraAsync(input, cancellationToken) + 1;
        return (start, runCount);
    }

    private static async ValueTask<int> ReadRunExtraAsync(MultiplexReader input, CancellationToken cancellationToken)
    {
        byte[] b = await input.ReadDataExactlyAsync(2, cancellationToken);
        return b[0] | (b[1] << 8);
    }

    /// <summary>Copies (or zero-fills) basis block <paramref name="index"/> to the destination and
    /// hasher, returning the block's byte length. Shared by the plain and zlibx token loops.</summary>
    private static async ValueTask<int> CopyBasisBlockAsync(
        int index, SumHeader sumHead, Stream? basis, Stream destination, WholeFileChecksum hasher,
        CancellationToken cancellationToken)
    {
        if (sumHead.IsFullTransferRequest)
            throw new InvalidDataException(
                $"transfer: block reference {index} inside a full transfer — stream is desynced");
        if (index < 0 || index >= sumHead.Count)
            throw new InvalidDataException(
                $"transfer: block reference {index} outside [0, {sumHead.Count})");

        int length = index == sumHead.Count - 1 && sumHead.Remainder != 0
            ? sumHead.Remainder
            : sumHead.BlockLength;
        long offset = (long)index * sumHead.BlockLength;

        // Changed-file window: the basis was deleted between signing and this reply. Zero-fill like
        // rsync's map_ptr does for a changed file rather than treating it as a desync (see the
        // ReceiveAsync <paramref name="basis"> doc).
        byte[] block = basis is null
            ? new byte[length]
            : await ReadBasisBlockAsync(basis, offset, length, cancellationToken);
        hasher.Append(block);
        await destination.WriteAsync(block, cancellationToken);
        return length;
    }

    /// <summary>
    /// Reads up to <paramref name="length"/> bytes from <paramref name="basis"/> at
    /// <paramref name="offset"/>. Always seeks explicitly (the basis may be read out of order) and
    /// loops on short reads per the <see cref="Stream.Read"/> contract. If the basis was truncated
    /// (a changed-file window, not a protocol error — see the <c>basis</c> param doc on
    /// <see cref="ReceiveAsync"/>), the unread tail is left zero-filled rather than throwing.
    /// </summary>
    private static async ValueTask<byte[]> ReadBasisBlockAsync(
        Stream basis, long offset, int length, CancellationToken cancellationToken)
    {
        basis.Seek(offset, SeekOrigin.Begin);
        byte[] block = new byte[length];
        int filled = 0;
        while (filled < length)
        {
            int read = await basis.ReadAsync(block.AsMemory(filled, length - filled), cancellationToken);
            if (read == 0)
                break; // basis shorter than the signature claims — zero-fill the rest, see summary above
            filled += read;
        }
        return block;
    }
}
