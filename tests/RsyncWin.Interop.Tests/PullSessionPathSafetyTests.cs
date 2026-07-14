using System.Text;
using RsyncWin.Engine;
using RsyncWin.Fs;
using RsyncWin.Protocol.FileList;
using RsyncWin.Protocol.Session;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Hermetic: Windows-side path safety in <see cref="PullSession"/>. <c>FileListReader</c> rejects
/// the Unix shapes ('/'-rooted, '..' components) at decode time; these pin the Windows-only
/// escapes — '\' as a separator, drive and ADS colons — being sanitized by
/// <see cref="RsyncWin.Fs.WindowsPathMapper"/> rather than rejected, plus the containment backstop
/// and the mtime clamp that keeps a bogus wire timestamp from throwing out of the session.
/// </summary>
[Trait("Category", "WindowsFs")]
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
    [InlineData(@"..\..\evil.txt", @"C:\dest\.._.._evil.txt")]           // '\' sanitized, one segment
    [InlineData(@"subdir\..\..\evil.txt", @"C:\dest\subdir_.._.._evil.txt")]
    [InlineData(@"\Windows\evil.txt", @"C:\dest\_Windows_evil.txt")]     // drive-absolute backslash
    [InlineData(@"C:\Windows\evil.txt", @"C:\dest\C__Windows_evil.txt")] // fully rooted
    [InlineData("C:evil.txt", @"C:\dest\C_evil.txt")]                   // drive-relative colon
    [InlineData("log.txt:hidden", @"C:\dest\log.txt_hidden")]           // NTFS alternate data stream
    public void WindowsPathSyntax_IsSanitized(string name, string expected)
    {
        Assert.Equal(expected, PullSession.LocalPath(@"C:\dest", Regular(name)));
    }

    [Fact]
    public void BareParentSegment_StillEscapesAndThrows()
    {
        // Defense in depth: a bare ".." component split on '/' should already be rejected by
        // FileListReader, but the mapper intentionally leaves "." and ".." untouched (they are
        // navigation tokens, not names to sanitize) so the containment check below is the real
        // backstop if that rejection is ever bypassed.
        var exception = Assert.Throws<ProtocolException>(
            () => PullSession.LocalPath(@"C:\dest", Regular("../evil.txt")));
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

    [Fact]
    public void ExplicitUnixPolicy_DoesNotSanitizeLegalUnixNames()
    {
        string destination = Path.Combine(Path.GetTempPath(), "rsyncwin-unix-policy");
        string expected = Path.GetFullPath(Path.Combine(destination, "CON:a\\b"));

        string actual = PullSession.LocalPath(
            destination, Regular("CON:a\\b"), LocalPathPolicy.Unix, out bool changed);

        Assert.Equal(expected, actual);
        Assert.False(changed);
    }

    [Fact]
    public void ExplicitUnixPolicy_StillRejectsDestinationTraversal()
    {
        string destination = Path.Combine(Path.GetTempPath(), "rsyncwin-unix-policy");

        var exception = Assert.Throws<ProtocolException>(
            () => PullSession.LocalPath(
                destination, Regular("../evil.txt"), LocalPathPolicy.Unix, out _));

        Assert.Equal(RsyncExitCode.UnsupportedAction, exception.ExitCode);
    }

    [Fact]
    public void HostileNames_CreateOnlyMappedFilesUnderTheActualDestination()
    {
        string destination = Path.Combine(Path.GetTempPath(), $"rsyncwin-pathsafety-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destination);
        try
        {
            string[] names = [
                @"..\..\evil.txt",
                @"C:\Windows\evil.txt",
                "log.txt:hidden",
                "CON.txt",
                "trailing.dot.",
            ];

            string root = Path.GetFullPath(destination);
            string rootWithSeparator = Path.EndsInDirectorySeparator(root)
                ? root
                : root + Path.DirectorySeparatorChar;

            foreach (string name in names)
            {
                string path = PullSession.LocalPath(destination, Regular(name));
                Assert.StartsWith(rootWithSeparator, path, StringComparison.OrdinalIgnoreCase);
                File.WriteAllText(path, "mapped");
            }

            foreach (string path in Directory.GetFiles(destination, "*", SearchOption.AllDirectories))
                Assert.StartsWith(rootWithSeparator, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(destination, recursive: true);
        }
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
