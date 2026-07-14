namespace RsyncWin.Protocol;

/// <summary>
/// Wire-level constants for the rsync protocol.
/// </summary>
/// <remarks>
/// <para>
/// PROVENANCE. Canonical rsync is GPLv3 and is read here for <em>behavior only</em>. These are
/// numeric protocol facts (a value a peer will send us), not borrowed expression. Anything larger
/// than a scalar — notably the <c>int_byte_extra</c> table used by the varint codec — must be taken
/// from a permissively licensed reference (openrsync, ISC/BSD; gokrazy/rsync, BSD-3) or regenerated
/// from the documented algorithm. See <c>docs/wire-notes.md</c>.
/// </para>
/// <para>
/// VERIFICATION STATUS. Values marked <c>[VERIFY]</c> have not yet been checked against a
/// permissive reference implementation or a captured wire trace. Phase P1 is a hard gate that
/// pins every one of these against bytes captured from a real rsync
/// (<c>--debug=deltasum2</c>, <c>--debug=deltasum4</c>, <c>--debug=proto</c>). Do not let a codec
/// depend on a <c>[VERIFY]</c> value until that gate is green — a single wrong constant produces a
/// silent full-resend or an exit-12 desync rather than a clean failure.
/// </para>
/// </remarks>
public static class RsyncConstants
{
    // ---- Protocol version negotiation -------------------------------------------------
    // Each side writes its max version as a little-endian int; the session uses min(local, remote).
    //
    // These three are NOT the same thing, and conflating them is a real bug:
    //   ProtocolVersion    = what WE implement and advertise.
    //   MinProtocolVersion = the floor we are willing to negotiate down to.
    //   MaxProtocolVersion = a sanity CEILING on what a peer may claim. It is deliberately far
    //                        above ProtocolVersion, because peers are newer than us and that is
    //                        fine -- min(local, remote) still lands on a version we implement.
    //
    // Measured: rsync 3.4.3 (alpine:3.21) advertises PROTOCOL VERSION 32. A ceiling of 31 here
    // would reject a stock modern rsync outright.

    /// <summary>Highest protocol version we implement and advertise.</summary>
    public const int ProtocolVersion = 31;

    /// <summary>Lowest protocol version we are willing to negotiate down to.</summary>
    public const int MinProtocolVersion = 29;

    /// <summary>
    /// Sanity ceiling on the version a peer may claim. Matches upstream rsync's
    /// <c>MAX_PROTOCOL_VERSION</c>. A peer claiming more than this is treated as garbage on the wire,
    /// not as a newer rsync.
    /// </summary>
    public const int MaxProtocolVersion = 40;

    // ---- Multiplexed I/O ---------------------------------------------------------------
    // Frame header is a 4-byte little-endian word: low 24 bits = payload length,
    // high byte = MplexBase + MessageTag. See MessageTag and Mux/.
    //
    // NOTE: multiplexing is turned on PER DIRECTION and independently
    // (io_start_multiplex_in vs io_start_multiplex_out). It is NOT active during the
    // handshake prologue. Exact per-direction/per-version timing is an open question to be
    // pinned by live capture in P2, before any transfer code is written.

    /// <summary>Value added to a <see cref="MessageTag"/> to form the header's high byte.</summary>
    public const int MplexBase = 7;

    /// <summary>Maximum payload of a single multiplex frame (the length field is 24 bits).</summary>
    public const int MaxMuxPayload = (1 << 24) - 1;

    // ---- File-list index sentinels -----------------------------------------------------
    // These travel through write_ndx/read_ndx, which is its OWN delta-from-previous byte-reduction
    // encoding -- NOT write_int, NOT varint. See Wire/NdxCodec.

    /// <summary>End-of-phase marker.</summary>
    public const int NdxDone = -1;

    /// <summary>End of an incremental file list (protocol 30+, incremental recursion only).</summary>
    public const int NdxFlistEof = -2;

    /// <summary>Deletion statistics follow.</summary>
    public const int NdxDelStats = -3;

    /// <summary>Base offset applied to incremental file-list indices.</summary>
    public const int NdxFlistOffset = -101;

    // ---- Block sizing and checksums ----------------------------------------------------
    //
    // Block length comes from sum_sizes_sqroot(). The resulting blength is rounded to a
    // MULTIPLE OF 8 -- it is *not* rounded to a power of two, a widespread misconception.
    // The strong checksum stored per block is TRUNCATED to s2length bytes:
    //     s2length = (b + 1 - 32 + 7) / 8     (where b relates to the block count)
    // clamped into [ShortSumLength, SumLength]. The whole-file checksum uses the FULL length
    // and may use a different negotiated algorithm than the per-block one.

    /// <summary>Minimum block length (the 700 in <c>max(700, sqrt(file_size))</c>).</summary>
    public const int BlockSize = 700;

    /// <summary>Upper bound on a negotiated block length.</summary>
    public const int MaxBlockSize = 1 << 17; // 131072

    /// <summary>Full length in bytes of a strong checksum (MD4/MD5 digest size).</summary>
    public const int SumLength = 16;

    /// <summary>Floor for the truncated per-block strong checksum length.</summary>
    public const int ShortSumLength = 2;

    /// <summary>Bias term in the s2length derivation.</summary>
    public const int BlockSumBias = 10;

    /// <summary>Offset added to each byte by the weak rolling checksum. Zero in all modern builds.</summary>
    public const int CharOffset = 0;

    /// <summary>Chunk size the strong checksum is fed in.</summary>
    public const int CsumChunk = 64;

    /// <summary>Read granularity for literal data on the receive side.</summary>
    public const int ChunkSize = 32 * 1024;

    // ---- Compat flags (protocol 30+) ---------------------------------------------------
    // Exchanged as a varint immediately after version negotiation and BEFORE the checksum seed.
    // [VERIFY] bit positions against openrsync/gokrazy before the Session codec depends on them.

    /// <summary>Server negotiated incremental recursion. We force <c>--no-inc-recursive</c> and assert this is clear.</summary>
    public const int CompatIncRecurse = 1 << 0;

    /// <summary>[VERIFY] Symlink mtimes are transferred.</summary>
    public const int CompatSymlinkTimes = 1 << 1;

    /// <summary>[VERIFY] Symlink targets are subject to iconv conversion.</summary>
    public const int CompatSymlinkIconv = 1 << 2;

    /// <summary>[VERIFY] File list is sent in a "safe" (sanitized) form.</summary>
    public const int CompatSafeFlist = 1 << 3;

    /// <summary>[VERIFY] Disable an xattr optimization that is unsafe across versions.</summary>
    public const int CompatAvoidXattrOptim = 1 << 4;

    /// <summary>
    /// Peer applies the corrected checksum-seed ordering. Controls whether the seed is
    /// prepended or appended when mixing into an MD5 strong checksum.
    /// </summary>
    public const int CompatChecksumSeedFix = 1 << 5;

    /// <summary>[VERIFY] In-place transfers honor a partial directory.</summary>
    public const int CompatInplacePartialDir = 1 << 6;

    /// <summary>File-list entry flags are encoded as a varint rather than a byte/short.</summary>
    public const int CompatVarintFlistFlags = 1 << 7;

    // ---- Exit codes --------------------------------------------------------------------
    // Mirrors rsync's numeric exit statuses. A receiver on Windows will realistically surface
    // 11 (file I/O) far more often than 12; do not collapse filesystem failures into 12.
    // See Session/RsyncExitCode for the typed enum.
}
