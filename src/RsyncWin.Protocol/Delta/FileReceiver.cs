using RsyncWin.Protocol.Checksums;
using RsyncWin.Protocol.Mux;

namespace RsyncWin.Protocol.Delta;

public sealed record FileReceiveResult(
    SumHeader SumHeadEcho,
    long BytesWritten,
    byte[] SenderFileSum,
    byte[] ComputedFileSum)
{
    public bool ChecksumMatches => SenderFileSum.AsSpan().SequenceEqual(ComputedFileSum);
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
    public static async ValueTask<FileReceiveResult> ReceiveAsync(
        MultiplexReader input,
        Stream destination,
        ChecksumAlgorithm algorithm,
        int seed,
        int protocol,
        int trailerLength,
        CancellationToken cancellationToken = default)
    {
        var sumHead = SumHeader.Read(
            await input.ReadDataExactlyAsync(SumHeader.Size, cancellationToken), trailerLength);

        var hasher = StrongChecksum.CreateFileSum(algorithm, seed, protocol);
        long written = 0;

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
                throw new NotSupportedException("basis-block references are not implemented yet (P6)");
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

        return new FileReceiveResult(sumHead, written, senderSum, computed[..computedLength]);
    }
}
