using System.Buffers.Binary;
using RsyncWin.Protocol.Checksums;
using RsyncWin.Protocol.Mux;

namespace RsyncWin.Protocol.Delta;

// PROVENANCE: canonical rsync master (GPLv3) — `match.c:match_sums/matched/hash_search` — was read
// for BEHAVIOR ONLY (docs/transfer-spec.md §2, §6): the token framing (plain LE int32, sign convention,
// remainder-block length rule, 32768 literal chunking) and the "prefer the block adjacent to the
// previous match" (want_i) search-order heuristic. No expression (code) was copied or transcribed
// from match.c — the search below is independently written from that documented behavior, per the
// project's licensing rule (CLAUDE.md: "or regenerated from the documented algorithm"). The weak
// rolling checksum (RollingChecksum) and truncated strong checksum (StrongChecksum.ComputeBlockSum)
// this reuses are themselves already provenance-cleared in their own files.

/// <summary>
/// One block's signature from the generator's request: the weak (rolling) checksum plus the strong
/// checksum truncated to the request's <c>s2length</c>. A flat, read-side-agnostic input shape —
/// deliberately not <see cref="SumRequestReader"/>'s model, so this class has no compile-time
/// dependency on that reader.
/// </summary>
public readonly record struct BlockSignature(uint WeakSum, byte[] StrongSum);

/// <summary>Byte totals for one file's token stream: how much rode the wire as literals versus how
/// much was satisfied by a block reference into the receiver's basis (P7 T6: lets a caller populate
/// its own literal/matched counters without re-parsing the token stream it just asked this class to
/// write).</summary>
public readonly record struct MatchResult(long LiteralBytes, long MatchedBytes);

