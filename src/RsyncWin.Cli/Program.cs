using System.ComponentModel;
using RsyncWin.Engine;
using RsyncWin.Fs;
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
        Console.Error.WriteLine("usage: rsyncwin -r [-t|-a] [-e ssh_path] [user@]host:path dest");
        return (int)RsyncExitCode.SyntaxError;
    }

    string source = positionals[0];
    string dest = positionals[1];

    // rsync's rule: the FIRST ':' splits host from path. No ':' means a local source, which pull
    // does not support yet (push is out of scope for this CLI).
    int colonIndex = source.IndexOf(':');
    if (colonIndex < 0)
    {
        Console.Error.WriteLine(
            "rsyncwin: source must be a remote path (\"[user@]host:path\") — local sources are not supported");
        return (int)RsyncExitCode.SyntaxError;
    }

    string hostPart = source[..colonIndex];
    string remotePath = source[(colonIndex + 1)..];
    int atIndex = hostPart.IndexOf('@');
    string? user = atIndex >= 0 ? hostPart[..atIndex] : null;
    string host = atIndex >= 0 ? hostPart[(atIndex + 1)..] : hostPart;

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

    var sshArgs = new List<string>();
    if (user is not null)
    {
        sshArgs.Add("-l");
        sshArgs.Add(user);
    }
    sshArgs.Add(host);
    sshArgs.AddRange(serverArgs.Build());

    string sshExe = rshOverride ?? OpenSshProcessTransport.DefaultSshExePath;

    OpenSshProcessTransport transport;
    try
    {
        transport = OpenSshProcessTransport.Start(sshExe, sshArgs);
    }
    catch (Win32Exception ex)
    {
        Console.Error.WriteLine($"rsyncwin: failed to start \"{sshExe}\": {ex.Message}");
        return (int)RsyncExitCode.StartClientServerError;
    }

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
