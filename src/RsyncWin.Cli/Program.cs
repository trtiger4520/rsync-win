using System.ComponentModel;
using System.Net.Sockets;
using RsyncWin.Cli;
using RsyncWin.Engine;
using RsyncWin.Fs;
using RsyncWin.Protocol.Delta;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Session;
using RsyncWin.Transport;

// Thin shell: argument parsing + exit-code mapping only. All protocol logic lives in
// RsyncWin.Engine/RsyncWin.Protocol; this file never reasons about wire bytes.
// Flag parsing + source/dest classification live in CommandLineParser.cs (unit-tested without
// launching ssh); exception -> exit-code mapping lives in ExitCodeMapper.cs.
return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(args);
    if (failure is not null)
    {
        Console.Error.WriteLine(failure.Message);
        if (failure.ShowUsage)
            PrintHelp(Console.Error);
        return (int)RsyncExitCode.SyntaxError;
    }

    ParsedCommand cmd = command!;
    return cmd.Action switch
    {
        ParsedAction.ShowHelp => RunHelp(),
        ParsedAction.DaemonList => await RunDaemonListAsync(cmd.Endpoint!),
        ParsedAction.SshPull => await RunPullAsync(cmd.Source!, cmd.Dest!, cmd.Recurse, cmd.Archive, cmd.Checksum, cmd.Delete, cmd.Secluded, cmd.Compress, cmd.RshOverride, cmd.Progress, cmd.InfoProgress2),
        ParsedAction.SshPush => await RunPushAsync(cmd.Source!, cmd.Dest!, cmd.Recurse, cmd.Archive, cmd.Checksum, cmd.Delete, cmd.Secluded, cmd.Compress, cmd.RshOverride, cmd.Progress, cmd.InfoProgress2),
        ParsedAction.DaemonPull => await RunDaemonPullAsync(cmd.Endpoint!, cmd.Dest!, cmd.Recurse, cmd.Archive, cmd.Checksum, cmd.Delete, cmd.Compress, cmd.Progress, cmd.InfoProgress2),
        ParsedAction.DaemonPush => await RunDaemonPushAsync(cmd.Source!, cmd.Endpoint!, cmd.Recurse, cmd.Archive, cmd.Checksum, cmd.Delete, cmd.Compress, cmd.Progress, cmd.InfoProgress2),
        ParsedAction.LocalCopy => RunLocal(cmd.Source!, cmd.Dest!, cmd.Recurse || cmd.Archive, cmd.Checksum, cmd.Delete, cmd.Progress, cmd.InfoProgress2),
        _ => throw new InvalidOperationException($"unhandled parsed action {cmd.Action}"),
    };
}

/// <summary>-h/--help: the full help goes to stdout and the run exits 0 — unlike the usage-error
/// path, which prints the same text to stderr and exits 1 (rsync convention for both).</summary>
static int RunHelp()
{
    PrintHelp(Console.Out);
    return (int)RsyncExitCode.Ok;
}

