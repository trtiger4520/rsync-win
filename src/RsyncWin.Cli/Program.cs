using System.ComponentModel;
using System.Net.Sockets;
using RsyncWin.Engine;
using RsyncWin.Fs;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Session;
using RsyncWin.Transport;

// Thin shell: argument parsing + exit-code mapping only. All protocol logic lives in
// RsyncWin.Engine/RsyncWin.Protocol; this file never reasons about wire bytes.
return await RunAsync(args);

static async Task<int> RunAsync(string[] args)
{
    string? rshOverride = null;
    bool recurse = false;
    bool archive = false;
    var positionals = new List<string>();

    for (int i = 0; i < args.Length; i++)
    {
        string arg = args[i];
        if (arg is "-e" or "--rsh")
        {
            if (++i >= args.Length)
            {
                Console.Error.WriteLine($"rsyncwin: {arg} requires an argument");
                return (int)RsyncExitCode.SyntaxError;
            }
            rshOverride = args[i];
        }
        else if (arg.Length > 1 && arg[0] == '-' && arg[1] != '-')
        {
            // Bundled short options, e.g. -r, -rt, -a — only flags ServerArgvBuilder already supports.
            foreach (char flag in arg[1..])
            {
                switch (flag)
                {
                    case 'r': recurse = true; break;
                    case 't': break; // PreserveTimes already defaults to true
                    case 'a': archive = true; break;
                    default:
                        Console.Error.WriteLine($"rsyncwin: unsupported option -{flag}");
                        return (int)RsyncExitCode.SyntaxError;
                }
            }
        }
        else
        {
            positionals.Add(arg);
        }
    }

    // Daemon module listing is the one case with a single positional: a bare daemon endpoint
    // ("rsync://host[:port]/", "rsync://host[:port]" with no path, or "host::") whose module is
    // empty lists the daemon's modules instead of transferring anything.
    if (positionals.Count == 1)
    {
        if (!TryParseDaemonEndpoint(positionals[0], out DaemonEndpoint? listEndpoint, out string? listParseError))
        {
            if (listParseError is not null)
            {
                Console.Error.WriteLine(listParseError);
                PrintUsage();
                return (int)RsyncExitCode.SyntaxError;
            }
        }
        else if (listEndpoint!.Module.Length == 0)
        {
            return await RunDaemonListAsync(listEndpoint);
        }
    }

    if (positionals.Count != 2)
    {
        Console.Error.WriteLine(
            "usage: rsyncwin -r [-t|-a] [-e ssh_path] SOURCE DEST  (exactly one of SOURCE/DEST must be " +
            "\"[user@]host:path\", \"rsync://[user@]host[:port]/module[/path]\", or \"[user@]host::module[/path]\")");
        PrintUsage();
        return (int)RsyncExitCode.SyntaxError;
    }

    string source = positionals[0];
    string dest = positionals[1];

    // Daemon endpoints (rsync:// or host::module) are detected BEFORE the ssh single-colon rule —
    // both daemon forms contain ':' characters ("rsync://" and "::") that the ssh rule would
    // otherwise misread as "[user@]host:path".
    bool sourceIsDaemon = TryParseDaemonEndpoint(source, out DaemonEndpoint? sourceDaemon, out string? sourceParseError);
    bool destIsDaemon = TryParseDaemonEndpoint(dest, out DaemonEndpoint? destDaemon, out string? destParseError);

    if (sourceParseError is not null || destParseError is not null)
    {
        Console.Error.WriteLine(sourceParseError ?? destParseError);
        PrintUsage();
        return (int)RsyncExitCode.SyntaxError;
    }

    if (sourceIsDaemon || destIsDaemon)
    {
        // Daemon-to-daemon, or daemon mixed with an ssh remote spec on the other side, is
        // remote-to-remote — rejected the same way ssh-to-ssh already is below.
        bool otherSideAlsoRemote = sourceIsDaemon
            ? destIsDaemon || dest.IndexOf(':') >= 0
            : source.IndexOf(':') >= 0;

        if (otherSideAlsoRemote)
        {
            Console.Error.WriteLine("rsyncwin: remote-to-remote transfers are not supported");
            PrintUsage();
            return (int)RsyncExitCode.SyntaxError;
        }

        return sourceIsDaemon
            ? await RunDaemonPullAsync(sourceDaemon!, dest, recurse, archive)
            : await RunDaemonPushAsync(source, destDaemon!, recurse, archive);
    }

    // rsync's rule: the FIRST ':' splits host from path. Same rule applied to both sides — whichever
    // one looks remote decides direction. (Known limitation, out of scope here: a Windows absolute
    // path like "C:\foo" also contains a ':', so this simple check cannot disambiguate a drive letter
    // from a remote host on the SOURCE side; P5's pull CLI already carried this same assumption.)
    bool sourceIsRemote = source.IndexOf(':') >= 0;
    bool destIsRemote = dest.IndexOf(':') >= 0;

    if (sourceIsRemote && !destIsRemote)
        return await RunPullAsync(source, dest, recurse, archive, rshOverride);
    if (destIsRemote && !sourceIsRemote)
        return await RunPushAsync(source, dest, recurse, archive, rshOverride);

    Console.Error.WriteLine(sourceIsRemote
        ? "rsyncwin: remote-to-remote transfers are not supported"
        : "rsyncwin: one side must be a remote path (\"[user@]host:path\") — local-to-local transfers are not supported");
    PrintUsage();
    return (int)RsyncExitCode.SyntaxError;
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
}

