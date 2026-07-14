using System.IO.Compression;

namespace RsyncWin.Protocol.Delta;

// PROVENANCE: rsync token.c (GPLv3) read for BEHAVIOR ONLY — the compressed-token flag grammar
// (END_FLAG / DEFLATED_DATA / TOKEN_REL / TOKENRUN_REL / *_LONG), the DEFLATED_DATA length encoding,
// and the "zlibx excludes matched blocks from the deflate window" rule. Byte layout capture-pinned
// against test-fixtures/vectors/ssh31-pull-z-{zlibx,delta} (docs/transfer-spec.md §2). No expression
// was copied — deflate/inflate come from the BCL, and the framing below is written from the
// documented/measured behavior.

/// <summary>
/// The zlibx (<c>-z</c>) token-stream flag grammar and the raw-deflate primitives it rides on.
/// <para>
/// zlibx is rsync's "new" compression: raw deflate (no zlib header, windowBits −15), where matched
/// blocks are NOT inserted into the deflate window. That single fact is what makes a BCL-only
/// implementation possible — the receiver copies matched blocks straight from the basis and feeds
/// only the literal (DEFLATED_DATA) payloads through <see cref="DeflateStream"/>, never needing a
/// "insert into the window without emitting output" primitive (which the BCL lacks).
/// </para>
/// <para>
/// One literal run (the DEFLATED_DATA tokens between two match tokens, or before END) is a sequence
/// of raw-deflate blocks terminated by a <c>Z_SYNC_FLUSH</c> whose trailing <c>00 00 ff ff</c> marker
/// is stripped on the wire. To decompress we re-append that marker; to compress we
/// <see cref="DeflateStream.Flush"/> (which emits exactly that marker, verified) and strip it.
/// </para>
/// <para>
/// The deflate WINDOW persists across runs — rsync's sender keeps one deflate stream for the whole
/// file and only excludes MATCHED blocks from the window, so a later literal run can back-reference an
/// earlier one (verified against real rsync, <c>ssh31-pull-z-crossrun</c>). Therefore the receiver
/// must inflate the run stream CONTINUOUSLY (<see cref="InflateRuns"/>), never run-by-run — a per-run
/// inflate throws on a cross-run reference. The encoder side compresses each run independently (so it
/// never emits a cross-run reference of its own), which any receiver decodes fine; the asymmetry is
/// safe because a continuous inflater handles both continuous and independent segments.
/// </para>
/// </summary>
public static class ZlibxTokenCodec
{
    public const byte EndFlag = 0x00;
    public const byte DeflatedData = 0x40; // (flag & 0xC0) == 0x40; low 6 bits are len high byte
    public const byte TokenRel = 0x80;     // (flag & 0xC0) == 0x80; low 6 bits are the relative block delta
    public const byte TokenRunRel = 0xC0;  // (flag & 0xC0) == 0xC0; + a 2-byte run count follows
    public const byte TokenLong = 0x20;    // absolute 4-byte block number follows
    public const byte TokenRunLong = 0x21; // absolute 4-byte block + 2-byte run count

    /// <summary>Max DEFLATED_DATA payload length: the length is <c>((flag &amp; 0x3f) &lt;&lt; 8) | next</c>, so 14 bits.</summary>
    public const int MaxDeflatedChunk = 0x3FFF; // 16383

    /// <summary>The stripped Z_SYNC_FLUSH tail that terminates every flush region on the wire.</summary>
    private static readonly byte[] SyncFlushTail = [0x00, 0x00, 0xFF, 0xFF];

    /// <summary>
    /// Inflates one literal run: the concatenated DEFLATED_DATA payloads, whose stripped
    /// <c>00 00 ff ff</c> sync-flush tail we re-append so the raw-deflate stream is flushable in full.
    /// Only correct for a STANDALONE run (no cross-run back-reference) — use <see cref="InflateRuns"/>
    /// for a real transfer, where rsync's continuous deflate window means a later run may reference an
    /// earlier one.
    /// </summary>
    public static byte[] InflateRun(ReadOnlySpan<byte> compressedRun) => InflateStream(compressedRun);

    /// <summary>
    /// Inflates the first <paramref name="count"/> literal runs as ONE continuous raw-deflate stream —
    /// each run's payload followed by the re-appended <c>00 00 ff ff</c> sync-flush marker. This is
    /// load-bearing: rsync's zlibx sender keeps the deflate window across runs (only MATCHED blocks
    /// are excluded from the window), so a later literal run can back-reference an earlier one. A
    /// per-run inflate throws `InvalidDataException` on such a reference (verified against real rsync,
    /// `ssh31-pull-z-crossrun`) — runs MUST be inflated continuously. Returns all decompressed literal
    /// bytes of runs <c>[0, count)</c> concatenated.
    /// </summary>
    public static byte[] InflateRuns(IReadOnlyList<byte[]> runs, int count)
    {
        using var input = new MemoryStream();
        for (int i = 0; i < count; i++)
        {
            input.Write(runs[i]);
            input.Write(SyncFlushTail);
        }
        input.Position = 0;
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    private static byte[] InflateStream(ReadOnlySpan<byte> compressed)
    {
        byte[] buffer = new byte[compressed.Length + SyncFlushTail.Length];
        compressed.CopyTo(buffer);
        SyncFlushTail.CopyTo(buffer.AsSpan(compressed.Length));

        using var source = new MemoryStream(buffer, writable: false);
        using var deflate = new DeflateStream(source, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>
    /// Compresses one literal run into its DEFLATED_DATA payload: raw-deflate the bytes, take the
    /// <see cref="DeflateStream.Flush"/> output, and strip the trailing <c>00 00 ff ff</c> sync-flush
    /// marker (re-added by the peer on inflate). Empty input yields an empty payload. Each run is
    /// compressed independently so it never back-references a prior run — safe for any receiver, and
    /// symmetric with <see cref="InflateRun"/>.
    /// </summary>
    public static byte[] DeflateRun(ReadOnlySpan<byte> literals)
    {
        if (literals.Length == 0)
            return [];

        using var compressed = new MemoryStream();
        var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true);
        deflate.Write(literals);
        deflate.Flush(); // emits the raw-deflate bytes ending in the 00 00 ff ff sync marker
        int flushedLength = (int)compressed.Length; // ends exactly in 00 00 ff ff (verified)
        // Dispose finalizes the stream (a BFINAL empty block) AFTER flushedLength — we slice it off
        // below by only ever reading the first flushedLength bytes. Disposing releases the native
        // deflater; leaveOpen keeps the MemoryStream so we can read the bytes back.
        deflate.Dispose();

        // The DEFLATED_DATA payload is the flushed region minus its trailing sync-flush marker
        // (the peer re-appends it on inflate).
        return compressed.ToArray()[..(flushedLength - SyncFlushTail.Length)];
    }
}
