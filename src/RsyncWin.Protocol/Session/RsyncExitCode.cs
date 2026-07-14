namespace RsyncWin.Protocol.Session;

/// <summary>
/// rsync's numeric exit statuses. The CLI returns these verbatim so scripts that wrap rsync keep working.
/// </summary>
/// <remarks>
/// Mapping guidance for a Windows receiver:
/// <list type="bullet">
/// <item>Filesystem failures (path too long, access denied, reserved name) are
/// <see cref="FileIoError"/> (11) — <em>not</em> <see cref="ProtocolStreamError"/> (12).</item>
/// <item>A source file that disappeared mid-transfer is <see cref="PartialTransferVanished"/> (24),
/// which is a success-ish outcome, not a hard failure.</item>
/// <item><c>ssh.exe</c> failing to connect exits 255; map that to <see cref="StartClientServerError"/> (5)
/// and surface its stderr — auth and host-key failures land there, never in the protocol stream.</item>
/// <item>A protocol <c>MSG_ERROR_EXIT</c> from the peer takes precedence over the transport's exit code.</item>
/// </list>
/// </remarks>
public enum RsyncExitCode
{
    /// <summary>Success.</summary>
    Ok = 0,

    /// <summary>Syntax or usage error.</summary>
    SyntaxError = 1,

    /// <summary>Protocol incompatibility — no common version could be negotiated.</summary>
    ProtocolIncompatibility = 2,

    /// <summary>Errors selecting input/output files or directories.</summary>
    FileSelectionError = 3,

    /// <summary>Requested action not supported by this build or by the peer.</summary>
    UnsupportedAction = 4,

    /// <summary>Error starting the client-server protocol. Includes an <c>ssh.exe</c> launch/auth failure.</summary>
    StartClientServerError = 5,

    /// <summary>Error in socket I/O.</summary>
    SocketIoError = 10,

    /// <summary>Error in file I/O. The expected code for Windows path/permission failures on the receiver.</summary>
    FileIoError = 11,

    /// <summary>Error in the rsync protocol data stream — i.e. we desynced.</summary>
    ProtocolStreamError = 12,

    /// <summary>Errors with program diagnostics.</summary>
    DiagnosticsError = 13,

    /// <summary>Partial transfer due to error.</summary>
    PartialTransferError = 23,

    /// <summary>Partial transfer because source files vanished during the run.</summary>
    PartialTransferVanished = 24,

    /// <summary>Timeout in data send/receive. Usually means keep-alives were not emitted.</summary>
    Timeout = 30,

    /// <summary>Timeout waiting for a daemon connection.</summary>
    DaemonConnectionTimeout = 35,
}