static async Task<int> RunPullAsync(string source, string dest, bool recurse, bool archive, string? rshOverride)
{
    (string? user, string host, string remotePath) = SplitRemoteSpec(source);

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
    };

    (OpenSshProcessTransport? transport, int startError) = StartSsh(user, host, serverArgs.Build(), rshOverride);
    if (transport is null)
        return startError;

    await using (transport)
    {
        PullSession.Result result;
        try
        {
            result = await PullSession.RunAsync(transport, serverArgs, dest);
        }
        catch (ProtocolException ex)
        {
            Console.Error.WriteLine($"rsyncwin: {ex.Message}");
            if (await TryGetSshExitCodeAsync(transport) == 255)
            {
                Console.Error.WriteLine(await transport.ReadAllStandardErrorAsync());
                return (int)RsyncExitCode.StartClientServerError;
            }
            return (int)ex.ExitCode;
        }
        catch (InvalidDataException ex)
        {
            // Codec/choreography desync — the wire stream is corrupt or out of sync.
            Console.Error.WriteLine($"rsyncwin: {ex.Message}");
            return (int)RsyncExitCode.ProtocolStreamError;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"rsyncwin: {ex.Message}");
            return (int)RsyncExitCode.FileIoError;
        }

        foreach (string name in result.MappedNames)
            Console.Error.WriteLine($"mapped: {name} -> {WindowsPathMapper.Map(name).Mapped}");
        foreach ((string name, string reason) in result.SkippedNonRegular)
            Console.Error.WriteLine($"skipping {reason}: {name}");
        Console.Error.WriteLine($"files transferred: {result.TransferredFiles}, bytes: {result.TransferredBytes}");

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

    (string? user, string host, string remotePath) = SplitRemoteSpec(dest);

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
        catch (ProtocolException ex)
        {
            Console.Error.WriteLine($"rsyncwin: {ex.Message}");
            if (await TryGetSshExitCodeAsync(transport) == 255)
            {
                Console.Error.WriteLine(await transport.ReadAllStandardErrorAsync());
                return (int)RsyncExitCode.StartClientServerError;
            }
            return (int)ex.ExitCode;
        }
        catch (InvalidDataException ex)
        {
            // Codec/choreography desync — the wire stream is corrupt or out of sync.
            Console.Error.WriteLine($"rsyncwin: {ex.Message}");
            return (int)RsyncExitCode.ProtocolStreamError;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"rsyncwin: {ex.Message}");
            return (int)RsyncExitCode.FileIoError;
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