/// <summary>The single source of the usage/help text — stdout for -h/--help, stderr for usage errors.</summary>
static void PrintHelp(TextWriter writer)
{
    writer.WriteLine("rsyncwin — a native Windows rsync client (protocol 31)");
    writer.WriteLine();
    writer.WriteLine("Usage:");
    writer.WriteLine("  rsyncwin [OPTIONS] [user@]host:path localdir                            ssh pull");
    writer.WriteLine("  rsyncwin [OPTIONS] localdir [user@]host:path                            ssh push (-a not yet)");
    writer.WriteLine("  rsyncwin [OPTIONS] rsync://[user@]host[:port]/module[/path] localdir    daemon pull");
    writer.WriteLine("  rsyncwin [OPTIONS] localdir rsync://[user@]host[:port]/module[/path]    daemon push (-a not yet)");
    writer.WriteLine("  rsyncwin rsync://[user@]host[:port]/                                    list daemon modules");
    writer.WriteLine("  rsyncwin [OPTIONS] localsrc localdir                                    local copy (\"src\\\" copies contents; \"src\" creates dir)");
    writer.WriteLine();
    writer.WriteLine("Options:");
    writer.WriteLine("  -r, --recursive        recurse into directories");
    writer.WriteLine("  -a, --archive          archive mode: -r plus preserve links/perms/owner/group/devices (pull only for now)");
    writer.WriteLine("  -t, --times            preserve modification times (already the default; accepted for compatibility)");
    writer.WriteLine("  -c, --checksum         skip the mtime+size fast path; compare files by full block checksum");
    writer.WriteLine("      --delete           delete extraneous files from the destination (requires -r or -a)");
    writer.WriteLine("  -s, --secluded-args    keep remote paths off the ssh command line (alias: --protect-args)");
    writer.WriteLine("  -z, --compress         compress file data during transfer (zlibx)");
    writer.WriteLine("      --progress         show a per-file progress bar (bytes, %, rate, ETA)");
    writer.WriteLine("      --info=progress2   show a single progress line for the whole transfer");
    writer.WriteLine(@"  -e, --rsh PATH         use PATH as the ssh executable (default: C:\Windows\System32\OpenSSH\ssh.exe)");
    writer.WriteLine("  -h, --help             show this help and exit");
    writer.WriteLine();
    writer.WriteLine("Notes:");
    writer.WriteLine("  \"[user@]host::module[/path]\" is accepted anywhere \"rsync://host/module[/path]\" is (port always 873).");
    writer.WriteLine("  Daemon auth password is read from the RSYNC_PASSWORD environment variable.");
}

/// <summary>Builds the progress sink for a run: a real renderer when <c>--progress</c> or
/// <c>--info=progress2</c> is set (whole-transfer wins), else the no-op sink. Renders to stderr, and
/// only animates with carriage returns when stderr is a real console (docs/progress-spec.md).</summary>
static ITransferProgressSink CreateProgressSink(bool progress, bool infoProgress2)
{
    if (!progress && !infoProgress2)
        return NullProgressSink.Instance;
    ProgressMode mode = infoProgress2 ? ProgressMode.WholeTransfer : ProgressMode.PerFile;
    return new ProgressRenderer(Console.Error, animate: !Console.IsErrorRedirected, mode);
}

static async Task<int> RunPullAsync(
    string source, string dest, bool recurse, bool archive, bool checksum, bool delete, bool secluded, bool compress, string? rshOverride,
    bool progress, bool infoProgress2)
{
    (string? user, string host, string remotePath) = CommandLineParser.SplitRemoteSpec(source);

    var serverArgs = new ServerArgvBuilder
    {
        Sender = true, // pull: the remote side sends
        Paths = [remotePath],
        Recurse = recurse || archive,
        PreserveLinks = archive,
        PreserveOwner = archive,
        PreserveGroup = archive,
        PreserveDevices = archive,
        PreservePerms = archive,
        Checksum = checksum,
        SecludedArgs = secluded,
        Compress = compress,
    };

    // -s holds the remote path back from the ssh command line (a pre-handshake NUL list); -z forces
    // zlibx token compression (no compression string negotiated). Either needs a handshake override.
    var handshake = secluded || compress
        ? new HandshakeOptions
        {
            SecludedArgs = secluded ? [remotePath] : null,
            Compression = compress ? CompressionMethod.Zlibx : CompressionMethod.None,
        }
        : null;

    (OpenSshProcessTransport? transport, int startError) = StartSsh(user, host, serverArgs.Build(), rshOverride);
    if (transport is null)
        return startError;

    await using (transport)
    {
        PullSession.Result result;
        try
        {
            result = await PullSession.RunAsync(transport, serverArgs, dest, handshake: handshake, delete: delete,
                progress: CreateProgressSink(progress, infoProgress2));
        }
        catch (Exception ex) when (ex is ProtocolException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return await ReportSshFailureAsync(ex, transport);
        }

        foreach (string name in result.MappedNames)
            Console.Error.WriteLine($"mapped: {name} -> {WindowsPathMapper.Map(name).Mapped}");
        foreach ((string name, string reason) in result.SkippedNonRegular)
            Console.Error.WriteLine($"skipping {reason}: {name}");
        Console.Error.WriteLine($"files transferred: {result.TransferredFiles}, bytes: {result.TransferredBytes}");
        ReportPruneResult(result.Prune, result.PruneSkippedDueToIoError);

        if (await TryGetSshExitCodeAsync(transport) == 255)
        {
            Console.Error.WriteLine(await transport.ReadAllStandardErrorAsync());
            return (int)RsyncExitCode.StartClientServerError;
        }

        return result.FailedFiles.Count > 0
            ? (int)RsyncExitCode.PartialTransferError
            : (int)RsyncExitCode.Ok;
    }
}

