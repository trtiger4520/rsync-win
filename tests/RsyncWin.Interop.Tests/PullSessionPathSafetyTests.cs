using System.Text;
using RsyncWin.Engine;
using RsyncWin.Protocol.FileList;
using RsyncWin.Protocol.Session;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Hermetic: Windows-side path safety in <see cref="PullSession"/>. <c>FileListReader</c> rejects
/// the Unix shapes ('/'-rooted, '..' components) at decode time; these pin the Windows-only
/// escapes — '\' as a separator, drive and ADS colons — plus the mtime clamp that keeps a bogus
/// wire timestamp from throwing out of the session.
/// </summary>
public class PullSessionPathSafetyTests
{
    private static FileEntry Regular(string name) => new()
    {
        NameBytes = Encoding.UTF8.GetBytes(name),
        Mode = FileEntry.RegularFile | 0x1A4, // 0644
        Size = 0,
        ModifiedUnixSeconds = 0,
    };

    [Theory]
    [InlineData(@"..\..\evil.txt")]      // '\' traversal — invisible to the '/'-splitting validator
    [InlineData(@"subdir\..\..\evil.txt")]
    [InlineData(@"\Windows\evil.txt")]   // drive-absolute: Path.Combine discards the destination
    [InlineData(@"C:\Windows\evil.txt")] // fully rooted
    [InlineData("C:evil.txt")]           // drive-relative: resolves against that drive's CWD
    [InlineData("log.txt:hidden")]       // NTFS alternate data stream
    [InlineData("../evil.txt")]          // defense in depth: '..' the reader should already reject
    public void WindowsPathSyntax_IsRejected(string name)
    {
        var exception = Assert.Throws<ProtocolException>(
            () => PullSession.LocalPath(@"C:\dest", Regular(name)));
        Assert.Equal(RsyncExitCode.UnsupportedAction, exception.ExitCode);
    }

    [Fact]
    public void SafeNames_MapUnderTheDestination()
    {
        Assert.Equal(@"C:\dest", PullSession.LocalPath(@"C:\dest", Regular(".")));
        Assert.Equal(@"C:\dest\subdir\nested.txt",
            PullSession.LocalPath(@"C:\dest", Regular("subdir/nested.txt")));
        Assert.Equal(@"C:\dest\b005 name with space.txt",
            PullSession.LocalPath(@"C:\dest\", Regular("b005 name with space.txt")));
    }

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-62_135_596_801)] // below even DateTimeOffset's floor
    [InlineData(-11_644_473_600)] // exactly FILETIME 0
    [InlineData(long.MaxValue)]
    public void ExtremeMtimes_ClampToValuesTheFilesystemAccepts(long unixSeconds)
    {
        DateTime clamped = PullSession.ClampedMtimeUtc(unixSeconds);

        string path = Path.Combine(Path.GetTempPath(), $"rsyncwin-mtime-{Guid.NewGuid():N}");
        File.WriteAllBytes(path, []);
        try
        {
            File.SetLastWriteTimeUtc(path, clamped); // the Win32 acceptance gate — must not throw
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OrdinaryMtimes_PassThroughUnclamped()
    {
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1577934245).UtcDateTime,
            PullSession.ClampedMtimeUtc(1577934245));
    }
}