/// <summary>
/// Recognizes the two daemon endpoint forms — BEFORE the ssh single-colon rule ever sees them, since
/// both "rsync://" and "::" contain ':' characters the ssh rule would otherwise misread.
/// </summary>
/// <param name="spec">The positional argument to classify.</param>
/// <param name="endpoint">The parsed endpoint on success; <c>null</c> otherwise.</param>
/// <param name="parseError">
/// <c>null</c> when <paramref name="spec"/> is not daemon-shaped at all (the caller should fall
/// through to the ssh single-colon rule) — including case (d): a "::" whose pre-"::" segment already
/// contains ':' (e.g. "host:path::x") is not recognized as a daemon endpoint here.
/// Non-null when <paramref name="spec"/> IS daemon-shaped ("rsync://" prefix, or a genuine "::") but
/// malformed (empty host, or an invalid port) — the caller should print this and exit with a syntax
/// error rather than silently misinterpreting the spec as something else or falling back to a
/// default port.
/// </param>
static bool TryParseDaemonEndpoint(string spec, out DaemonEndpoint? endpoint, out string? parseError)
{
    const string UrlPrefix = "rsync://";
    if (spec.StartsWith(UrlPrefix, StringComparison.Ordinal))
    {
        string rest = spec[UrlPrefix.Length..];
        int slashIndex = rest.IndexOf('/');
        string authority = slashIndex >= 0 ? rest[..slashIndex] : rest;
        string pathPart = slashIndex >= 0 ? rest[(slashIndex + 1)..] : "";

        int atIndex = authority.IndexOf('@');
        string? user = atIndex >= 0 ? authority[..atIndex] : null;
        string hostPort = atIndex >= 0 ? authority[(atIndex + 1)..] : authority;

        string host;
        string? portText;
        if (hostPort.StartsWith('['))
        {
            // IPv6 literal, e.g. "[::1]" or "[::1]:873" — the host itself may contain ':', so it must
            // be read out of the brackets before we ever look for a port-separating ':'.
            int closeBracket = hostPort.IndexOf(']');
            if (closeBracket < 0)
            {
                endpoint = null;
                parseError = $"rsyncwin: malformed daemon URL \"{spec}\" (unterminated IPv6 literal — missing ']')";
                return false;
            }

            host = hostPort[1..closeBracket];
            string afterBracket = hostPort[(closeBracket + 1)..];
            if (afterBracket.Length == 0)
                portText = null;
            else if (afterBracket[0] == ':')
                portText = afterBracket[1..];
            else
            {
                endpoint = null;
                parseError = $"rsyncwin: malformed daemon URL \"{spec}\" (unexpected text after \"]\")";
                return false;
            }
        }
        else
        {
            int colonIndex = hostPort.IndexOf(':');
            host = colonIndex >= 0 ? hostPort[..colonIndex] : hostPort;
            portText = colonIndex >= 0 ? hostPort[(colonIndex + 1)..] : null;
        }

        if (host.Length == 0)
        {
            endpoint = null;
            parseError = $"rsyncwin: daemon URL \"{spec}\" has no host";
            return false;
        }

        int port = DaemonTcpTransport.DefaultPort;
        if (portText is not null && (!int.TryParse(portText, out port) || port is < 1 or > 65535))
        {
            endpoint = null;
            parseError = $"rsyncwin: invalid daemon port \"{portText}\" in \"{spec}\" (must be 1-65535)";
            return false;
        }

        (string module, string modulePath) = SplitModulePath(pathPart);
        endpoint = new DaemonEndpoint(user, host, port, module, modulePath);
        parseError = null;
        return true;
    }

    // Double-colon form carries no port syntax — always the well-known daemon port.
    int doubleColonIndex = spec.IndexOf("::", StringComparison.Ordinal);
    if (doubleColonIndex >= 0)
    {
        string hostPart = spec[..doubleColonIndex];
        if (hostPart.Contains(':'))
        {
            // The "::" doesn't immediately follow a colon-free host (e.g. "host:path::x") — not a
            // daemon endpoint; fall through to the ssh single-colon rule.
            endpoint = null;
            parseError = null;
            return false;
        }

        string pathPart = spec[(doubleColonIndex + 2)..];

        int atIndex = hostPart.IndexOf('@');
        string? user = atIndex >= 0 ? hostPart[..atIndex] : null;
        string host = atIndex >= 0 ? hostPart[(atIndex + 1)..] : hostPart;

        if (host.Length == 0)
        {
            endpoint = null;
            parseError = $"rsyncwin: daemon spec \"{spec}\" has no host before \"::\"";
            return false;
        }

        (string module, string modulePath) = SplitModulePath(pathPart);
        endpoint = new DaemonEndpoint(user, host, DaemonTcpTransport.DefaultPort, module, modulePath);
        parseError = null;
        return true;
    }

    endpoint = null;
    parseError = null;
    return false;
}