static async Task<int> RunPushAsync(
    string source, string dest, bool recurse, bool archive, bool checksum, bool delete, bool secluded, bool compress, string? rshOverride,
    bool progress, bool infoProgress2)
{
    if (archive)
    {
        // FileListWriter cannot encode the -a extras yet (links/owner/group/devices/perms throw
        // NotSupportedException); reject up front instead of crashing after the remote is started
        Console.Error.WriteLine("rsyncwin: -a is not supported for push yet — use -rt (P9)");
        return (int)RsyncExitCode.SyntaxError;
    }

    (string? user, string host, string remotePath) = CommandLineParser.SplitRemoteSpec(dest);

    // A single-file SOURCE is handled: FileEnumerator emits one basename entry with no "." root, the
    // flist shape canonical rsync sends for `rsync file host::mod/`. What remains as TODO(P9):
    // trailing-slash semantics on a *directory* SOURCE ("localdir/" pushes contents, "localdir" would
    // create the dir itself) are not modeled here — FileEnumerator always walks a directory root's
    // contents and never wraps it in an extra directory level, mirroring the pull CLI's existing
    // convention (dest is always treated as "receive contents into this directory", never checked for
    // a trailing slash either). Revisit both sides together in P9 if divergent behavior is needed.
    IReadOnlyList<EnumeratedEntry> walked;
    try
    {
        walked = FileEnumerator.Enumerate(source);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"rsyncwin: {ex.Message}");
        return (int)RsyncExitCode.FileIoError;
    }

    var entries = new List<EnumeratedEntry>(walked.Count);
    foreach (EnumeratedEntry entry in walked)
    {
        if (entry.Wire.IsDirectory || entry.Wire.IsRegularFile)
            entries.Add(entry);
        else
            // Symlinks/devices are not sent at all (mirrors pull's warn-and-skip policy, not fatal).
            Console.Error.WriteLine($"skipping {(entry.Wire.IsSymlink ? "symlink" : "not a regular file")}: {entry.Wire.Name}");
    }

    var serverArgs = new ServerArgvBuilder
    {
        Sender = false, // push: the remote side receives
        Paths = [remotePath],
        Recurse = recurse || archive,
        PreserveLinks = archive,
        PreserveOwner = archive,
        PreserveGroup = archive,
        PreserveDevices = archive,
        PreservePerms = archive,
        Checksum = checksum,
        Delete = delete,
        SecludedArgs = secluded,
        Compress = compress,
    };

    // -s holds the remote dest path back from the ssh command line (a pre-handshake NUL list); -z
    // forces zlibx token compression. Either needs a handshake override.
    var handshake = secluded || compress
        ? new HandshakeOptions
        {
            SecludedArgs = secluded ? [remotePath] : null,
            Compression = compress ? CompressionMethod.Zlibx : CompressionMethod.None,
        }
        : null;

    (OpenSshProcessTransport? transport, int startError) = StartSsh(user, host, serverArgs.Build(), rshOverride);
    if (transport is null)
        return startError;

    await using (transport)
    {
        PushSession.Result result;
        try
        {
            result = await PushSession.RunAsync(transport, serverArgs, entries, handshake: handshake,
                progress: CreateProgressSink(progress, infoProgress2));
        }
        catch (Exception ex) when (ex is ProtocolException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return await ReportSshFailureAsync(ex, transport);
        }

        foreach (string name in result.FailedFiles)
            Console.Error.WriteLine(result.OversizeFiles.Contains(name)
                ? $"skipping: {name} — source file too large (>2 GiB) for this build"
                : $"skipping vanished: {name}");
        foreach (ServerMessage message in result.ServerMessages)
            if (message.Tag is MessageTag.Error or MessageTag.ErrorXfer or MessageTag.Warning)
                Console.Error.WriteLine($"remote: {message.Text}");
        foreach (string path in result.DeletedPaths)
            Console.Error.WriteLine($"deleting {path}");
        Console.Error.WriteLine(
            $"files sent: {result.FilesSent}, literal bytes: {result.LiteralBytes}, matched bytes: {result.MatchedBytes}");

        if (await TryGetSshExitCodeAsync(transport) == 255)
        {
            Console.Error.WriteLine(await transport.ReadAllStandardErrorAsync());
            return (int)RsyncExitCode.StartClientServerError;
        }

        // A remote MSG_ERROR/MSG_ERROR_XFER means the generator/receiver reported a failure even
        // when every one of our own replies went out clean (FailedFiles empty) — without this,
        // such a session silently exits 0 where rsync itself would exit 23.
        bool remoteReportedError = result.ServerMessages.Any(
            m => m.Tag is MessageTag.Error or MessageTag.ErrorXfer);

        return result.FailedFiles.Count > 0 || remoteReportedError
            ? (int)RsyncExitCode.PartialTransferError
            : (int)RsyncExitCode.Ok;
    }
}

