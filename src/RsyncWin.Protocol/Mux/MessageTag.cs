namespace RsyncWin.Protocol.Mux;

/// <summary>
/// Tag carried in the high byte of a multiplex frame header, offset by
/// <see cref="RsyncConstants.MplexBase"/>.
/// </summary>
/// <remarks>
/// Only <see cref="Data"/> carries protocol payload. Every other tag is out-of-band and must be
/// routed to a handler rather than fed to the protocol reader.
/// <para>
/// A zero-length <see cref="Data"/> frame is a KEEP-ALIVE, not end-of-stream. Treating it as EOF is
/// a classic hang. (Protocol 30 used the explicit <see cref="Noop"/> tag for this.)
/// </para>
/// <para>[VERIFY] Numeric values are pinned in P1 against a captured trace.</para>
/// </remarks>
public enum MessageTag
{
    /// <summary>Raw protocol payload. Zero-length means keep-alive.</summary>
    Data = 0,

    /// <summary>A transfer error the sending side already knows about.</summary>
    ErrorXfer = 1,

    /// <summary>Informational text for the user.</summary>
    Info = 2,

    /// <summary>Error text for the user.</summary>
    Error = 3,

    /// <summary>Warning text for the user.</summary>
    Warning = 4,

    /// <summary>Socket-level error text.</summary>
    ErrorSocket = 5,

    /// <summary>Text destined for the log.</summary>
    Log = 6,

    /// <summary>Text destined for the client.</summary>
    Client = 7,

    /// <summary>Error text that is known to be UTF-8.</summary>
    ErrorUtf8 = 8,

    /// <summary>Reprocess the indicated file-list index. Drives the generator's redo list.</summary>
    Redo = 9,

    /// <summary>Transfer statistics for the generator.</summary>
    Stats = 10,

    /// <summary>The sending side hit an I/O error while enumerating.</summary>
    IoError = 22,

    /// <summary>Peer is reporting an I/O timeout.</summary>
    IoTimeout = 33,

    /// <summary>Do-nothing keep-alive (protocol 30 only; later versions use a 0-length <see cref="Data"/>).</summary>
    Noop = 42,

    /// <summary>Synchronize an error exit. Takes precedence over the transport's own exit code.</summary>
    ErrorExit = 86,

    /// <summary>Receiver reports a file transferred successfully.</summary>
    Success = 100,

    /// <summary>Receiver reports a file deleted.</summary>
    Deleted = 101,

    /// <summary>Sender could not open a file we asked for.</summary>
    NoSend = 102,
}
