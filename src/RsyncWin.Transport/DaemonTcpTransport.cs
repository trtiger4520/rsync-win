using System.IO.Pipelines;
using System.Net.Sockets;

namespace RsyncWin.Transport;

/// <summary>
/// <see cref="IRsyncTransport"/> over a plain TCP connection to an rsync daemon (<c>rsync://</c>,
/// default port 873).
/// </summary>
/// <remarks>
/// <para>
/// Unlike the SSH transport there is no process and no separate stderr channel — daemon-mode error
/// reporting rides inside the protocol stream itself (an <c>@ERROR</c> line during the greeting, or
/// <c>MSG_ERROR_EXIT</c> once multiplexing starts). <see cref="StandardError"/> exists only to satisfy
/// the shared seam and always reports EOF immediately.
/// </para>
/// <para>
/// The protocol stream is binary, so <see cref="Input"/>/<see cref="Output"/> wrap the raw
/// <see cref="NetworkStream"/> directly via <see cref="PipeReader.Create(System.IO.Stream, StreamPipeReaderOptions)"/>
/// and <see cref="PipeWriter.Create(System.IO.Stream, StreamPipeWriterOptions)"/> — no text encoding
/// involved, so no corruption risk there, but the same raw-stream discipline as the SSH transport is
/// kept for consistency.
/// </para>
/// </remarks>
public sealed class DaemonTcpTransport : IRsyncTransport
{
    /// <summary>The rsync daemon's well-known port.</summary>
    public const int DefaultPort = 873;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private bool _disposed;

    public PipeReader Input { get; }
    public PipeWriter Output { get; }

    /// <summary>
    /// A TCP socket has no stderr channel; this always reports EOF with zero buffered bytes.
    /// Daemon-mode diagnostics arrive as <c>@ERROR</c> lines or <c>MSG_ERROR_EXIT</c> inside the
    /// protocol stream itself, not here.
    /// </summary>
    public PipeReader StandardError { get; }

    /// <summary>Opens a TCP connection to <paramref name="host"/>:<paramref name="port"/>.</summary>
    /// <exception cref="SocketException">The connection could not be established.</exception>
    /// <exception cref="OperationCanceledException">The connect attempt exceeded its timeout.</exception>
    public static async Task<DaemonTcpTransport> ConnectAsync(
        string host, int port = DefaultPort, CancellationToken cancellationToken = default)
    {
        var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        try
        {
            await client.ConnectAsync(host, port, timeout.Token);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return new DaemonTcpTransport(client);
    }

    private DaemonTcpTransport(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true; // the protocol is chatty with small frames
        _stream = _client.GetStream();
        Input = PipeReader.Create(_stream);
        Output = PipeWriter.Create(_stream);
        StandardError = PipeReader.Create(Stream.Null);
    }

    /// <summary>
    /// Transport exit is meaningless for TCP: there is no remote process and no exit code, so this
    /// simply returns 0 once the connection has been closed/disposed. The real transfer status comes
    /// from the protocol stream (<c>MSG_ERROR_EXIT</c> or an <c>@ERROR</c> line), never from here.
    /// </summary>
    public ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(0);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        await Output.CompleteAsync();
        await Input.CompleteAsync();
        _stream.Dispose();
        _client.Dispose();
    }
}
