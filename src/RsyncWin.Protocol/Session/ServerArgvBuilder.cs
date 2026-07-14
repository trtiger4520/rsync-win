using System.Text;

namespace RsyncWin.Protocol.Session;

/// <summary>
/// Builds the argv of the remote <c>rsync --server</c> command. The SSH transport passes these words
/// to <c>ssh.exe</c>; the daemon transport sends the same line over the socket after module selection.
/// </summary>
/// <remarks>
/// Golden-pinned against the argv a real rsync 3.4.3 client produced for the same option sets
/// (<c>vectors/*/argv.txt</c>) — e.g. a plain <c>-tr</c> pull at protocol 31 must yield exactly
/// <c>rsync --server --sender -tre.LsfxCIvu . /t/tree/</c>.
/// </remarks>
public sealed record ServerArgvBuilder
{
    /// <summary>
    /// Capability letters appended after <c>-e.</c> at protocol ≥ 30 — this is rsync's
    /// <c>client_info</c> string, which the server echoes back bit-for-bit as compat_flags:
    /// L=symlink times, s=symlink iconv, f=safe flist, x=avoid xattr optim, C=checksum-seed fix,
    /// I=inplace partial dir, v=varint flist flags + string negotiation, u=id0 names.
    /// Deliberately identical to what stock rsync 3.4.3 sends (minus <c>i</c>: we never do
    /// incremental recursion), so every captured golden vector stays valid for our own sessions.
    /// </summary>
    public const string ClientInfo = "LsfxCIvu";

    /// <summary>True for a pull (the remote side sends); false for a push (it receives).</summary>
    public required bool Sender { get; init; }

    /// <summary>Remote paths, placed after the lone <c>.</c> separator argument.</summary>
    public required IReadOnlyList<string> Paths { get; init; }

    /// <summary>The protocol we will advertise; below 30 the capability letters are meaningless and omitted.</summary>
    public int Protocol { get; init; } = RsyncConstants.ProtocolVersion;

    public bool Recurse { get; init; }
    public bool PreserveTimes { get; init; } = true;
    public bool PreserveLinks { get; init; }
    public bool PreserveOwner { get; init; }
    public bool PreserveGroup { get; init; }
    public bool PreserveDevices { get; init; }
    public bool PreservePerms { get; init; }

    /// <summary>Emit <c>-c</c> (<c>--checksum</c>): the server includes a per-file whole-file checksum in the flist and both sides compare by content, not mtime+size.</summary>
    public bool Checksum { get; init; }
    public bool ListOnly { get; init; }

    public IReadOnlyList<string> Build()
    {
        List<string> args = ["rsync", "--server"];
        if (Sender)
            args.Add("--sender");

        // Short options bundled in rsync's server_options() emission order — pinned by the argv
        // goldens: 'e' rides last in the bundle because it carries the capability value.
        var bundle = new StringBuilder("-");
        if (PreserveLinks) bundle.Append('l');
        if (PreserveOwner) bundle.Append('o');
        if (PreserveGroup) bundle.Append('g');
        if (PreserveDevices) bundle.Append('D');
        if (PreserveTimes) bundle.Append('t');
        if (PreservePerms) bundle.Append('p');
        if (Recurse) bundle.Append('r');
        if (Checksum) bundle.Append('c');
        if (Protocol >= 30) bundle.Append("e.").Append(ClientInfo);
        if (bundle.Length > 1)
            args.Add(bundle.ToString());

        if (ListOnly)
            args.Add("--list-only");

        args.Add(".");
        args.AddRange(Paths);
        return args;
    }
}
