using System.IO.Pipelines;

namespace RsyncWin.Transport;

/// <summary>
/// A bidirectional byte pipe to a remote rsync peer.
/// </summary>
/// <remarks>
/// <para>
/// This is the single seam that keeps the protocol core transport-agnostic. Two implementations are
/// planned: <c>OpenSshProcessTransport</c> (spawns <c>ssh host rsync --server ...</c>) and
/// <c>DaemonTcpTransport</c> (TCP/873, <c>@RSYNCD</c> greeting). Both hand back the same raw duplex
/// pipe; everything above this interface is identical.
/// </para>
/// <para>
/// Implementations MUST feed <see cref="Output"/> and drain both <see cref="Input"/> and
/// <see cref="StandardError"/> concurrently. A process transport that only pumps stdin/stdout will
/// deadlock the moment the peer emits a large stderr burst and fills the pipe buffer.
/// </para>
/// <para>
/// The protocol stream is binary. A process transport must use the raw
/// <c>Process.StandardInput.BaseStream</c> / <c>StandardOutput.BaseStream</c> — never the
/// <c>StreamReader</c>/<c>StreamWriter</c> text wrappers, whose encoding and newline translation
/// silently corrupt the byte stream.
/// </para>
/// </remarks>
public interface IRsyncTransport : IAsyncDisposable
{
    /// <summary>Bytes arriving from the remote peer.</summary>
    PipeReader Input { get; }

    /// <summary>Bytes destined for the remote peer.</summary>
    PipeWriter Output { get; }

    /// <summary>
    /// Out-of-band diagnostic text from the transport itself, not from the rsync protocol.
    /// For SSH this is where authentication and host-key failures appear.
    /// </summary>
    PipeReader StandardError { get; }

    /// <summary>
    /// Waits for the remote side to terminate and yields its raw exit code.
    /// </summary>
    /// <remarks>
    /// This is the <em>transport's</em> exit code, not rsync's. A protocol-level
    /// <c>MSG_ERROR_EXIT</c> takes precedence over whatever this returns. An <c>ssh.exe</c> exit of
    /// 255 means SSH itself failed (auth, host key, DNS) and should be reported as
    /// <c>RsyncExitCode.StartClientServerError</c> along with <see cref="StandardError"/>.
    /// </remarks>
    ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default);
}
