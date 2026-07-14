using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using RsyncWin.Protocol;
using RsyncWin.Protocol.Daemon;
using RsyncWin.Protocol.Session;
using RsyncWin.Transport;

namespace RsyncWin.Engine;

/// <summary>Result of a successful single-module daemon preamble (<see cref="DaemonPreamble.RunAsync"/>).</summary>
/// <param name="Protocol">The negotiated protocol version — <c>min(ours, the server greeting's)</c>.
/// Feed this into <see cref="HandshakeOptions.PreNegotiatedProtocolVersion"/> so the role session that
/// runs next skips the binary version-int exchange (already settled by the textual greeting).</param>
/// <param name="MotdLines">Raw text lines the server sent before the terminal verdict line (motd).</param>
/// <param name="Success">Always <c>true</c> when this result is returned — a failed preamble
/// (<c>@ERROR</c>, missing password, unsupported auth digest) throws instead of returning one.
/// Carried anyway per the caller-facing contract this type is documented against.</param>
public sealed record DaemonPreambleResult(int Protocol, IReadOnlyList<string> MotdLines, bool Success = true);

/// <summary>Result of a module-listing daemon preamble (<see cref="DaemonPreamble.ListModulesAsync"/>).</summary>
/// <param name="MotdLines">Text lines that are not shaped like a module-listing line (docs/daemon-spec.md
/// §1: <c>printf "%-15s\t%s\n"</c>) — motd text and the blank separator line the daemon emits after it.
/// There is no formal wire-level tag distinguishing motd from listing lines; this is a shape heuristic
/// (see <see cref="LooksLikeModuleListingLine"/>), not a protocol fact.</param>
/// <param name="ModuleLines">Lines shaped like the module-listing format, verbatim (name padded to 15,
/// a TAB, then the comment — present even when empty).</param>
public sealed record DaemonModuleListResult(IReadOnlyList<string> MotdLines, IReadOnlyList<string> ModuleLines);

