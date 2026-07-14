using System.ComponentModel;
using System.Net.Sockets;
using RsyncWin.Cli;
using RsyncWin.Engine;
using RsyncWin.Fs;
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
            PrintUsage();
        return (int)RsyncExitCode.SyntaxError;
    }

    ParsedCommand cmd = command!;
    return cmd.Action switch
    {
        ParsedAction.DaemonList => await RunDaemonListAsync(cmd.Endpoint!),
        ParsedAction.SshPull => await RunPullAsync(cmd.Source!, cmd.Dest!, cmd.Recurse, cmd.Archive, cmd.Checksum, cmd.Delete, cmd.RshOverride),
        ParsedAction.SshPush => await RunPushAsync(cmd.Source!, cmd.Dest!, cmd.Recurse, cmd.Archive, cmd.RshOverride),
        ParsedAction.DaemonPull => await RunDaemonPullAsync(cmd.Endpoint!, cmd.Dest!, cmd.Recurse, cmd.Archive, cmd.Checksum, cmd.Delete),
        ParsedAction.DaemonPush => await RunDaemonPushAsync(cmd.Source!, cmd.Endpoint!, cmd.Recurse, cmd.Archive),
        _ => throw new InvalidOperationException($"unhandled parsed action {cmd.Action}"),
    };
}

/// <summary>Prints every supported invocation form to stderr — shared by every usage-error exit path.</summary>
static void PrintUsage()
{
    Console.Error.WriteLine("usage: rsyncwin -r [-t|-a] [-e ssh_path] [user@]host:path localdir   (ssh pull)");
    Console.Error.WriteLine("       rsyncwin -r [-t] [-e ssh_path] localdir [user@]host:path      (ssh push; -a not yet)");
    Console.Error.WriteLine("       rsyncwin -r [-t|-a] rsync://[user@]host[:port]/module[/path] localdir           (daemon pull)");
    Console.Error.WriteLine("       rsyncwin -r [-t] localdir rsync://[user@]host[:port]/module[/path]              (daemon push; -a not yet)");
    Console.Error.WriteLine("       \"[user@]host::module[/path]\" is accepted anywhere \"rsync://host/module[/path]\" is (port always 873)");
    Console.Error.WriteLine("       rsyncwin rsync://[user@]host[:port]/            (list daemon modules — module left empty)");
    Console.Error.WriteLine("       daemon auth password is read from the RSYNC_PASSWORD environment variable");
    Console.Error.WriteLine("       -c/--checksum and --delete are pull-only for now (P10 will add push support)");
}

static async Task<int> RunPullAsync(
    string source, string dest, bool recurse, bool archive, bool checksum, bool delete, string? rshOverride)
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
    };

    (OpenSshProcessTransport? transport, int startError) = StartSsh(user, host, serverArgs.Build(), rshOverride);
    if (transport is null)
        return startError;

    await using (transport)
    {
        PullSession.Result result;
        try
        {
            result = await PullSession.RunAsync(transport, serverArgs, dest, delete: delete);
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

static async Task<int> RunPushAsync(string source, string dest, bool recurse, bool archive, string? rshOverride)
{
    if (archive)
    {
        // FileListWriter cannot encode the -a extras yet (links/owner/group/devices/perms throw
        // NotSupportedException); reject up front instead of crashing after the remote is started
        Console.Error.WriteLine("rsyncwin: -a is not supported for push yet — use -rt (P9)");
        return (int)RsyncExitCode.SyntaxError;
    }

    (string? user, string host, string remotePath) = CommandLineParser.SplitRemoteSpec(dest);

    // TODO(P9): trailing-slash semantics on SOURCE ("localdir/" pushes contents, "localdir" would
    // create the dir itself) are not modeled here — FileEnumerator always walks the root's contents
    // and never wraps it in an extra directory level, mirroring the pull CLI's existing convention
    // (dest is always treated as "receive contents into this directory", never checked for a
    // trailing slash either). Revisit both sides together in P9 if divergent behavior is needed.
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
    };

    (OpenSshProcessTransport? transport, int startError) = StartSsh(user, host, serverArgs.Build(), rshOverride);
    if (transport is null)
        return startError;

    await using (transport)
    {
        PushSession.Result result;
        try
        {
            result = await PushSession.RunAsync(transport, serverArgs, entries);
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
    DaemonEndpoint endpoint, string dest, bool recurse, bool archive, bool checksum, bool delete)
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
    };

    (DaemonTcpTransport? transport, DaemonPreambleResult? preamble, int connectError) =
        await ConnectDaemonAsync(endpoint, serverArgs);
    if (transport is null || preamble is null)
        return connectError;

    await using (transport)
    {
        foreach (string line in preamble.MotdLines)
            Console.WriteLine(line);

        var handshake = new HandshakeOptions { PreNegotiatedProtocolVersion = preamble.Protocol };

        PullSession.Result result;
        try
        {
            result = await PullSession.RunAsync(transport, serverArgs, dest, handshake: handshake, delete: delete);
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

static async Task<int> RunDaemonPushAsync(string source, DaemonEndpoint endpoint, bool recurse, bool archive)
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
    };

    (DaemonTcpTransport? transport, DaemonPreambleResult? preamble, int connectError) =
        await ConnectDaemonAsync(endpoint, serverArgs);
    if (transport is null || preamble is null)
        return connectError;

    await using (transport)
    {
        foreach (string line in preamble.MotdLines)
            Console.WriteLine(line);

        var handshake = new HandshakeOptions { PreNegotiatedProtocolVersion = preamble.Protocol };

        PushSession.Result result;
        try
        {
            result = await PushSession.RunAsync(transport, serverArgs, entries, handshake: handshake);
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