static int RunLocal(string source, string dest, bool recurse, bool checksum, bool delete, bool progress, bool infoProgress2)
{
    LocalSyncResult result;
    try
    {
        result = LocalSyncEngine.Run(source, dest, new LocalSyncOptions(recurse, checksum, delete),
            CreateProgressSink(progress, infoProgress2));
    }
    catch (ArgumentException ex)
    {
        // Shape errors from the engine (self-copy, dest-inside-source, contents-into-a-file) or a
        // malformed path — syntax-level, like the parser's own rejections.
        Console.Error.WriteLine($"rsyncwin: {ex.Message}");
        return (int)RsyncExitCode.SyntaxError;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // Missing/unreadable source or an unusable destination root — file I/O (11), never 12:
        // there is no protocol stream in a local copy.
        Console.Error.WriteLine($"rsyncwin: {ex.Message}");
        return (int)ExitCodeMapper.Map(ex);
    }

    foreach (string name in result.SkippedDirectories)
        Console.Error.WriteLine($"skipping directory {name}");
    foreach ((string name, string reason) in result.SkippedNonRegular)
        Console.Error.WriteLine($"skipping {reason}: {name}");
    foreach ((string path, string reason) in result.FailedFiles)
        Console.Error.WriteLine($"rsyncwin: {path}: {reason}");
    Console.Error.WriteLine($"files transferred: {result.TransferredFiles}, bytes: {result.TransferredBytes}");
    ReportPruneResult(result.Prune, result.PruneSkipped);

    return result.FailedFiles.Count > 0
        ? (int)RsyncExitCode.PartialTransferError
        : (int)RsyncExitCode.Ok;
}

/// <summary>Shared session-exception handler for the ssh Run* methods: prints the error, checks
/// whether ssh.exe itself already died (255) via <see cref="ExitCodeMapper"/> — which takes
/// precedence for <see cref="ProtocolException"/>/<see cref="InvalidDataException"/> only, never for
/// a plain filesystem failure — and surfaces ssh's stderr when that is the actual cause.</summary>
static async Task<int> ReportSshFailureAsync(Exception ex, OpenSshProcessTransport transport)
{
    Console.Error.WriteLine($"rsyncwin: {ex.Message}");
    int? sshExitCode = ex is ProtocolException or InvalidDataException
        ? await TryGetSshExitCodeAsync(transport)
        : null;
    RsyncExitCode mapped = ExitCodeMapper.Map(ex, sshExitCode);
    if (mapped == RsyncExitCode.StartClientServerError && sshExitCode == 255)
        Console.Error.WriteLine(await transport.ReadAllStandardErrorAsync());
    return (int)mapped;
}

