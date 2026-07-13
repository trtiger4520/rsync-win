namespace RsyncWin.Fs;

/// <summary>
/// Defines how rsync wire names map to local paths and how local paths compare on the target
/// platform. The runtime policy follows the host OS; the named policies allow deterministic tests
/// without changing process-wide platform state.
/// </summary>
internal sealed class LocalPathPolicy
{
    private readonly Func<string, (string Mapped, bool Changed)> _map;

    private LocalPathPolicy(
        char directorySeparator,
        StringComparer pathComparer,
        StringComparison pathComparison,
        Func<string, (string Mapped, bool Changed)> map)
    {
        DirectorySeparator = directorySeparator;
        PathComparer = pathComparer;
        PathComparison = pathComparison;
        _map = map;
    }

    /// <summary>Windows/NTFS-compatible sanitization and case-insensitive path comparison.</summary>
    public static LocalPathPolicy Windows { get; } = new(
        '\\', StringComparer.OrdinalIgnoreCase, StringComparison.OrdinalIgnoreCase, WindowsPathMapper.Map);

    /// <summary>Unix-native wire names and case-sensitive ordinal path comparison.</summary>
    public static LocalPathPolicy Unix { get; } = new(
        '/', StringComparer.Ordinal, StringComparison.Ordinal, static name => (name, false));

    /// <summary>The policy for the current process platform.</summary>
    public static LocalPathPolicy Current => OperatingSystem.IsWindows() ? Windows : Unix;

    public char DirectorySeparator { get; }

    public StringComparer PathComparer { get; }

    public StringComparison PathComparison { get; }

    /// <summary>Maps a validated rsync wire name to a destination-relative local path.</summary>
    public (string Mapped, bool Changed) Map(string name) => _map(name);
}