/// <summary>
/// Drives the textual daemon preamble (<c>rsync://</c>, docs/daemon-spec.md §1-3) over an
/// <see cref="IRsyncTransport"/>, before a normal role session (<see cref="PullSession"/>,
/// <see cref="PushSession"/>, <see cref="ListOnlySession"/>) takes over the now-binary stream.
/// </summary>
/// <remarks>
/// <para>
/// LINE READING TRAP (docs/daemon-spec.md §1, §6): the byte immediately after <c>@RSYNCD: OK\n</c> is
/// already the binary compat_flags varint — there is no separator. <see cref="ReadLineAsync"/>
/// therefore never buffers-and-splits; it scans the <see cref="PipeReader"/>'s buffer for a single
/// <c>'\n'</c> and calls <c>AdvanceTo</c> with the consumed position landing exactly one byte past it,
/// leaving everything after the newline untouched for whatever reads next (the binary phase, or the
/// next preamble line).
/// </para>
/// <para>
/// All lines are '\n'-terminated only — never CRLF, and never <see cref="Console.WriteLine"/> /
/// <see cref="Environment.NewLine"/>, which would emit "\r\n" on Windows and desync a real daemon.
/// </para>
/// </remarks>
public static class DaemonPreamble
{
    /// <summary>
    /// Runs the preamble for a single module: greeting exchange, module request, the AUTHREQD/OK/
    /// Error verdict loop, then writes the server argv. Leaves the transport positioned on the first
    /// binary byte (the compat_flags varint) so <see cref="HandshakeRunner.RunClientAsync"/> — called
    /// with <see cref="HandshakeOptions.PreNegotiatedProtocolVersion"/> set to the returned protocol —
    /// can continue the session immediately.
    /// </summary>
    /// <param name="transport">The daemon connection. Its <see cref="IRsyncTransport.Input"/>/
    /// <see cref="IRsyncTransport.Output"/> are consumed directly — this runs entirely before
    /// multiplexing starts.</param>
    /// <param name="module">Module name, e.g. <c>"tree"</c>. Never the empty string here — that is
    /// <see cref="ListModulesAsync"/>'s job.</param>
    /// <param name="serverArgs">
    /// The argv builder for the role that will run after the preamble. Its <see cref="ServerArgvBuilder.Protocol"/>
    /// is overridden to the negotiated protocol before <see cref="ServerArgvBuilder.Build"/> is called —
    /// the capability-letter bundle (<c>-tre.LsfxCIvu</c>) depends on it, and the negotiated value is
    /// only known once the server's greeting has been read.
    /// </param>
    /// <param name="user">Auth username. Required only if the module turns out to be auth-guarded.</param>
    /// <param name="password">Auth password. Required only if the module turns out to be auth-guarded;
    /// a null password against an AUTHREQD module throws a clear <see cref="ProtocolException"/>
    /// instead of silently sending garbage.</param>
    /// <param name="maxProtocol">The protocol we advertise in the client greeting; the session runs
    /// <c>min(this, the server's greeting version)</c>, same sanity ceiling as the ssh handshake.</param>
    /// <exception cref="ProtocolException">
    /// The server sent <c>@ERROR: &lt;text&gt;</c> (exit-5 semantics, docs/daemon-spec.md §1); the
    /// module is auth-guarded but no password was supplied; or the server's auth digest list has no
    /// <c>md5</c> (docs/daemon-spec.md §2 — only md5 is implemented).
    /// </exception>
    public static async Task<DaemonPreambleResult> RunAsync(
        IRsyncTransport transport,
        string module,
        ServerArgvBuilder serverArgs,
        string? user = null,
        string? password = null,
        int maxProtocol = RsyncConstants.ProtocolVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxProtocol, RsyncConstants.MinProtocolVersion);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxProtocol, RsyncConstants.ProtocolVersion);

        (int protocol, IReadOnlyList<string> serverDigests) =
            await ExchangeGreetingAsync(transport, maxProtocol, cancellationToken);
        RequireBinaryPhaseFloor(protocol);
        await WriteLineAsync(transport.Output, $"{module}\n", cancellationToken);

        var motd = new List<string>();
        while (true)
        {
            string line = await ReadLineAsync(transport.Input, cancellationToken);
            DaemonServerLine verdict = DaemonServerLine.Classify(line);
            switch (verdict.Kind)
            {
                case DaemonLineKind.Text:
                    motd.Add(verdict.Text!);
                    continue;

                case DaemonLineKind.AuthRequired:
                    await ReplyToAuthChallengeAsync(
                        transport, verdict.Text!, serverDigests, user, password, cancellationToken);
                    continue; // the verdict continues after our reply (docs/daemon-spec.md §2)

                case DaemonLineKind.Error:
                    throw new ProtocolException(RsyncExitCode.StartClientServerError, verdict.Text!);

                case DaemonLineKind.Ok:
                    goto ok;

                default:
                    // Exit is only legal in listing mode; here it means the server desynced from us.
                    throw new InvalidDataException(
                        $"daemon preamble: unexpected line during module request: \"{line}\"");
            }
        }

        ok:
        ServerArgvBuilder argvForProtocol = serverArgs with { Protocol = protocol };
        List<string> words = [.. argvForProtocol.Build().Skip(1)]; // drop the leading "rsync" word
        byte[] argvBytes = DaemonArgWriter.Write(words, protocol);
        transport.Output.Write(argvBytes);
        await transport.Output.FlushAsync(cancellationToken);

        return new DaemonPreambleResult(protocol, motd);
    }

    /// <summary>
    /// Runs the module-listing preamble: greeting exchange, the bare empty-line module request
    /// (docs/daemon-spec.md §1 — the real client sends an empty line, not <c>#list</c>), then collects
    /// every text line up to <c>@RSYNCD: EXIT</c>. No binary phase follows; the connection closes.
    /// </summary>
    public static async Task<DaemonModuleListResult> ListModulesAsync(
        IRsyncTransport transport,
        int maxProtocol = RsyncConstants.ProtocolVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxProtocol, RsyncConstants.MinProtocolVersion);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxProtocol, RsyncConstants.ProtocolVersion);

        await ExchangeGreetingAsync(transport, maxProtocol, cancellationToken);
        await WriteLineAsync(transport.Output, "\n", cancellationToken); // bare empty line = list request

        var motd = new List<string>();
        var moduleLines = new List<string>();
        while (true)
        {
            string line = await ReadLineAsync(transport.Input, cancellationToken);
            DaemonServerLine verdict = DaemonServerLine.Classify(line);
            switch (verdict.Kind)
            {
                case DaemonLineKind.Exit:
                    return new DaemonModuleListResult(motd, moduleLines);

                case DaemonLineKind.Error:
                    throw new ProtocolException(RsyncExitCode.StartClientServerError, verdict.Text!);

                case DaemonLineKind.Text:
                    (LooksLikeModuleListingLine(line) ? moduleLines : motd).Add(line);
                    continue;

                default:
                    throw new InvalidDataException(
                        $"daemon preamble: unexpected line during module listing: \"{line}\"");
            }
        }
    }

    /// <summary>
    /// Shape check for the module-listing format (docs/daemon-spec.md §1): <c>printf "%-15s\t%s\n"</c>
    /// — the name padded to width 15 followed by a TAB, present even for an empty comment. Motd text
    /// essentially never has a literal TAB at column 15, so this cleanly separates the two without any
    /// protocol-level tag to rely on (the wire carries no such distinction).
    /// </summary>
    private static bool LooksLikeModuleListingLine(string line) => line.Length >= 16 && line[15] == '\t';

    /// <summary>
    /// The preamble negotiates <c>min(ours, server)</c> and would otherwise accept a protocol as low
    /// as <see cref="RsyncConstants.MinProtocolVersion"/> (29), but the binary phase (FileListReader
    /// etc.) only implements 30/31 — a protocol-29 daemon would pass the preamble and then silently
    /// desync mid-flist instead of failing loudly. Only <see cref="RunAsync"/> calls this:
    /// <see cref="ListModulesAsync"/> never reaches the binary phase, so an old daemon there is fine.
    /// </summary>
    /// <exception cref="ProtocolException">The negotiated protocol is below 30.</exception>
    private static void RequireBinaryPhaseFloor(int protocol)
    {
        if (protocol < 30)
        {
            throw new ProtocolException(
                RsyncExitCode.ProtocolIncompatibility,
                $"daemon negotiated protocol {protocol}; protocol >= 30 required (binary phase does not support 29)");
        }
    }

    /// <summary>Writes our version + digest-list line and reads the server's, returning
    /// <c>(min(ours, theirs), the server's advertised auth-digest list)</c>.</summary>
    private static async Task<(int Protocol, IReadOnlyList<string> Digests)> ExchangeGreetingAsync(
        IRsyncTransport transport, int maxProtocol, CancellationToken cancellationToken)
    {
        string greetingLine = await ReadLineAsync(transport.Input, cancellationToken);
        (int serverVersion, _, IReadOnlyList<string> digests) = DaemonGreeting.Parse(greetingLine);
        if (serverVersion < RsyncConstants.MinProtocolVersion || serverVersion > RsyncConstants.MaxProtocolVersion)
        {
            throw new ProtocolException(
                RsyncExitCode.ProtocolIncompatibility,
                $"daemon greeting advertised protocol {serverVersion}, outside [{RsyncConstants.MinProtocolVersion}, {RsyncConstants.MaxProtocolVersion}]");
        }

        int protocol = Math.Min(maxProtocol, serverVersion);
        await WriteLineAsync(transport.Output, DaemonGreeting.FormatClient(protocol), cancellationToken);
        return (protocol, digests);
    }

    /// <summary>Computes and sends the auth reply for an AUTHREQD challenge (docs/daemon-spec.md §2).
    /// Only md5 is implemented, matching stock rsync 3.4.3's own choice off the same digest list.</summary>
    private static async Task ReplyToAuthChallengeAsync(
        IRsyncTransport transport, string challenge, IReadOnlyList<string> serverDigests,
        string? user, string? password, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(user))
            throw new ProtocolException(RsyncExitCode.SyntaxError, "daemon module requires auth but no username was supplied");
        if (password is null)
            throw new ProtocolException(RsyncExitCode.SyntaxError, $"daemon module requires auth but no password was supplied for user \"{user}\"");
        if (serverDigests.Count > 0 && !serverDigests.Contains("md5"))
        {
            throw new ProtocolException(RsyncExitCode.UnsupportedAction,
                $"daemon offered no md5 auth digest (advertised: {string.Join(' ', serverDigests)}) — only md5 is implemented");
        }

        string digest = DaemonAuth.ComputeDigest(password, challenge);
        await WriteLineAsync(transport.Output, DaemonAuth.FormatReply(user, digest), cancellationToken);
    }

    /// <summary>Writes a '\n'-terminated line verbatim — never CRLF, never <see cref="Console.WriteLine"/>.</summary>
    private static async ValueTask WriteLineAsync(PipeWriter output, string line, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line);
        output.Write(bytes);
        await output.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Cap on the accumulated bytes of one preamble line (F3, adversarial review). rsync's own
    /// read_line() caps around 1KB; we allow generous headroom for the digest/module-listing lines
    /// this codebase actually emits, while still refusing to buffer an unbounded stream forever
    /// waiting for a '\n' that a hostile or broken daemon never sends.
    /// </summary>
    private const int MaxLineLength = 4096;

    /// <summary>
    /// Reads one '\n'-terminated line, consuming exactly through the newline — never past it. This is
    /// the load-bearing piece of the over-read trap (docs/daemon-spec.md §1/§6): the byte right after
    /// <c>@RSYNCD: OK\n</c> is the first binary byte of the session, so a reader that buffers ahead and
    /// splits on newlines would swallow it.
    /// </summary>
    /// <exception cref="InvalidDataException">The stream ended before a '\n' was found.</exception>
    /// <exception cref="ProtocolException">More than <see cref="MaxLineLength"/> bytes were buffered
    /// with no '\n' in sight — treated as a protocol-stream error (exit 12 family), not a hang.</exception>
    private static async ValueTask<string> ReadLineAsync(PipeReader input, CancellationToken cancellationToken)
    {
        while (true)
        {
            ReadResult result = await input.ReadAsync(cancellationToken);
            ReadOnlySequence<byte> buffer = result.Buffer;

            SequencePosition? newlinePosition = buffer.PositionOf((byte)'\n');
            if (newlinePosition is not null)
            {
                ReadOnlySequence<byte> lineBytes = buffer.Slice(0, newlinePosition.Value);
                string line = Encoding.UTF8.GetString(lineBytes);
                SequencePosition consumedTo = buffer.GetPosition(1, newlinePosition.Value); // past the '\n'
                input.AdvanceTo(consumedTo, consumedTo);
                return line;
            }

            if (buffer.Length > MaxLineLength)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                throw new ProtocolException(
                    RsyncExitCode.ProtocolStreamError,
                    $"daemon preamble: line exceeded {MaxLineLength} bytes with no '\\n' — treating the stream as corrupt");
            }

            if (result.IsCompleted)
            {
                input.AdvanceTo(buffer.Start, buffer.End);
                throw new InvalidDataException(
                    $"daemon preamble: stream ended mid-line after {buffer.Length} buffered bytes with no '\\n'");
            }

            // No '\n' yet: nothing consumed, but everything examined so far — ask for more bytes.
            input.AdvanceTo(buffer.Start, buffer.End);
        }
    }
}