/// <summary>
/// The sender's rolling match search (<c>match.c:hash_search</c> behavior, <c>token.c</c> framing):
/// scans a source file against a received block signature list and writes the resulting token
/// stream — literal runs and block references — to the data channel. The byte-exact inverse of
/// <see cref="FileReceiver"/>'s token consumption.
/// </summary>
/// <remarks>
/// <para>
/// Algorithm: maintain a rolling weak checksum over a window the size of the signature's block
/// length (shrinking only in the final partial window at end-of-source). On a weak-sum hit, verify
/// the truncated strong checksum before accepting a match — a weak collision alone is not a match.
/// On acceptance, flush any pending literal bytes (chunked at exactly <see cref="RsyncConstants.ChunkSize"/>,
/// mirroring <c>simple_send_token</c>), emit the block-reference token, and jump the scan past the
/// matched region (recomputing the weak checksum fresh at the new position — a jump invalidates the
/// rolling state). No match at a position slides the window by one byte (O(1) via
/// <see cref="RollingChecksum.Roll"/>) rather than recomputing.
/// </para>
/// <para>
/// <b>want_i adjacency preference</b>: when a position's weak+strong checksum matches more than one
/// signature block (identical block content repeated in the basis), the block immediately following
/// the previously matched block is preferred over an earlier-index match. This is a behavior-derived
/// heuristic (not independently capture-pinned beyond the identical-blocks unit test in
/// <c>MatchSearcherTests</c>) — its effect is that a run of duplicate blocks emits sequential
/// references (0,1,2,3) rather than repeating the same index.
/// </para>
/// <para>
/// A null head (<see cref="SumHeader.Count"/> zero, or <see cref="SumHeader.BlockLength"/> zero —
/// the latter covers the "existing zero-length basis" head <c>(0, 700, 2, 0)</c> from
/// <c>docs/transfer-spec.md</c> §4) skips the search entirely: the whole source becomes literal
/// chunks, since there is nothing to reference against an empty block list.
/// </para>
/// <para>
/// This method writes tokens only (<c>docs/codec-spec.md</c> §2/token stream) — the whole-file
/// checksum trailer that follows a file's token stream (<c>docs/transfer-spec.md</c> §3) is the
/// caller's responsibility, computed by hashing the same bytes (literals and matched regions) this
/// method reconstructs implicitly, in the same order they are emitted here.
/// </para>
/// </remarks>
public static class MatchSearcher
{
    /// <param name="output">Data-channel sink. The caller flushes; this method only calls <see cref="MultiplexWriter.Write"/>.</param>
    /// <param name="source">The sender's own file content, scanned start to end exactly once (aside from the O(blockLength) tail recompute described in the class remarks).</param>
    /// <param name="header">The generator's sum head — <see cref="SumHeader.BlockLength"/> sizes the scan window; <see cref="SumHeader.Remainder"/> sizes only the last block.</param>
    /// <param name="blockSums">One entry per signature block, ascending by index — must have exactly <see cref="SumHeader.Count"/> entries.</param>
    /// <param name="algorithm">Negotiated transfer checksum (<c>xfer_sum_nni</c>) — the same algorithm the signature's strong sums were computed with.</param>
    /// <param name="seed">Session <c>checksum_seed</c>, applied identically to how the signature was generated (<see cref="StrongChecksum.ComputeBlockSum"/> owns the seed-placement rules).</param>
    /// <param name="checksumSeedFix"><c>CF_CHKSUM_SEED_FIX</c> negotiated (MD5 seed placement) — must match what the signature side used.</param>
    /// <exception cref="ArgumentException"><paramref name="blockSums"/>.Count does not equal <paramref name="header"/>.Count.</exception>
    public static MatchResult Search(
        MultiplexWriter output,
        ReadOnlySpan<byte> source,
        SumHeader header,
        IReadOnlyList<BlockSignature> blockSums,
        ChecksumAlgorithm algorithm,
        int seed,
        bool checksumSeedFix,
        CompressionMethod compression = CompressionMethod.None)
    {
        ArgumentNullException.ThrowIfNull(blockSums);
        if (blockSums.Count != header.Count)
            throw new ArgumentException(
                $"match: sum head declares {header.Count} blocks but {blockSums.Count} entries were given",
                nameof(blockSums));

        bool zlibx = compression == CompressionMethod.Zlibx;

        if (header.Count == 0 || header.BlockLength == 0)
        {
            EmitLiteralRun(output, source, zlibx);
            WriteEnd(output, zlibx);
            return new MatchResult(source.Length, 0);
        }

        long literalBytes = 0;
        long matchedBytes = 0;
        Dictionary<uint, List<int>> byWeak = BuildWeakIndex(blockSums);

        int sourceLength = source.Length;
        int literalStart = 0;
        int previousMatch = -1;
        int previousZlibxBlock = 0; // running cursor for the relative TOKEN_REL delta (zlibx only)
        int pos = 0;
        uint weak = 0;
        int weakWindowLen = -1; // window length the current `weak` value was computed over
        Span<byte> strongDigest = stackalloc byte[16]; // widest strong digest (md4/md5/xxh128)

        while (pos < sourceLength)
        {
            int windowLen = Math.Min(header.BlockLength, sourceLength - pos);

            if (windowLen != weakWindowLen)
            {
                weak = RollingChecksum.Compute(source.Slice(pos, windowLen));
                weakWindowLen = windowLen;
            }

            int matchedBlock = -1;
            if (byWeak.TryGetValue(weak, out List<int>? candidates))
            {
                ReadOnlySpan<byte> window = source.Slice(pos, windowLen);
                int digestLength = StrongChecksum.ComputeBlockSum(
                    algorithm, seed, checksumSeedFix, window, strongDigest);
                ReadOnlySpan<byte> truncated = strongDigest[..Math.Min(header.StrongSumLength, digestLength)];

                matchedBlock = FindMatch(candidates, previousMatch, header, windowLen, blockSums, truncated);

                // zlibx TOKEN_REL carries an unsigned 6-bit forward delta; a match whose delta from
                // the last-referenced block does not fit is dropped back to literal bytes rather than
                // emitting the un-capture-pinned TOKEN_LONG form (docs/transfer-spec.md §2). Rare
                // (matches are near-sequential); costs a few extra literal bytes, never correctness.
                if (zlibx && matchedBlock >= 0)
                {
                    int delta = matchedBlock - previousZlibxBlock;
                    if (delta is < 0 or > 0x3F)
                        matchedBlock = -1;
                }
            }

            if (matchedBlock >= 0)
            {
                literalBytes += pos - literalStart;
                EmitLiteralRun(output, source[literalStart..pos], zlibx);
                if (zlibx)
                {
                    WriteZlibxTokenRel(output, matchedBlock - previousZlibxBlock);
                    previousZlibxBlock = matchedBlock;
                }
                else
                {
                    WriteToken(output, -(matchedBlock + 1));
                }
                matchedBytes += windowLen;

                pos += windowLen;
                literalStart = pos;
                previousMatch = matchedBlock;
                weakWindowLen = -1; // jumped — the rolling state is invalid, recompute fresh
                continue;
            }

            // No match here: slide one byte. Roll in O(1) when the window is still full-length and
            // a byte is available to bring in; otherwise (the shrinking tail) fall back to a fresh
            // recompute next iteration — bounded to at most one block length of extra work.
            if (windowLen == header.BlockLength && pos + windowLen < sourceLength)
            {
                weak = RollingChecksum.Roll(weak, source[pos], source[pos + windowLen], windowLen);
                // weakWindowLen stays == header.BlockLength: still valid for the next position.
            }
            else
            {
                weakWindowLen = -1;
            }
            pos++;
        }

        literalBytes += sourceLength - literalStart;
        EmitLiteralRun(output, source[literalStart..sourceLength], zlibx);
        WriteEnd(output, zlibx);
        return new MatchResult(literalBytes, matchedBytes);
    }

