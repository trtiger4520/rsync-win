using System.Text;
using RsyncWin.Transport;

namespace RsyncWin.Cli;

/// <summary>Which real invocation shape a parsed command line resolves to.</summary>
internal enum ParsedAction
{
    SshPull,
    SshPush,
    DaemonPull,
    DaemonPush,
    DaemonList,
    LocalCopy,
    ShowHelp,
}

/// <summary>A parsed daemon endpoint from either "rsync://[user@]host[:port]/module[/path]" or
/// "[user@]host::module[/path]". <see cref="Module"/> is empty for a bare endpoint (module listing).</summary>
internal sealed record DaemonEndpoint(string? User, string Host, int Port, string Module, string ModulePath);

/// <summary>A syntax/usage error to report before any I/O — always exit 1 (<c>RsyncExitCode.SyntaxError</c>),
/// since every rejection here is a parse-time classification, never a wire/session failure.
/// <see cref="ShowUsage"/> mirrors the existing convention: a genuine usage/shape error prints the
/// full usage text after the message; a specific flag-combination rejection does not (its message is
/// already self-explanatory).</summary>
internal sealed record ParseFailure(string Message, bool ShowUsage = false);

/// <summary>A fully classified, ready-to-dispatch command line — flags plus which of the
/// invocation shapes it resolves to. <see cref="Source"/>/<see cref="Dest"/> carry the raw local or
/// "[user@]host:path" spec for whichever side is relevant to <see cref="Action"/>; <see cref="Endpoint"/>
/// carries the parsed daemon endpoint for the daemon actions.</summary>
internal sealed record ParsedCommand(
    ParsedAction Action,
    bool Recurse,
    bool Archive,
    bool Checksum,
    bool Delete,
    bool Secluded,
    bool Compress,
    bool Progress,
    bool InfoProgress2,
    string? RshOverride,
    string? Source,
    string? Dest,
    DaemonEndpoint? Endpoint);

/// <summary>
/// Pure command-line classification: flag parsing, source/dest direction, and daemon-vs-ssh
/// detection — zero I/O, everything Program.cs needs to know before it may touch a socket or spawn
/// ssh.exe. Kept internal and unit-tested directly (RsyncWin.Cli.Tests, via InternalsVisibleTo).
/// </summary>
internal static class CommandLineParser
{
    // -h/--help wins over every other argument (rsync convention) — Parse returns this immediately,
    // so the flag values it carries are never read; only Action matters.
    private static readonly ParsedCommand HelpCommand = new(
        ParsedAction.ShowHelp, Recurse: false, Archive: false, Checksum: false, Delete: false,
        Secluded: false, Compress: false, Progress: false, InfoProgress2: false,
        RshOverride: null, Source: null, Dest: null, Endpoint: null);

    public static (ParsedCommand? Command, ParseFailure? Failure) Parse(string[] args)
    {
        string? rshOverride = null;
        bool recurse = false;
        bool archive = false;
        bool checksum = false;
        bool delete = false;
        bool secluded = false;
        bool compress = false;
        bool progress = false;
        bool infoProgress2 = false;
        var positionals = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg is "-e" or "--rsh")
            {
                if (++i >= args.Length)
                    return (null, new ParseFailure($"rsyncwin: {arg} requires an argument"));
                rshOverride = args[i];
            }
            else if (arg == "--recursive")
            {
                recurse = true;
            }
            else if (arg == "--archive")
            {
                archive = true;
            }
            else if (arg == "--times")
            {
                // PreserveTimes already defaults to true
            }
            else if (arg == "--checksum")
            {
                checksum = true;
            }
            else if (arg == "--delete")
            {
                delete = true;
            }
            else if (arg == "--help")
            {
                return (HelpCommand, null);
            }
            else if (arg is "-s" or "--secluded-args" or "--protect-args")
            {
                secluded = true;
            }
            else if (arg is "-z" or "--compress")
            {
                compress = true;
            }
            else if (arg == "--progress")
            {
                progress = true;
            }
            else if (arg.StartsWith("--info=", StringComparison.Ordinal))
            {
                // Only the progress info flags are supported (a client-local display concern, never
                // forwarded to the server). rsync's --info is a comma list of FLAG[LEVEL]; we accept
                // progress/progress1 (per-file) and progress2 (whole-transfer) and reject the rest
                // rather than silently ignoring an unimplemented info category.
                foreach (string item in arg["--info=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    switch (item)
                    {
                        case "progress2": infoProgress2 = true; break;
                        case "progress" or "progress1": progress = true; break;
                        default:
                            return (null, new ParseFailure($"rsyncwin: unsupported --info flag \"{item}\" (only progress/progress2)"));
                    }
                }
            }
            else if (arg.Length > 2 && arg[0] == '-' && arg[1] == '-')
            {
                // Any other long flag is unrecognized — reject explicitly instead of falling through
                // to positionals, where it used to produce a confusing "wrong number of arguments" error.
                return (null, new ParseFailure($"rsyncwin: unsupported option {arg}"));
            }
            else if (arg.Length > 1 && arg[0] == '-' && arg[1] != '-')
            {
                // Bundled short options, e.g. -r, -rt, -a, -rtc — only flags ServerArgvBuilder already supports.
                foreach (char flag in arg[1..])
                {
                    switch (flag)
                    {
                        case 'r': recurse = true; break;
                        case 't': break; // PreserveTimes already defaults to true
                        case 'a': archive = true; break;
                        case 'c': checksum = true; break;
                        case 's': secluded = true; break;
                        case 'z': compress = true; break;
                        case 'h': return (HelpCommand, null); // -h is help; --human-readable is not implemented

                        default:
                            return (null, new ParseFailure($"rsyncwin: unsupported option -{flag}"));
                    }
                }
            }
            else
            {
                positionals.Add(arg);
            }
        }