/// <summary>Splits "module[/path...]" into (module, path-after-module) — "" for path when the
/// module is bare (e.g. "tree" or "tree/").</summary>
static (string Module, string ModulePath) SplitModulePath(string pathPart)
{
    if (pathPart.Length == 0)
        return ("", "");
    int slashIndex = pathPart.IndexOf('/');
    return slashIndex >= 0 ? (pathPart[..slashIndex], pathPart[(slashIndex + 1)..]) : (pathPart, "");
}

/// <summary>Composes the module-relative server path argument (docs/daemon-spec.md §3): the module
/// root itself is "module/"; a sub-path is appended verbatim after it (same trailing-slash-is-literal
/// convention as the ssh path handling).</summary>
static string ComposeServerPath(string module, string modulePath) =>
    modulePath.Length == 0 ? $"{module}/" : $"{module}/{modulePath}";

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

static async Task<int> RunDaemonPullAsync(DaemonEndpoint endpoint, string dest, bool recurse, bool archive)
{
    var serverArgs = new ServerArgvBuilder
    {
        Sender = true, // pull: the remote side sends
        Paths = [ComposeServerPath(endpoint.Module, endpoint.ModulePath)],
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

        PullSession.Result result;
        try
        {
            result = await PullSession.RunAsync(transport, serverArgs, dest, handshake: handshake);
        }
        catch (ProtocolException ex)
        {
            // In-mux MSG_ERROR_EXIT carries a real exit code — surfaced verbatim, never squashed to 12.
            Console.Error.WriteLine($"rsyncwin: {ex.Message}");
            return (int)ex.ExitCode;
        }
        catch (InvalidDataException ex)
        {
            // Codec/choreography desync — the wire stream is corrupt or out of sync.
            Console.Error.WriteLine($"rsyncwin: {ex.Message}");
            return (int)RsyncExitCode.ProtocolStreamError;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"rsyncwin: {ex.Message}");
            return (int)RsyncExitCode.FileIoError;
        }

        foreach (string name in result.MappedNames)
            Console.Error.WriteLine($"mapped: {name} -> {WindowsPathMapper.Map(name).Mapped}");
        foreach ((string name, string reason) in result.SkippedNonRegular)
            Console.Error.WriteLine($"skipping {reason}: {name}");
        Console.Error.WriteLine($"files transferred: {result.TransferredFiles}, bytes: {result.TransferredBytes}");

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
        Paths = [ComposeServerPath(endpoint.Module, endpoint.ModulePath)],
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
        catch (ProtocolException ex)
        {
            Console.Error.WriteLine($"rsyncwin: {ex.Message}");
            return (int)ex.ExitCode;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"rsyncwin: {ex.Message}");
            return (int)RsyncExitCode.ProtocolStreamError;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"rsyncwin: {ex.Message}");
            return (int)RsyncExitCode.FileIoError;
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
        catch (ProtocolException ex)
        {
            Console.Error.WriteLine($"rsyncwin: {ex.Message}");
            return (int)ex.ExitCode;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"rsyncwin: {ex.Message}");
            return (int)RsyncExitCode.ProtocolStreamError;
        }

        foreach (string line in result.MotdLines)
            Console.WriteLine(line);
        foreach (string line in result.ModuleLines)
            Console.WriteLine(line);

        return (int)RsyncExitCode.Ok;
    }
}

/// <summary>Splits a "[user@]host:path" remote spec on the FIRST ':' — rsync's rule, used for
/// whichever side (source for pull, dest for push) is the remote one.</summary>
static (string? User, string Host, string Path) SplitRemoteSpec(string spec)
{
    int colonIndex = spec.IndexOf(':');
    string hostPart = spec[..colonIndex];
    string remotePath = spec[(colonIndex + 1)..];
    int atIndex = hostPart.IndexOf('@');
    string? user = atIndex >= 0 ? hostPart[..atIndex] : null;
    string host = atIndex >= 0 ? hostPart[(atIndex + 1)..] : hostPart;
    return (user, host, remotePath);
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

/// <summary>A parsed daemon endpoint from either "rsync://[user@]host[:port]/module[/path]" or
/// "[user@]host::module[/path]". <see cref="Module"/> is empty for a bare endpoint (module listing).</summary>
record DaemonEndpoint(string? User, string Host, int Port, string Module, string ModulePath);
