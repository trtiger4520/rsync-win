using System.Buffers.Binary;
using RsyncWin.Protocol.Checksums;

namespace RsyncWin.Protocol.Delta;

/// <summary>
/// The sum head plus its fully serialized wire bytes (head + one entry per block), ready for the
/// caller to write to the mux's data channel as the generator's request for a file.
/// </summary>
public readonly record struct SignatureResult(SumHeader Header, byte[] Wire);

/// <summary>
/// Generates the signature we (receiver+generator) send for a basis file that differs from the
/// sender's copy: the sum head followed by one entry per block, each a 4-byte LE weak sum and the
/// strong sum truncated to <c>s2length</c>. This is the GENERATOR's outbound request
/// (<c>docs/transfer-spec.md</c> §6, <c>docs/codec-spec.md</c> §7) — the mirror of
/// <see cref="FileReceiver"/>, which consumes the SENDER's reply on the other side of the ndx.
/// </summary>
public static class SignatureGenerator
{
    /// <param name="basis">Seekable basis stream, read start-to-end exactly once.</param>
    /// <param name="algorithm">Negotiated transfer checksum (<c>xfer_sum_nni</c>) for the strong sum.</param>
    /// <param name="seed">Session <c>checksum_seed</c>.</param>
    /// <param name="checksumSeedFix"><c>CF_CHKSUM_SEED_FIX</c> negotiated (MD5 block-sum seed placement).</param>
    /// <param name="blockSizes">
    /// Precomputed block sizing, or null to derive it from <paramref name="basis"/>'s length via
    /// <see cref="BlockSizes.ForFileLength"/> — the normal phase-0 path.
    /// </param>
    /// <param name="s2Length">
    /// Strong-sum truncation length to put on the wire, or null to use the value <paramref
    /// name="blockSizes"/> derived. Phase 0 leaves this null; a redo request instead passes
    /// <c>MIN(16, xfer_sum_len)</c> — the full negotiated digest length — per
    /// <c>docs/transfer-spec.md</c> §6. The caller decides which one applies; this method only
    /// serializes whatever length it is given.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="s2Length"/> (explicit or derived) is outside <c>[0, digest length]</c> for
    /// <paramref name="algorithm"/> — the same bound <c>SumHeader.Read</c> enforces on the wire.
    /// </exception>
    /// <exception cref="InvalidDataException"><paramref name="basis"/> ended before a full block could be read.</exception>
    public static async ValueTask<SignatureResult> GenerateAsync(
        Stream basis,
        ChecksumAlgorithm algorithm,
        int seed,
        bool checksumSeedFix,
        BlockSizes? blockSizes = null,
        int? s2Length = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(basis);

        BlockSizes sizes = blockSizes ?? BlockSizes.ForFileLength(basis.Length);
        int strongSumLength = s2Length ?? sizes.StrongSumLength;

        int digestLength = StrongChecksum.DigestLength(algorithm);
        if (strongSumLength < 0 || strongSumLength > digestLength)
            throw new ArgumentOutOfRangeException(
                nameof(s2Length), strongSumLength, $"s2length outside [0, {digestLength}] for {algorithm}");

        var header = SumHeader.For(sizes) with { StrongSumLength = strongSumLength };

        int entrySize = 4 + strongSumLength;
        byte[] wire = new byte[SumHeader.Size + checked(header.Count * entrySize)];
        header.Write(wire);

        byte[] block = new byte[(int)sizes.BlockLength];
        byte[] digest = new byte[16]; // widest strong digest (md4/md5/xxh128); truncated per-block below
        int offset = SumHeader.Size;

        for (int i = 0; i < header.Count; i++)
        {
            int length = i == header.Count - 1 && header.Remainder != 0
                ? header.Remainder
                : header.BlockLength;

            await ReadBlockAsync(basis, block.AsMemory(0, length), i, cancellationToken);
            ReadOnlySpan<byte> blockSpan = block.AsSpan(0, length);

            uint weak = RollingChecksum.Compute(blockSpan);
            BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(offset, 4), weak);

            StrongChecksum.ComputeBlockSum(algorithm, seed, checksumSeedFix, blockSpan, digest);
            digest.AsSpan(0, strongSumLength).CopyTo(wire.AsSpan(offset + 4, strongSumLength));

            offset += entrySize;
        }

        return new SignatureResult(header, wire);
    }

    /// <summary>
    /// Reads exactly <paramref name="buffer"/>.Length bytes, looping on short reads per the
    /// <see cref="Stream.Read"/> contract (mirrors <c>FileReceiver.ReadBasisBlockAsync</c>). The
    /// basis is read sequentially start-to-end, so no explicit seek is needed here.
    /// </summary>
    private static async ValueTask ReadBlockAsync(
        Stream basis, Memory<byte> buffer, int blockIndex, CancellationToken cancellationToken)
    {
        int filled = 0;
        while (filled < buffer.Length)
        {
            int read = await basis.ReadAsync(buffer[filled..], cancellationToken);
            if (read == 0)
                throw new InvalidDataException(
                    $"signature: basis stream ended early reading block {blockIndex} " +
                    $"({filled}/{buffer.Length} bytes)");
            filled += read;
        }
    }
}