/// <summary>Shared session-exception handler for the daemon Run* methods — no ssh subprocess, so no
/// 255 special-casing, just a straight mapping.</summary>
static int ReportDaemonFailure(Exception ex)
{
    Console.Error.WriteLine($"rsyncwin: {ex.Message}");
    return (int)ExitCodeMapper.Map(ex, sshExitCode: null);
}

/// <summary>Best-effort surfacing of <c>--delete</c> results to stderr, mirroring the existing "files
/// transferred" report line. A prune skipped due to a flist io_error is reported once, not per file.</summary>
static void ReportPruneResult(PruneResult prune, bool pruneSkippedDueToIoError)
{
    if (pruneSkippedDueToIoError)
    {
        Console.Error.WriteLine("rsyncwin: IO error encountered -- skipping file deletion");
        return;
    }

    foreach (string path in prune.DeletedPaths)
        Console.Error.WriteLine($"deleting {path}");
}

static async Task<int> RunDaemonPullAsync(
    DaemonEndpoint endpoint, string dest, bool recurse, bool archive, bool checksum, bool delete, bool compress,
    bool progress, bool infoProgress2)
{
    var serverArgs = new ServerArgvBuilder
    {
        Sender = true, // pull: the remote side sends
        Paths = [CommandLineParser.ComposeServerPath(endpoint.Module, endpoint.ModulePath)],
        Recurse = recurse || archive,
        PreserveLinks = archive,
        PreserveOwner = archive,
        PreserveGroup = archive,
        PreserveDevices = archive,
        PreservePerms = archive,
        Checksum = checksum,
        Compress = compress,
    };

    (DaemonTcpTransport? transport, DaemonPreambleResult? preamble, int connectError) =
        await ConnectDaemonAsync(endpoint, serverArgs);
    if (transport is null || preamble is null)
        return connectError;

    await using (transport)
    {
        foreach (string line in preamble.MotdLines)
            Console.WriteLine(line);

        var handshake = new HandshakeOptions { PreNegotiatedProtocolVersion = preamble.Protocol, Compression = compress ? CompressionMethod.Zlibx : CompressionMethod.None };

        PullSession.Result result;
        try
        {
            result = await PullSession.RunAsync(transport, serverArgs, dest, handshake: handshake, delete: delete,
                progress: CreateProgressSink(progress, infoProgress2));
        }
        catch (Exception ex) when (ex is ProtocolException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // In-mux MSG_ERROR_EXIT carries a real exit code — surfaced verbatim, never squashed to 12.
            return ReportDaemonFailure(ex);
        }

        foreach (string name in result.MappedNames)
            Console.Error.WriteLine($"mapped: {name} -> {WindowsPathMapper.Map(name).Mapped}");
        foreach ((string name, string reason) in result.SkippedNonRegular)
            Console.Error.WriteLine($"skipping {reason}: {name}");
        Console.Error.WriteLine($"files transferred: {result.TransferredFiles}, bytes: {result.TransferredBytes}");
        ReportPruneResult(result.Prune, result.PruneSkippedDueToIoError);

        return result.FailedFiles.Count > 0
            ? (int)RsyncExitCode.PartialTransferError
            : (int)RsyncExitCode.Ok;
    }
}

