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
        Stream? basis = null)
    {
        var sumHead = SumHeader.Read(
            await input.ReadDataExactlyAsync(SumHeader.Size, cancellationToken), trailerLength);

        var hasher = StrongChecksum.CreateFileSum(algorithm, seed, protocol);
        long written = 0;
        long matched = 0;

        while (true)
        {
            Token token = await Token.ReadAsync(input, cancellationToken);
            if (token.IsEnd)
                break;

            if (token.IsBlockReference)
            {
                if (sumHead.IsFullTransferRequest)
                    throw new InvalidDataException(
                        $"transfer: block reference {token.BlockIndex} inside a full transfer — stream is desynced");

                int index = token.BlockIndex;
                if (index < 0 || index >= sumHead.Count)
                    throw new InvalidDataException(
                        $"transfer: block reference {index} outside [0, {sumHead.Count})");

                int length = index == sumHead.Count - 1 && sumHead.Remainder != 0
                    ? sumHead.Remainder
                    : sumHead.BlockLength;
                long offset = (long)index * sumHead.BlockLength;

                // Changed-file window: the basis was deleted between signing and this reply. Zero-fill
                // like rsync's map_ptr does for a changed file, rather than treating it as a desync —
                // see the <param name="basis"> doc above.
                byte[] block = basis is null
                    ? new byte[length]
                    : await ReadBasisBlockAsync(basis, offset, length, cancellationToken);
                hasher.Append(block);
                await destination.WriteAsync(block, cancellationToken);
                // Zero/short-filled bytes still count as MatchedBytes (the reconstructed-from-basis
                // path), not LiteralBytes: no literal bytes rode the wire for a block-reference token,
                // so `written` (== LiteralBytes) must stay equal to the wire's actual literal byte count.
                matched += length;
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

        byte[] senderSum = await input.ReadDataExactlyAsync(trailerLength, cancellationToken);
        byte[] computed = new byte[16];
        int computedLength = hasher.Finish(computed);

        return new FileReceiveResult(sumHead, written, matched, senderSum, computed[..computedLength]);
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
