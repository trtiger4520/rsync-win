using RsyncWin.Cli;

namespace RsyncWin.Cli.Tests;

/// <summary>The version string shown by -V/--version. It comes from the assembly (CI's computed
/// version), so these pin its shape rather than a literal value.</summary>
public class VersionInfoTests
{
    [Fact]
    public void Product_IsNotEmptyOrUnknown()
    {
        Assert.False(string.IsNullOrWhiteSpace(VersionInfo.Product));
        Assert.NotEqual("unknown", VersionInfo.Product);
    }

    /// <summary>SourceLink appends "+&lt;commit sha&gt;" to the informational version; the build
    /// metadata must be stripped before it reaches the version line.</summary>
    [Fact]
    public void Product_HasNoBuildMetadata()
    {
        Assert.DoesNotContain('+', VersionInfo.Product);
    }

    [Fact]
    public void Product_StartsWithMajorMinorPatch()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+", VersionInfo.Product);
    }
}
