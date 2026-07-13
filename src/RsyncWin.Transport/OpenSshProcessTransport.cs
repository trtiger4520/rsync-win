using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;

namespace RsyncWin.Transport;

/// <summary>
/// <see cref="IRsyncTransport"/> over a spawned <c>ssh.exe</c> (or any process speaking the protocol
/// on stdin/stdout — the capture harness and tests exploit that).
/// </summary>
/// <remarks>
/// <para>
/// The three streams are pumped on three independent paths: the caller's own reads drain stdout,
/// the caller's own writes feed stdin, and a dedicated background task drains stderr into an
/// in-memory pipe. That third loop is not optional — a large stderr burst with no reader fills the
/// OS pipe buffer and deadlocks both sides.
/// </para>
/// <para>
/// Only the raw <c>BaseStream</c>s are touched. The <c>StreamReader</c>/<c>StreamWriter</c> text
/// wrappers corrupt the binary protocol with encoding and newline translation.
/// </para>
/// <para>
/// Every wait inside <see cref="DisposeAsync"/> is bounded. Dispose is the last line of defense
/// against protocol hangs, so it must never itself hang — not on a child that stopped draining
/// stdin (the flush is abandoned and the tree killed), and not on a lingering descendant that
/// inherited the stderr handle (the pump wait is capped; pipe EOF needs every writer closed).
/// </para>
/// </remarks>
public sealed class OpenSshProcessTransport : IRsyncTransport
{
    private static readonly TimeSpan PoliteExitGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StderrGrace = TimeSpan.FromSeconds(2);

    /// <summary>The platform OpenSSH client: the in-box binary on Windows, or <c>ssh</c> from PATH elsewhere.</summary>
    public static string DefaultSshExePath =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe")
            : "ssh";

    private readonly Process _process;
    // pauseWriterThreshold 0 = never apply backpressure: the drain loop must never block, or the
    // deadlock it exists to prevent comes back. stderr is human-scale diagnostics, not bulk data.
    private readonly Pipe _stderr = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
    private readonly Task _stderrPump;
    private bool _disposed;

    public PipeReader Input { get; }
    public PipeWriter Output { get; }
    public PipeReader StandardError => _stderr.Reader;

    /// <summary>Spawns <paramref name="executable"/> and wires the protocol to its stdin/stdout.</summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The executable could not be started.</exception>
    public static OpenSshProcessTransport Start(string executable, IEnumerable<string> arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"failed to start {executable}");
        return new OpenSshProcessTransport(process);
    }

    private OpenSshProcessTransport(Process process)
    {
        _process = process;
        Input = PipeReader.Create(process.StandardOutput.BaseStream);
        Output = PipeWriter.Create(process.StandardInput.BaseStream);
        _stderrPump = PumpStandardErrorAsync();
    }

    private async Task PumpStandardErrorAsync()
    {
        try
        {
            await _process.StandardError.BaseStream.CopyToAsync(_stderr.Writer.AsStream());
        }
        catch (IOException)
        {
            // A broken stderr pipe just means no more diagnostics arrive; not an error worth
            // propagating into the reader — this stream exists purely for error reporting.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            await _stderr.Writer.CompleteAsync();
        }
    }

    /// <summary>Returns everything the process has written to stderr <em>so far</em>.</summary>
    /// <remarks>
    /// Best-effort by design: waits briefly for stderr EOF (i.e. the process closing it), then
    /// drains whatever is buffered. Callers use this to enrich error reports while ssh may still
    /// be alive — it must never hang on a live process.
    /// </remarks>
    public async ValueTask<string> ReadAllStandardErrorAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _stderrPump.WaitAsync(StderrGrace, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Process still running; report what we have.
        }

        if (!_stderr.Reader.TryRead(out ReadResult result))
            return "";
        string text = Encoding.UTF8.GetString(result.Buffer.ToArray());
        _stderr.Reader.AdvanceTo(result.Buffer.End);
        return text;
    }

    public async ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        await _process.WaitForExitAsync(cancellationToken);
        return _process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        // Polite path: close stdin (EOF is the shutdown signal) and give the process a bounded
        // grace to exit. CompleteAsync flushes any unflushed bytes with no cancellation support,
        // and a child that stopped draining stdin would block that flush forever — so the whole
        // polite phase is abandoned on timeout and the tree is killed.
        try
        {
            await Output.CompleteAsync().AsTask().WaitAsync(PoliteExitGrace);
            using var grace = new CancellationTokenSource(PoliteExitGrace);
            await _process.WaitForExitAsync(grace.Token);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or OperationCanceledException)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
                using var reap = new CancellationTokenSource(PoliteExitGrace);
                await _process.WaitForExitAsync(reap.Token);
            }
            catch (Exception killFailure) when (
                killFailure is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                // Exited between the timeout and the kill, or is beyond killing; nothing left to do.
            }
        }

        await Input.CompleteAsync();

        // Bounded: pipe EOF requires every writer handle closed, and a descendant of ssh (e.g. a
        // ProxyCommand child) may have inherited stderr and outlived it.
        try
        {
            await _stderrPump.WaitAsync(StderrGrace);
        }
        catch (TimeoutException)
        {
        }

        _process.Dispose();
    }
}