static async Task<int> RunDaemonPushAsync(
    string source, DaemonEndpoint endpoint, bool recurse, bool archive, bool checksum, bool delete, bool compress,
    bool progress, bool infoProgress2)
{
    if (archive)
    {
        // Checked before any connection attempt — same as the ssh push path, and for the same reason
        // (FileListWriter cannot encode the -a extras yet).
        Console.Error.WriteLine("rsyncwin: -a is not supported for push yet — use -rt (P9)");
        return (int)RsyncExitCode.SyntaxError;
    }

    IReadOnlyList<EnumeratedEntry> walked;
    try
    {
        walked = FileEnumerator.Enumerate(source);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine($"rsyncwin: {ex.Message}");
        return (int)RsyncExitCode.FileIoError;
    }

    var entries = new List<EnumeratedEntry>(walked.Count);
    foreach (EnumeratedEntry entry in walked)
    {
        if (entry.Wire.IsDirectory || entry.Wire.IsRegularFile)
            entries.Add(entry);
        else
            // Symlinks/devices are not sent at all (mirrors pull's warn-and-skip policy, not fatal).
            Console.Error.WriteLine($"skipping {(entry.Wire.IsSymlink ? "symlink" : "not a regular file")}: {entry.Wire.Name}");
    }

    var serverArgs = new ServerArgvBuilder
    {
        Sender = false, // push: the remote side receives
        Paths = [CommandLineParser.ComposeServerPath(endpoint.Module, endpoint.ModulePath)],
        Recurse = recurse || archive,
        PreserveLinks = archive,
        PreserveOwner = archive,
        PreserveGroup = archive,
        PreserveDevices = archive,
        PreservePerms = archive,
        Checksum = checksum,
        Delete = delete,
        Compress = compress,
    };

    (DaemonTcpTransport? transport, DaemonPreambleResult? preamble, int connectError) =
        await ConnectDaemonAsync(endpoint, serverArgs);
    if (transport is null || preamble is null)
        return connectError;

    await using (transport)
    {
        foreach (string line in preamble.MotdLines)
            Console.WriteLine(line);

        var handshake = new HandshakeOptions { PreNegotiatedProtocolVersion = preamble.Protocol, Compression = compress ? CompressionMethod.Zlibx : CompressionMethod.None };

        PushSession.Result result;
        try
        {
            result = await PushSession.RunAsync(transport, serverArgs, entries, handshake: handshake,
                progress: CreateProgressSink(progress, infoProgress2));
        }
        catch (Exception ex) when (ex is ProtocolException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return ReportDaemonFailure(ex);
        }

        foreach (string name in result.FailedFiles)
            Console.Error.WriteLine(result.OversizeFiles.Contains(name)
                ? $"skipping: {name} — source file too large (>2 GiB) for this build"
                : $"skipping vanished: {name}");
        foreach (ServerMessage message in result.ServerMessages)
            if (message.Tag is MessageTag.Error or MessageTag.ErrorXfer or MessageTag.Warning)
                Console.Error.WriteLine($"remote: {message.Text}");
        foreach (string path in result.DeletedPaths)
            Console.Error.WriteLine($"deleting {path}");
        Console.Error.WriteLine(
            $"files sent: {result.FilesSent}, literal bytes: {result.LiteralBytes}, matched bytes: {result.MatchedBytes}");

        // Same as the ssh push path: a remote MSG_ERROR/MSG_ERROR_XFER means the receiver reported a
        // failure even when every one of our own replies went out clean.
        bool remoteReportedError = result.ServerMessages.Any(
            m => m.Tag is MessageTag.Error or MessageTag.ErrorXfer);

        return result.FailedFiles.Count > 0 || remoteReportedError
            ? (int)RsyncExitCode.PartialTransferError
            : (int)RsyncExitCode.Ok;
    }
}

static async Task<int> RunDaemonListAsync(DaemonEndpoint endpoint)
{
    (DaemonTcpTransport? transport, int connectError) = await ConnectDaemonTransportAsync(endpoint);
    if (transport is null)
        return connectError;

    await using (transport)
    {
        DaemonModuleListResult result;
        try
        {
            result = await DaemonPreamble.ListModulesAsync(transport);
        }
        catch (Exception ex) when (ex is ProtocolException or InvalidDataException)
        {
            return ReportDaemonFailure(ex);
        }

        foreach (string line in result.MotdLines)
            Console.WriteLine(line);
        foreach (string line in result.ModuleLines)
            Console.WriteLine(line);

        return (int)RsyncExitCode.Ok;
    }
}

