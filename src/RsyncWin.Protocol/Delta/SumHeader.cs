using System.Buffers.Binary;

namespace RsyncWin.Protocol.Delta;

/// <summary>
/// The sum head (<c>write_sum_head</c>/<c>read_sum_head</c>): four plain 4-byte LE ints —
/// count, blength, s2length, remainder — on <em>every</em> protocol version, never varints.
/// A null sum head (all zeros) requests a full transfer.
/// </summary>
/// <remarks>
/// Specified in <c>docs/codec-spec.md</c> §7; the 300000-length head is measured ground truth,
/// and the all-zero form is visible in the captured generator requests
/// (<c>vectors/ssh31-pull-rt/c2s.bin</c>).
/// </remarks>
public readonly record struct SumHeader(int Count, int BlockLength, int StrongSumLength, int Remainder)
{
    public const int Size = 16;

    /// <summary>The all-zero head: "send me the whole file".</summary>
    public static SumHeader Null => default;

    public bool IsFullTransferRequest => this == default;

    /// <summary>Builds the head the generator sends for a basis file of the given sizing.</summary>
    public static SumHeader For(BlockSizes sizes)
    {
        if (sizes.Count > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(sizes), "block count exceeds int32 — file is unprocessable");
        return new SumHeader((int)sizes.Count, (int)sizes.BlockLength, sizes.StrongSumLength, (int)sizes.Remainder);
    }

    public void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination, Count);
        BinaryPrimitives.WriteInt32LittleEndian(destination[4..], BlockLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination[8..], StrongSumLength);
        BinaryPrimitives.WriteInt32LittleEndian(destination[12..], Remainder);
    }

    /// <summary>
    /// Decodes and validates a sum head. <paramref name="digestLength"/> is the negotiated
    /// transfer-checksum digest size that bounds s2length.
    /// </summary>
    /// <exception cref="InvalidDataException">Any field out of range — a protocol-stream error
    /// (exit 12): a desynced stream reads as garbage sizes long before anything else fails.</exception>
    public static SumHeader Read(ReadOnlySpan<byte> source, int digestLength)
    {
        var header = new SumHeader(
            BinaryPrimitives.ReadInt32LittleEndian(source),
            BinaryPrimitives.ReadInt32LittleEndian(source[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(source[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(source[12..]));

        if (header.Count < 0)
            throw new InvalidDataException($"sum head: negative block count {header.Count}");
        if (header.BlockLength is < 0 or > RsyncConstants.MaxBlockSize)
            throw new InvalidDataException($"sum head: block length {header.BlockLength} outside [0, {RsyncConstants.MaxBlockSize}]");
        if (header.StrongSumLength < 0 || header.StrongSumLength > digestLength)
            throw new InvalidDataException($"sum head: s2length {header.StrongSumLength} outside [0, {digestLength}]");
        if (header.Remainder < 0 || header.Remainder > header.BlockLength)
            throw new InvalidDataException($"sum head: remainder {header.Remainder} outside [0, {header.BlockLength}]");

        return header;
    }
}
