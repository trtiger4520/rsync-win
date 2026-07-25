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
/// must inflate the run stream CONTINUOUSLY (<see cref="RunInflater"/>), never run-by-run — a per-run
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
    /// The receiver's continuous raw-inflater for one file's literal-run stream: DEFLATED_DATA
    /// payloads are <see cref="Feed"/>-ed as they come off the wire and inflated bytes are pulled out
    /// with <see cref="Read"/>, so neither the compressed runs nor the decompressed literals are ever
    /// fully buffered. One instance per file — the deflate window must persist across runs (see the
    /// class remarks).
    /// <para>
    /// <b>The run boundary is a drain, not a length.</b> A run's decompressed length is not on the
    /// wire and cannot be derived, so the only way to know a run ended is to feed its
    /// <c>00 00 ff ff</c> sync marker (<see cref="EndRun"/>) and inflate until the inflater asks for
    /// input it does not have — signalled by <see cref="Read"/> returning 0.
    /// </para>
    /// <para>
    /// <b>Load-bearing runtime behavior:</b> that a <see cref="DeflateStream"/> whose source returns 0
    /// mid-stream returns 0 and stays RESUMABLE (window intact) when more input arrives afterwards is
    /// observed BCL behavior, not a documented contract — <c>ZlibxCodecTests.RunInflater_*</c> pins it
    /// so a runtime upgrade that changed it (e.g. to throw on truncated input) fails loudly instead of
    /// silently mis-decoding. Fallback if that ever happens: a fresh inflater per run, prefixed with a
    /// hand-built stored block carrying the previous 32 KiB of output as the window (measured working,
    /// ~2x slower, depends only on the DEFLATE format).
    /// </para>
    /// </summary>
    public sealed class RunInflater : IDisposable
    {
        private readonly FeedStream _feed = new();
        private readonly DeflateStream _inflater;

        public RunInflater() => _inflater = new DeflateStream(_feed, CompressionMode.Decompress);

        /// <summary>
        /// Queues one DEFLATED_DATA payload as inflater input. The array is enqueued as-is, NOT
        /// copied — ownership passes to the inflater until it has been drained, so the caller must
        /// not reuse or mutate it. That is free for the receiver, which hands over the fresh array
        /// <c>ReadDataExactlyAsync</c> just allocated, and avoids a copy per chunk on the hot path.
        /// </summary>
        public void Feed(byte[] compressed) => _feed.Feed(compressed);

        /// <summary>Queues a payload the caller only has as a slice — copied, unlike the array
        /// overload.</summary>
        public void Feed(ReadOnlySpan<byte> compressed) => _feed.Feed(compressed.ToArray());

        /// <summary>Closes the current literal run by queueing the sync marker the wire stripped.
        /// Only call this for a run that actually had payload: two adjacent match tokens carry no run,
        /// and injecting a marker the sender never emitted would insert a block into the stream.</summary>
        public void EndRun() => _feed.Feed(SyncFlushTail);

        /// <summary>
        /// Inflates as much as the queued input allows into <paramref name="destination"/>, returning
        /// 0 once the inflater needs input that has not been fed. Synchronous by design: the source is
        /// an in-memory queue, never the wire, so this performs no I/O and is safe to call from the
        /// async receive loop.
        /// </summary>
        public int Read(Span<byte> destination) => _inflater.Read(destination);

        public void Dispose() => _inflater.Dispose();

        /// <summary>Hands the inflater exactly what has been fed so far and returns 0 when empty —
        /// which is what makes "the inflater wants more input" observable as <c>Read</c> returning 0.</summary>
        private sealed class FeedStream : Stream
        {
            private readonly Queue<byte[]> _chunks = new();
            private int _offset;

            /// <summary>Takes ownership of <paramref name="data"/> — never copied, never mutated.</summary>
            public void Feed(byte[] data)
            {
                if (data.Length != 0)
                    _chunks.Enqueue(data);
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                if (_chunks.Count == 0 || buffer.Length == 0)
                    return 0;

                byte[] head = _chunks.Peek();
                int length = Math.Min(buffer.Length, head.Length - _offset);
                head.AsSpan(_offset, length).CopyTo(buffer);
                _offset += length;
                if (_offset == head.Length)
                {
                    _chunks.Dequeue();
                    _offset = 0;
                }
                return length;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
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
