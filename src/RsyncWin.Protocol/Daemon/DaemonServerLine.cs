namespace RsyncWin.Protocol.Daemon;

/// <summary>The shape of one server-sent line during the daemon preamble (docs/daemon-spec.md §1).</summary>
public enum DaemonLineKind
{
    /// <summary><c>@RSYNCD: OK</c> — module accepted, preamble over, binary phase next.</summary>
    Ok,

    /// <summary><c>@RSYNCD: AUTHREQD &lt;challenge&gt;</c> — the module is auth-guarded.</summary>
    AuthRequired,

    /// <summary><c>@RSYNCD: EXIT</c> — terminates a module listing; connection closes next.</summary>
    Exit,

    /// <summary><c>@ERROR: &lt;text&gt;</c> — fatal; server closes, client exits 5.</summary>
    Error,

    /// <summary>Anything else: motd text, or a module-listing line.</summary>
    Text,
}

/// <summary>
/// One classified server line from the daemon preamble, with the payload (challenge, error message,
/// or raw text) extracted where the kind carries one.
/// </summary>
public readonly record struct DaemonServerLine(DaemonLineKind Kind, string? Text = null)
{
    private const string OkLine = "@RSYNCD: OK";
    private const string ExitLine = "@RSYNCD: EXIT";
    private const string AuthPrefix = "@RSYNCD: AUTHREQD ";
    private const string ErrorPrefix = "@ERROR: ";

    /// <summary>Classifies one preamble line (no trailing newline).</summary>
    public static DaemonServerLine Classify(string line) => line switch
    {
        OkLine => new DaemonServerLine(DaemonLineKind.Ok),
        ExitLine => new DaemonServerLine(DaemonLineKind.Exit),
        _ when line.StartsWith(AuthPrefix, StringComparison.Ordinal)
            => new DaemonServerLine(DaemonLineKind.AuthRequired, line[AuthPrefix.Length..]),
        _ when line.StartsWith(ErrorPrefix, StringComparison.Ordinal)
            => new DaemonServerLine(DaemonLineKind.Error, line[ErrorPrefix.Length..]),
        _ => new DaemonServerLine(DaemonLineKind.Text, line),
    };
}
