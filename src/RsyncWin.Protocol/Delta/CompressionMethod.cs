namespace RsyncWin.Protocol.Delta;

/// <summary>
/// The negotiated per-file token-stream compression (<c>-z</c>). Only <see cref="Zlibx"/> is
/// implementable on the BCL: it is raw deflate (<c>DeflateStream</c>) and — unlike the older
/// <c>zlib</c> mode — never inserts matched-block bytes into the deflate window, so the receiver can
/// copy matched blocks from the basis and feed only the literal (DEFLATED_DATA) payloads to the
/// inflater (docs/transfer-spec.md §2). Stock rsync's default <c>-z</c> negotiates <c>zstd</c>, and
/// <c>zlib</c> needs a window-insertion primitive the BCL lacks — so we force <c>zlibx</c> via the
/// <c>--new-compress</c> server option and never negotiate a compression string.
/// </summary>
public enum CompressionMethod
{
    /// <summary>No compression: the plain 4-byte-int token stream (<see cref="Token"/>).</summary>
    None = 0,

    /// <summary>rsync's "new" zlib mode (raw deflate, matched blocks excluded from the window).</summary>
    Zlibx = 1,
}