        // rsync refuses --delete without recursion — checked once here regardless of direction.
        if (delete && !recurse && !archive)
            return (null, new ParseFailure("rsyncwin: --delete requires -r or -a (rsync refuses to delete without recursion)"));

        // Daemon module listing is the one case with a single positional: a bare daemon endpoint
        // ("rsync://host[:port]/", "rsync://host[:port]" with no path, or "host::") whose module is
        // empty lists the daemon's modules instead of transferring anything.
        if (positionals.Count == 1)
        {
            if (!TryParseDaemonEndpoint(positionals[0], out DaemonEndpoint? listEndpoint, out string? listParseError))
            {
                if (listParseError is not null)
                    return (null, new ParseFailure(listParseError, ShowUsage: true));
            }
            else if (listEndpoint!.Module.Length == 0)
            {
                return (new ParsedCommand(ParsedAction.DaemonList, recurse, archive, checksum, delete, secluded, compress, progress, infoProgress2, rshOverride, null, null, listEndpoint), null);
            }
        }

        if (positionals.Count != 2)
        {
            return (null, new ParseFailure(
                "rsyncwin: expected exactly two arguments (SOURCE and DEST)",
                ShowUsage: true));
        }

        string source = positionals[0];
        string dest = positionals[1];

        // Daemon endpoints (rsync:// or host::module) are detected BEFORE the ssh single-colon rule —
        // both daemon forms contain ':' characters ("rsync://" and "::") that the ssh rule would
        // otherwise misread as "[user@]host:path".
        bool sourceIsDaemon = TryParseDaemonEndpoint(source, out DaemonEndpoint? sourceDaemon, out string? sourceParseError);
        bool destIsDaemon = TryParseDaemonEndpoint(dest, out DaemonEndpoint? destDaemon, out string? destParseError);

        if (sourceParseError is not null || destParseError is not null)
            return (null, new ParseFailure(sourceParseError ?? destParseError!, ShowUsage: true));

        if (sourceIsDaemon || destIsDaemon)
        {
            // Daemon-to-daemon, or daemon mixed with an ssh remote spec on the other side, is
            // remote-to-remote — rejected the same way ssh-to-ssh already is below.
            bool otherSideAlsoRemote = sourceIsDaemon
                ? destIsDaemon || IsRemoteSpec(dest)
                : IsRemoteSpec(source);

            if (otherSideAlsoRemote)
                return (null, new ParseFailure("rsyncwin: remote-to-remote transfers are not supported", ShowUsage: true));

            if (sourceIsDaemon)
            {
                return (new ParsedCommand(ParsedAction.DaemonPull, recurse, archive, checksum, delete, secluded, compress, progress, infoProgress2, rshOverride, null, dest, sourceDaemon), null);
            }

            return (new ParsedCommand(ParsedAction.DaemonPush, recurse, archive, checksum, delete, secluded, compress, progress, infoProgress2, rshOverride, source, null, destDaemon), null);
        }

        // rsync's rule: the FIRST ':' splits host from path, applied to both sides — whichever one
        // looks remote decides direction. IsRemoteSpec additionally guards against misreading a
        // Windows drive-letter or UNC path (e.g. "D:\backup", "\\server\share") as a remote host.
        bool sourceIsRemote = IsRemoteSpec(source);
        bool destIsRemote = IsRemoteSpec(dest);

        if (sourceIsRemote && !destIsRemote)
            return (new ParsedCommand(ParsedAction.SshPull, recurse, archive, checksum, delete, secluded, compress, progress, infoProgress2, rshOverride, source, dest, null), null);

