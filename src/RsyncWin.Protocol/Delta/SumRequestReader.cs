using System.Buffers.Binary;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Protocol.Delta;

/// <summary>
/// One block-sum entry from a generator's transfer request: the 4-byte LE weak (rolling) checksum
/// plus the strong checksum truncated to the request's negotiated <c>s2length</c>. Field order and
/// widths mirror <see cref="SignatureGenerator"/>'s own wire serialization — read and write sides
/// agree byte-for-byte. Kept as a flat struct (no computed helpers) so a match-search consumer can
/// index straight into it.
/// </summary>
public readonly record struct BlockSumEntry(uint WeakSum, byte[] StrongSum);

/// <summary>
/// One parsed generator request off the wire, or the <see cref="Done"/> marker for an
/// <c>NDX_DONE</c> phase boundary. <see cref="SumHead"/> and <see cref="BlockSums"/> are null
/// whenever <see cref="ItemFlags.Transfer"/> is not set (attribute-only requests carry no head at
/// all — <c>docs/transfer-spec.md</c> §1, mirrored here for the request direction).
/// </summary>
public readonly record struct SumRequest(int Ndx, ItemFlags Iflags, SumHeader? SumHead, IReadOnlyList<BlockSumEntry>? BlockSums)
{
    public bool IsDone => Ndx == RsyncConstants.NdxDone;

    /// <summary>The phase-boundary marker: <c>NDX_DONE</c>, no iflags, no payload.</summary>
    public static SumRequest Done { get; } = new(RsyncConstants.NdxDone, ItemFlags.None, null, null);
}

/// <summary>
/// Reads the sender-side view of a generator's per-file transfer requests: <c>read_ndx</c> + iflags,
/// and — iff <see cref="ItemFlags.Transfer"/> is set — the sum head plus its block-sum entries. This
/// is the read-side mirror of <see cref="SignatureGenerator"/> (which writes the same shape as the
/// pull-side generator's own request): on a push, the SERVER is generator+receiver and sends these
/// requests to us, the sender (<c>docs/transfer-spec.md</c> §1, <c>docs/codec-spec.md</c> §5).
/// </summary>
public static class SumRequestReader
{
    /// <summary>
    /// Upper bound on <see cref="SumHeader.Count"/> before it is trusted enough to allocate against:
    /// 2^24 blocks. <see cref="SumHeader.Read"/> only rejects a negative count, so a hostile head
    /// (e.g. count=0x7FFFFFFF, blength=700) would otherwise drive <c>new BlockSumEntry[header.Count]</c>
    /// to ~32 GB before a single entry is read — an uncaught <see cref="OutOfMemoryException"/>
    /// instead of the exit-12 protocol-stream error a hostile/desynced peer should produce. 2^24
    /// blocks at the 700-byte floor is already ~11 TB of basis file, far beyond any real transfer.
    /// </summary>
    private const int MaxBlockCount = 16_777_216;

    /// <param name="input">Demuxed data channel.</param>
    /// <param name="ndxCodec">
    /// The stateful ndx decoder for this direction. Callers own it and must reuse the SAME instance
    /// across every call for the life of the session — the delta-from-previous encoding, and
    /// <c>NDX_DONE</c>'s "state untouched" rule, persist across phase boundaries
    /// (<c>docs/transfer-spec.md</c> §6: a redo re-request re-encodes on the same persistent state).
    /// </param>
    /// <param name="digestLength">
    /// Negotiated transfer-checksum digest length (<c>xfer_sum_len</c>) — the upper bound on the
    /// head's <c>s2length</c>, the same bound <see cref="SumHeader.Read"/> enforces on any sum head.
    /// A phase-0 request truncates to a short s2length; a redo re-request carries the full digest
    /// length instead (§6) — both are legal against this same bound.
    /// </param>
    /// <exception cref="InvalidDataException">
    /// A malformed sum head (delegated to <see cref="SumHeader.Read"/>), or iflags carrying
    /// <see cref="ItemFlags.BasisTypeFollows"/> / <see cref="ItemFlags.XnameFollows"/> — payload
    /// shapes this reader does not parse. Failing loudly here is deliberate: silently skipping an
    /// unparsed payload would misalign every byte read after it.
    /// </exception>
    public static async ValueTask<SumRequest> ReadAsync(
        MultiplexReader input, NdxCodec ndxCodec, int digestLength, CancellationToken cancellationToken = default)
    {
        int ndx = await input.ReadNdxAsync(ndxCodec, cancellationToken);
        if (ndx == RsyncConstants.NdxDone)
            return SumRequest.Done;

        ItemFlags iflags = await input.ReadItemFlagsAsync(cancellationToken);
        if ((iflags & (ItemFlags.BasisTypeFollows | ItemFlags.XnameFollows)) != 0)
            throw new InvalidDataException(
                $"sum request: iflags 0x{(ushort)iflags:x4} carries a fnamecmp_type/xname payload — not supported by this reader");

        if (!iflags.HasFlag(ItemFlags.Transfer))
            return new SumRequest(ndx, iflags, null, null);

        SumHeader header = SumHeader.Read(
            await input.ReadDataExactlyAsync(SumHeader.Size, cancellationToken), digestLength);
        if (header.Count > MaxBlockCount)
            throw new InvalidDataException(
                $"sum head: block count {header.Count} exceeds the {MaxBlockCount} sanity bound — hostile or desynced stream");

        var entries = new BlockSumEntry[header.Count];
        int entrySize = 4 + header.StrongSumLength;
        for (int i = 0; i < header.Count; i++)
        {
            byte[] entry = await input.ReadDataExactlyAsync(entrySize, cancellationToken);
            entries[i] = new BlockSumEntry(BinaryPrimitives.ReadUInt32LittleEndian(entry), entry[4..]);
        }

        return new SumRequest(ndx, iflags, header, entries);
    }
}
