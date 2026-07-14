using System.ComponentModel;
using System.Net.Sockets;
using RsyncWin.Protocol.Session;

namespace RsyncWin.Cli;

/// <summary>
/// Central exception-to-<see cref="RsyncExitCode"/> mapping shared by every Run* method in
/// Program.cs, so the ssh-255-takes-precedence rule (docs/wire-notes.md open questions) is applied
/// exactly once instead of being re-derived per catch block.
/// </summary>
internal static class ExitCodeMapper
{
    /// <summary>
    /// Maps a caught exception to rsync's exit status.
    /// </summary>
    /// <param name="exception">
    /// One of <see cref="ProtocolException"/>, <see cref="InvalidDataException"/>,
    /// <see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>,
    /// <see cref="SocketException"/>, or <see cref="Win32Exception"/> — the set every Run* catch
    /// filter actually narrows to.
    /// </param>
    /// <param name="sshExitCode">
    /// The ssh.exe subprocess exit code, when known/applicable (null for daemon transport, which has
    /// no subprocess). 255 means ssh itself failed (auth/host-key/DNS) and takes precedence over a
    /// <see cref="ProtocolException"/> or <see cref="InvalidDataException"/> that resulted from the
    /// wire stream simply going dead underneath us — that is a symptom, not the root cause. It never
    /// overrides a filesystem or socket failure, which has nothing to do with ssh.
    /// </param>
    public static RsyncExitCode Map(Exception exception, int? sshExitCode = null) => exception switch
    {
        ProtocolException ex => sshExitCode == 255 ? RsyncExitCode.StartClientServerError : ex.ExitCode,

        // Codec/choreography desync — normally exit 12, BUT if ssh itself already died (255), that is
        // the real cause. Fixes the open bug where an unreachable host surfaced as 12 instead of 5.
        InvalidDataException => sshExitCode == 255 ? RsyncExitCode.StartClientServerError : RsyncExitCode.ProtocolStreamError,

        IOException or UnauthorizedAccessException => RsyncExitCode.FileIoError,
        SocketException => RsyncExitCode.SocketIoError,
        Win32Exception => RsyncExitCode.StartClientServerError,

        _ => throw new ArgumentException($"ExitCodeMapper: unmapped exception type {exception.GetType()}", nameof(exception)),
    };
}
