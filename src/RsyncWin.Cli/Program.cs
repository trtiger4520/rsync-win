using System.ComponentModel;
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

    if (positionals.Count != 2)
    {
        Console.Error.WriteLine(
            "usage: rsyncwin -r [-t|-a] [-e ssh_path] SOURCE DEST  (exactly one of SOURCE/DEST must be \"[user@]host:path\")");
        return (int)RsyncExitCode.SyntaxError;
    }

    string source = positionals[0];
    string dest = positionals[1];

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
    Console.Error.WriteLine("usage: rsyncwin -r [-t|-a] [-e ssh_path] [user@]host:path localdir   (pull)");
    Console.Error.WriteLine("       rsyncwin -r [-t] [-e ssh_path] localdir [user@]host:path      (push; -a not yet)");
    return (int)RsyncExitCode.SyntaxError;
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