        if (destIsRemote && !sourceIsRemote)
            return (new ParsedCommand(ParsedAction.SshPush, recurse, archive, checksum, delete, secluded, compress, progress, infoProgress2, rshOverride, source, dest, null), null);

        if (sourceIsRemote)
            return (null, new ParseFailure("rsyncwin: remote-to-remote transfers are not supported", ShowUsage: true));

        // Neither side is remote: local-to-local direct copy (no wire, no transport).
        return (new ParsedCommand(ParsedAction.LocalCopy, recurse, archive, checksum, delete, secluded, compress, progress, infoProgress2, rshOverride, source, dest, null), null);
    }

    /// <summary>
    /// True when <paramref name="spec"/> looks like a remote "[user@]host:path" spec rather than a
    /// local path. Plain <c>spec.IndexOf(':') >= 0</c> misreads a Windows drive-letter path
    /// ("D:\backup", "D:/backup", "D:") as remote — <see cref="IsLocalWindowsPath"/> guards against
    /// that specific case.
    /// </summary>
    internal static bool IsRemoteSpec(string spec) => spec.IndexOf(':') >= 0 && !IsLocalWindowsPath(spec);

    /// <summary>
    /// True for a Windows drive-letter path ("D:", "D:\backup", "D:/backup" — first char an ASCII
    /// letter, second char ':', and either nothing after it or a path separator) or a UNC path
    /// ("\\server\share..."). Never true for a genuine "[user@]host:path" or "host:path", since a
    /// real hostname is not a single letter immediately followed by ':' and a separator (or nothing).
    /// </summary>
    internal static bool IsLocalWindowsPath(string spec)
    {
        if (spec.Length >= 2
            && spec[0] is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z')
            && spec[1] == ':'
            && (spec.Length == 2 || spec[2] is '\\' or '/'))
        {
            return true;
        }

        return spec.StartsWith(@"\\", StringComparison.Ordinal);
    }

    /// <summary>Splits a "[user@]host:path" remote spec on the FIRST ':' — rsync's rule, used for
    /// whichever side (source for pull, dest for push) is the remote one.</summary>
    internal static (string? User, string Host, string Path) SplitRemoteSpec(string spec)
    {
        int colonIndex = spec.IndexOf(':');
        string hostPart = spec[..colonIndex];
        string remotePath = spec[(colonIndex + 1)..];
        int atIndex = hostPart.IndexOf('@');
        string? user = atIndex >= 0 ? hostPart[..atIndex] : null;
        string host = atIndex >= 0 ? hostPart[(atIndex + 1)..] : hostPart;
        return (user, host, remotePath);
    }

    /// <summary>
    /// Splits a remote-shell command (from <c>-e</c>/<c>--rsh</c> or <c>RSYNC_RSH</c>) into its words
    /// the way rsync does: the first word is the program to launch, the rest are arguments inserted
    /// before the host. Whitespace separates words; single or double quotes group a word (and are
    /// stripped) so a path containing spaces survives; a backslash is a literal character, NOT an
    /// escape — Windows paths are full of them and treating <c>\</c> as an escape would corrupt every
    /// path. An all-whitespace or empty command yields an empty list (the caller falls back to the
    /// in-box ssh.exe).
    /// </summary>
    internal static IReadOnlyList<string> SplitRsh(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        bool inToken = false;
        char quote = '\0'; // '\0' = outside quotes; otherwise the active quote character

        foreach (char c in command)
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0'; // closing quote
                else
                    current.Append(c);
                inToken = true; // even "" is a token
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                inToken = true;
            }
            else if (c is ' ' or '\t')
            {
                if (inToken)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                    inToken = false;
                }
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }

        if (inToken)
            tokens.Add(current.ToString());

        return tokens;
    }

    /// <summary>Composes the module-relative server path argument (docs/daemon-spec.md §3): the module
    /// root itself is "module/"; a sub-path is appended verbatim after it (same trailing-slash-is-literal
    /// convention as the ssh path handling).</summary>
    internal static string ComposeServerPath(string module, string modulePath) =>
        modulePath.Length == 0 ? $"{module}/" : $"{module}/{modulePath}";

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
    internal static bool TryParseDaemonEndpoint(string spec, out DaemonEndpoint? endpoint, out string? parseError)
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
    private static (string Module, string ModulePath) SplitModulePath(string pathPart)
    {
        if (pathPart.Length == 0)
            return ("", "");
        int slashIndex = pathPart.IndexOf('/');
        return slashIndex >= 0 ? (pathPart[..slashIndex], pathPart[(slashIndex + 1)..]) : (pathPart, "");
    }
}
