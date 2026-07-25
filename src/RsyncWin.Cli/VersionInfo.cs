using System.Reflection;

namespace RsyncWin.Cli;

/// <summary>
/// The product version shown by <c>-V</c>/<c>--version</c>, read from the assembly rather than a
/// hand-maintained constant: git tags are the single source of truth for versions, and CI passes the
/// computed version through <c>-p:Version=</c>, which MSBuild turns into
/// <see cref="AssemblyInformationalVersionAttribute"/>. Hand-bumping anything here would diverge from
/// the released tag.
/// </summary>
internal static class VersionInfo
{
    /// <summary>Shown when neither the informational version nor the assembly version is readable —
    /// the version line still prints rather than crashing the one command whose job is to report it.</summary>
    private const string Unknown = "unknown";

    /// <summary>The version string without build metadata. SourceLink appends "+&lt;commit sha&gt;" to
    /// the informational version; everything from the '+' on is stripped so the line reads like
    /// rsync's own.</summary>
    public static string Product { get; } = Resolve();

    private static string Resolve()
    {
        Assembly assembly = typeof(VersionInfo).Assembly;

        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plusIndex = informational.IndexOf('+');
            string trimmed = plusIndex >= 0 ? informational[..plusIndex] : informational;
            if (trimmed.Length > 0)
                return trimmed;
        }

        // Fallback: AssemblyVersion is always emitted, but it is the 4-part numeric form and drops
        // any prerelease suffix — good enough to identify a build, never the preferred source.
        return assembly.GetName().Version?.ToString() ?? Unknown;
    }
}