/// <summary>Opens the raw TCP connection to a daemon endpoint — connection refused or DNS failure
/// maps to exit 10 (RERR_SOCKETIO), never 12 (that's a protocol-stream desync, not a socket error).</summary>
static async Task<(DaemonTcpTransport? Transport, int ErrorExitCode)> ConnectDaemonTransportAsync(DaemonEndpoint endpoint)
{
    try
    {
        return (await DaemonTcpTransport.ConnectAsync(endpoint.Host, endpoint.Port), (int)RsyncExitCode.Ok);
    }
    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
    {
        Console.Error.WriteLine($"rsyncwin: failed to connect to daemon {endpoint.Host}:{endpoint.Port}: {ex.Message}");
        return (null, (int)RsyncExitCode.SocketIoError);
    }
}

/// <summary>Connects and runs the module-session preamble (docs/daemon-spec.md §1-3) — shared by
/// daemon pull and push, which differ only in <paramref name="serverArgs"/>'s <c>Sender</c> flag and
/// what follows. On failure the transport is already disposed and (null, null, exitCode) is returned.</summary>
static async Task<(DaemonTcpTransport? Transport, DaemonPreambleResult? Preamble, int ErrorExitCode)> ConnectDaemonAsync(
    DaemonEndpoint endpoint, ServerArgvBuilder serverArgs)
{
    (DaemonTcpTransport? transport, int connectError) = await ConnectDaemonTransportAsync(endpoint);
    if (transport is null)
        return (null, null, connectError);

    string? password = Environment.GetEnvironmentVariable("RSYNC_PASSWORD");
    try
    {
        DaemonPreambleResult preamble = await DaemonPreamble.RunAsync(transport, endpoint.Module, serverArgs, endpoint.User, password);
        return (transport, preamble, (int)RsyncExitCode.Ok);
    }
    catch (ProtocolException ex)
    {
        // @ERROR during the preamble (bad module, auth failure, missing password) — exit 5, never
        // the ssh-specific 255-exit-code handling, which does not apply to a daemon socket.
        Console.Error.WriteLine($"rsyncwin: {ex.Message}");
        await transport.DisposeAsync();
        return (null, null, (int)ex.ExitCode);
    }
    catch (InvalidDataException ex)
    {
        Console.Error.WriteLine($"rsyncwin: {ex.Message}");
        await transport.DisposeAsync();
        return (null, null, (int)RsyncExitCode.ProtocolStreamError);
    }
}

/// <summary>Launches ssh.exe with the server argv appended after the host — shared by pull and push,
/// which differ only in what <paramref name="serverArgv"/> and the host/user came from.</summary>
static (OpenSshProcessTransport? Transport, int ErrorExitCode) StartSsh(
    string? user, string host, IReadOnlyList<string> serverArgv, string? rshOverride)
{
    var sshArgs = new List<string>();
    if (user is not null)
    {
        sshArgs.Add("-l");
        sshArgs.Add(user);
    }
    sshArgs.Add(host);
    sshArgs.AddRange(serverArgv);

    string sshExe = rshOverride ?? OpenSshProcessTransport.DefaultSshExePath;
    try
    {
        return (OpenSshProcessTransport.Start(sshExe, sshArgs), (int)RsyncExitCode.Ok);
    }
    catch (Win32Exception ex)
    {
        Console.Error.WriteLine($"rsyncwin: failed to start \"{sshExe}\": {ex.Message}");
        return (null, (int)RsyncExitCode.StartClientServerError);
    }
}

/// <summary>
/// Bounded check for whether ssh.exe has already exited (and with what code) — a live process must
/// never hang this check, since ssh.exe normally lingers a moment after the protocol tail closes.
/// </summary>
static async ValueTask<int?> TryGetSshExitCodeAsync(IRsyncTransport transport)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try
    {
        return await transport.WaitForExitAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        return null; // still running — not a launch failure
    }
}
