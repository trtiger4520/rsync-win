using RsyncWin.Fs;

namespace RsyncWin.Fs.Tests;

/// <summary>The shared temp-name/replace/mtime-clamp helpers extracted from PullSession's
/// receiver — behavior is additionally pinned by the PullSession replay tests that ride on them.</summary>
[Trait("Category", "WindowsFs")]
public class DestinationReplacerTests
{
    [Fact]
    public void TempPathFor_IsASiblingWithTheTmpSuffix()
    {
        string tempPath = DestinationReplacer.TempPathFor(@"C:\some\dir\file.txt");

        Assert.Equal(@"C:\some\dir", Path.GetDirectoryName(tempPath));
        string name = Path.GetFileName(tempPath);
        Assert.StartsWith(".file.txt.", name);
        Assert.EndsWith(".rsyncwin-tmp", name);
    }

    [Fact]
    public void FinalizeReplace_OverwritesReadOnlyDestinationAndRestoresMtime()
    {
        string root = WindowsFsTestSupport.CreateTempDirectory("rsyncwin-replace");
        try
        {
            string finalPath = Path.Combine(root, "target.txt");
            File.WriteAllText(finalPath, "old");
            File.SetAttributes(finalPath, FileAttributes.ReadOnly);

            string tempPath = DestinationReplacer.TempPathFor(finalPath);
            File.WriteAllText(tempPath, "new");
            var mtime = new DateTime(2023, 7, 8, 9, 10, 11, DateTimeKind.Utc);

            DestinationReplacer.FinalizeReplace(tempPath, finalPath, mtime);

            Assert.Equal("new", File.ReadAllText(finalPath));
            Assert.Equal(mtime, File.GetLastWriteTimeUtc(finalPath));
            Assert.False(File.Exists(tempPath));
        }
        finally
        {
            WindowsFsTestSupport.DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(long.MinValue, -11_644_473_599)] // clamps up to FILETIME's first settable second
    [InlineData(long.MaxValue, 253_402_300_799)] // clamps down to DateTimeOffset's ceiling
    [InlineData(1_600_000_000, 1_600_000_000)]   // in-range passes through
    public void ClampedMtimeUtc_ClampsToTheSettableWindow(long unixSeconds, long expectedUnixSeconds)
    {
        DateTime clamped = DestinationReplacer.ClampedMtimeUtc(unixSeconds);

        Assert.Equal(expectedUnixSeconds, new DateTimeOffset(clamped).ToUnixTimeSeconds());
    }
}
