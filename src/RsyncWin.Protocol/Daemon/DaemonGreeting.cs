namespace RsyncWin.Protocol.Daemon;

/// <summary>
/// The <c>@RSYNCD: &lt;ver&gt;[.&lt;sub&gt;][ &lt;digest list&gt;]</c> line exchanged at the very
/// start of a daemon connection, in both directions, before any binary protocol byte.
/// </summary>
/// <remarks>
/// MEASURED (daemon31-pull-rt): server sends <c>@RSYNCD: 32.0 md5 md4\n</c>; a client negotiating
/// protocol 31 replies <c>@RSYNCD: 31.0 md5 md4\n</c>. The digest list is unrelated to the in-band
/// checksum vstrings exchanged after <c>OK</c> — it is purely the daemon-auth digest preference
/// (docs/daemon-spec.md §1-2). Older daemons may omit the digest list entirely.
/// </remarks>
public static class DaemonGreeting
{
    private const string Prefix = "@RSYNCD: ";

    /// <summary>Digests we advertise in the client greeting, matching stock rsync 3.4.3.</summary>
    private const string ClientDigests = "md5 md4";

    /// <summary>Parses one greeting line (no trailing newline).</summary>
    /// <exception cref="InvalidDataException">The line does not start with <c>@RSYNCD: </c>, or the
    /// version field is not a valid integer.</exception>
    public static (int Version, int Subversion, IReadOnlyList<string> Digests) Parse(string line)
    {
        if (!line.StartsWith(Prefix, StringComparison.Ordinal))
            throw new InvalidDataException($"daemon greeting: line does not start with \"{Prefix}\": \"{line}\"");

        string rest = line[Prefix.Length..];

        // The version/subversion field ends at the first space (if a digest list follows) or the
        // end of the line (older daemons with no digest list).
        int spaceIndex = rest.IndexOf(' ');
        string versionField = spaceIndex < 0 ? rest : rest[..spaceIndex];

        int version;
        int subversion = 0;
        int dot = versionField.IndexOf('.');
        if (dot < 0)
        {
            if (!int.TryParse(versionField, out version))
                throw new InvalidDataException($"daemon greeting: bad version field \"{versionField}\"");
        }
        else
        {
            if (!int.TryParse(versionField[..dot], out version)
                || !int.TryParse(versionField[(dot + 1)..], out subversion))
                throw new InvalidDataException($"daemon greeting: bad version field \"{versionField}\"");
        }

        IReadOnlyList<string> digests = spaceIndex < 0
            ? []
            : rest[(spaceIndex + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return (version, subversion, digests);
    }

    /// <summary>Formats the client greeting we send back, advertising <paramref name="version"/>.</summary>
    public static string FormatClient(int version) => $"{Prefix}{version}.0 {ClientDigests}\n";
}
