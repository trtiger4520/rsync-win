using System.Text;
using RsyncWin.Engine;
using RsyncWin.Protocol.FileList;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Hermetic: a local file's mtime is always written via <see cref="PullSession.ClampedMtimeUtc"/>,
/// so an out-of-range wire mtime must be compared against the <em>clamped</em> value in the
/// mtime+size fast path, not the raw one — otherwise a local file that is genuinely up to date
/// never matches and gets re-requested forever.
/// </summary>
/// <remarks>
/// Uses <see cref="long.MinValue"/> rather than <see cref="long.MaxValue"/>: the latter clamps to
/// the year-9999 ceiling, and reading it back via <see cref="FileInfo.LastWriteTimeUtc"/> throws
/// <see cref="ArgumentException"/> on hosts with a positive UTC offset (e.g. UTC+8) — a distinct,
/// pre-existing platform quirk unrelated to this fix, out of scope for these four findings.
/// </remarks>
[Trait("Category", "WindowsFs")]
public class PullSessionMtimeClampTests
{
    private static FileEntry Regular(string name, long modifiedUnixSeconds, long size) => new()
    {
        NameBytes = Encoding.UTF8.GetBytes(name),
        Mode = FileEntry.RegularFile | 0x1A4, // 0644
        Size = size,
        ModifiedUnixSeconds = modifiedUnixSeconds,
    };

    [Fact]
    public void OutOfRangeMtime_MatchesTheClampedLocalMtime_FastPathSkips()
    {
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-mtimeclamp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dest);
        try
        {
            FileEntry entry = Regular("b.txt", long.MinValue, size: 0);
            string path = PullSession.LocalPath(dest, entry);
            File.WriteAllBytes(path, []);
            File.SetLastWriteTimeUtc(path, PullSession.ClampedMtimeUtc(long.MinValue));

            bool needsRequest = PullSession.TryComputeRequestIflags(entry, dest, out ItemFlags iflags);

            Assert.False(needsRequest);
            Assert.Equal(ItemFlags.None, iflags);
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public void OutOfRangeMtime_StillRequestsWhenSizeDiffers()
    {
        // Sanity check against the fix being a no-op: an actual size mismatch must still trigger
        // a request even when the (clamped) mtime matches.
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-mtimeclamp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dest);
        try
        {
            FileEntry entry = Regular("b.txt", long.MinValue, size: 5);
            string path = PullSession.LocalPath(dest, entry);
            File.WriteAllBytes(path, []); // 0 bytes locally, entry claims size 5
            File.SetLastWriteTimeUtc(path, PullSession.ClampedMtimeUtc(long.MinValue));

            bool needsRequest = PullSession.TryComputeRequestIflags(entry, dest, out ItemFlags iflags);

            Assert.True(needsRequest);
            Assert.Equal(ItemFlags.Transfer | ItemFlags.ReportSize, iflags);
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }
}