    private static Dictionary<uint, List<int>> BuildWeakIndex(IReadOnlyList<BlockSignature> blockSums)
    {
        var byWeak = new Dictionary<uint, List<int>>();
        for (int i = 0; i < blockSums.Count; i++)
        {
            if (!byWeak.TryGetValue(blockSums[i].WeakSum, out List<int>? list))
                byWeak[blockSums[i].WeakSum] = list = [];
            list.Add(i);
        }
        return byWeak;
    }

    /// <summary>
    /// Picks the accepted block among weak-sum candidates: the want_i adjacency preference (the
    /// block right after <paramref name="previousMatch"/>) wins if it also passes strong
    /// verification and has the matching length; otherwise the first ascending candidate that
    /// verifies. A candidate whose own expected length (<c>BlockLength</c>, below) does not equal
    /// the current scan window is never eligible — its stored checksum was computed over a
    /// different number of bytes.
    /// </summary>
    private static int FindMatch(
        List<int> candidates,
        int previousMatch,
        SumHeader header,
        int windowLen,
        IReadOnlyList<BlockSignature> blockSums,
        ReadOnlySpan<byte> truncatedStrongSum)
    {
        int preferred = previousMatch + 1;
        if (preferred < header.Count
            && BlockLength(header, preferred) == windowLen
            && candidates.Contains(preferred)
            && truncatedStrongSum.SequenceEqual(blockSums[preferred].StrongSum))
        {
            return preferred;
        }

        foreach (int candidate in candidates)
        {
            if (BlockLength(header, candidate) != windowLen)
                continue;
            if (truncatedStrongSum.SequenceEqual(blockSums[candidate].StrongSum))
                return candidate;
        }
        return -1;
    }

    /// <summary>The expected byte length of block <paramref name="index"/>: <see cref="SumHeader.Remainder"/> for the last block iff nonzero, else <see cref="SumHeader.BlockLength"/>.</summary>
    private static int BlockLength(SumHeader header, int index) =>
        index == header.Count - 1 && header.Remainder != 0 ? header.Remainder : header.BlockLength;

    /// <summary>
    /// Writes a pending literal run. Uncompressed: consecutive plain int32 tokens chunked at exactly
    /// <see cref="RsyncConstants.ChunkSize"/> (mirrors the stock sender). zlibx: the whole run is
    /// raw-deflated once (<see cref="ZlibxTokenCodec.DeflateRun"/>) and its payload chunked into
    /// DEFLATED_DATA tokens (<c>flag = 0x40 | (len&gt;&gt;8)</c>, then <c>len&amp;0xff</c>, then the bytes),
    /// each ≤ <see cref="ZlibxTokenCodec.MaxDeflatedChunk"/>. An empty run emits nothing either way.
    /// </summary>
    private static void EmitLiteralRun(MultiplexWriter output, ReadOnlySpan<byte> literal, bool zlibx)
    {
        if (!zlibx)
        {
            for (int offset = 0; offset < literal.Length; offset += RsyncConstants.ChunkSize)
            {
                int length = Math.Min(RsyncConstants.ChunkSize, literal.Length - offset);
                WriteToken(output, length);
                output.Write(literal.Slice(offset, length));
            }
            return;
        }

        if (literal.Length == 0)
            return;

        byte[] compressed = ZlibxTokenCodec.DeflateRun(literal);
        for (int offset = 0; offset < compressed.Length; offset += ZlibxTokenCodec.MaxDeflatedChunk)
        {
            int length = Math.Min(ZlibxTokenCodec.MaxDeflatedChunk, compressed.Length - offset);
            Span<byte> header = [(byte)(ZlibxTokenCodec.DeflatedData | (length >> 8)), (byte)(length & 0xFF)];
            output.Write(header);
            output.Write(compressed.AsSpan(offset, length));
        }
    }

    /// <summary>Ends a file's token stream: uncompressed writes the int32 <c>0</c>; zlibx writes the
    /// single END_FLAG byte <c>0x00</c>.</summary>
    private static void WriteEnd(MultiplexWriter output, bool zlibx)
    {
        if (zlibx)
        {
            Span<byte> end = [ZlibxTokenCodec.EndFlag];
            output.Write(end);
        }
        else
        {
            WriteToken(output, 0);
        }
    }

    /// <summary>Writes a single-block zlibx TOKEN_REL for a forward <paramref name="delta"/> in [0, 63]
    /// from the running block cursor (the caller guarantees the range and advances the cursor).</summary>
    private static void WriteZlibxTokenRel(MultiplexWriter output, int delta)
    {
        Span<byte> token = [(byte)(ZlibxTokenCodec.TokenRel | delta)];
        output.Write(token);
    }

    private static void WriteToken(MultiplexWriter output, int value)
    {
        Span<byte> wire = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(wire, value);
        output.Write(wire);
    }
}
