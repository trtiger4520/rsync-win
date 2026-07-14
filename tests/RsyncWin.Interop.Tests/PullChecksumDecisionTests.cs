using System.Text;
using RsyncWin.Engine;
using RsyncWin.Protocol.Checksums;
using RsyncWin.Protocol.FileList;
using RsyncWin.Protocol.Session;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Hermetic: exercises <see cref="PullSession.ComputeChecksumDecisionAsync"/> directly against the
/// <c>--checksum</c> (<c>-c</c>) decision table pinned by capture (<c>ssh31-pull-checksum</c>,
/// docs/wire-notes.md P9): under <c>-c</c> the mtime+size fast path is replaced by a whole-file
/// content compare against <see cref="FileEntry.FlistChecksum"/>. Every expected checksum is
/// computed in-test via <see cref="StrongChecksum.CreateFileSum"/> over known content, so this stays
/// self-contained rather than depending on a fixture's exact bytes.
/// </summary>
public class PullChecksumDecisionTests
{
    private static readonly SessionContext Session = new()
    {
        Protocol = 31,
        CompatFlags = 0,
        ChecksumSeed = 0,
        TransferChecksum = ChecksumAlgorithm.XxHash128,
        FileChecksum = ChecksumAlgorithm.XxHash128,
    };

    private static readonly DateTimeOffset EntryMtime = new(2021, 6, 15, 12, 0, 0, TimeSpan.Zero);

    private static FileEntry Regular(string name, long size, long mtimeUnixSeconds, byte[]? flistChecksum) => new()
    {
        NameBytes = Encoding.UTF8.GetBytes(name),
        Mode = FileEntry.RegularFile | 0x1A4, // 0644
        Size = size,
        ModifiedUnixSeconds = mtimeUnixSeconds,
        FlistChecksum = flistChecksum,
    };

    private static byte[] WholeFileChecksumOf(byte[] content)
    {
        WholeFileChecksum hasher = StrongChecksum.CreateFileSum(ChecksumAlgorithm.XxHash128, seed: 0, protocol: 31);
        hasher.Append(content);
        byte[] digest = new byte[16];
        int length = hasher.Finish(digest);
        return digest[..length];
    }

    [Fact]
    public async Task MissingLocally_IsTransfer_IsNew_ReportChange()
    {
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-checksumdecision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dest);
        try
        {
            byte[] content = "brand new file\n"u8.ToArray();
            FileEntry entry = Regular("c_new.txt", content.Length, EntryMtime.ToUnixTimeSeconds(), WholeFileChecksumOf(content));
            string path = Path.Combine(dest, "c_new.txt"); // never created — missing locally

            (PullSession.ChecksumOutcome outcome, ItemFlags iflags) =
                await PullSession.ComputeChecksumDecisionAsync(entry, path, Session, CancellationToken.None);

            Assert.Equal(PullSession.ChecksumOutcome.Transfer, outcome);
            Assert.Equal(ItemFlags.Transfer | ItemFlags.IsNew | ItemFlags.ReportChange, iflags);
            Assert.Equal((ItemFlags)0xA002, iflags);
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public async Task ContentDiffers_SameSizeAndMtime_IsTransfer_ReportChangeOnly()
    {
        // b_fast: same size+mtime as the flist entry but different bytes — the plain mtime+size
        // fast path would skip this file; -c must still transfer it, with neither ReportSize nor
        // ReportTime since those fields genuinely match.
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-checksumdecision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dest);
        try
        {
            byte[] srcContent = new byte[65536];
            Array.Fill(srcContent, (byte)0xC1);
            byte[] localContent = new byte[65536];
            Array.Fill(localContent, (byte)0xC2);

            FileEntry entry = Regular("b_fast.bin", srcContent.Length, EntryMtime.ToUnixTimeSeconds(), WholeFileChecksumOf(srcContent));
            string path = Path.Combine(dest, "b_fast.bin");
            await File.WriteAllBytesAsync(path, localContent);
            File.SetLastWriteTimeUtc(path, PullSession.ClampedMtimeUtc(entry.ModifiedUnixSeconds));

            (PullSession.ChecksumOutcome outcome, ItemFlags iflags) =
                await PullSession.ComputeChecksumDecisionAsync(entry, path, Session, CancellationToken.None);

            Assert.Equal(PullSession.ChecksumOutcome.Transfer, outcome);
            Assert.Equal(ItemFlags.Transfer | ItemFlags.ReportChange, iflags);
            Assert.Equal((ItemFlags)0x8002, iflags);
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public async Task ContentDiffers_SizeAndMtimeAlsoDiffer_SetsReportSizeAndReportTime()
    {
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-checksumdecision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dest);
        try
        {
            byte[] srcContent = "hello rsync\n"u8.ToArray();
            byte[] localContent = "stale\n"u8.ToArray(); // different size AND different content

            FileEntry entry = Regular("a_match.txt", srcContent.Length, EntryMtime.ToUnixTimeSeconds(), WholeFileChecksumOf(srcContent));
            string path = Path.Combine(dest, "a_match.txt");
            await File.WriteAllBytesAsync(path, localContent);
            File.SetLastWriteTimeUtc(path, new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // stale mtime too

            (PullSession.ChecksumOutcome outcome, ItemFlags iflags) =
                await PullSession.ComputeChecksumDecisionAsync(entry, path, Session, CancellationToken.None);

            Assert.Equal(PullSession.ChecksumOutcome.Transfer, outcome);
            Assert.Equal(
                ItemFlags.Transfer | ItemFlags.ReportChange | ItemFlags.ReportSize | ItemFlags.ReportTime,
                iflags);
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public async Task ContentMatches_MtimeDiffers_IsAttributeOnlyTime_AndFixesTheLocalMtime()
    {
        // a_match: identical content, stale mtime — -c fixes only the time, no data transfer.
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-checksumdecision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dest);
        try
        {
            byte[] content = "hello rsync\n"u8.ToArray();
            FileEntry entry = Regular("a_match.txt", content.Length, EntryMtime.ToUnixTimeSeconds(), WholeFileChecksumOf(content));
            string path = Path.Combine(dest, "a_match.txt");
            await File.WriteAllBytesAsync(path, content);
            var staleMtime = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path, staleMtime);

            (PullSession.ChecksumOutcome outcome, ItemFlags iflags) =
                await PullSession.ComputeChecksumDecisionAsync(entry, path, Session, CancellationToken.None);

            Assert.Equal(PullSession.ChecksumOutcome.AttributeOnlyTime, outcome);
            Assert.Equal(ItemFlags.ReportTime, iflags);
            Assert.Equal((ItemFlags)0x0008, iflags);
            Assert.Equal(PullSession.ClampedMtimeUtc(entry.ModifiedUnixSeconds), File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public async Task ContentMatches_MtimeMatches_Skips()
    {
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-checksumdecision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dest);
        try
        {
            byte[] content = "hello rsync\n"u8.ToArray();
            FileEntry entry = Regular("a_match.txt", content.Length, EntryMtime.ToUnixTimeSeconds(), WholeFileChecksumOf(content));
            string path = Path.Combine(dest, "a_match.txt");
            await File.WriteAllBytesAsync(path, content);
            File.SetLastWriteTimeUtc(path, PullSession.ClampedMtimeUtc(entry.ModifiedUnixSeconds));

            (PullSession.ChecksumOutcome outcome, ItemFlags iflags) =
                await PullSession.ComputeChecksumDecisionAsync(entry, path, Session, CancellationToken.None);

            Assert.Equal(PullSession.ChecksumOutcome.Skip, outcome);
            Assert.Equal(ItemFlags.None, iflags);
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public async Task MissingFlistChecksum_FallsBackToTransfer_InsteadOfThrowing()
    {
        // Should never happen under -c for a regular file, but a peer bug or a partial flist decode
        // must not crash the session — a missing F_SUM falls back to a safe full transfer.
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-checksumdecision-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dest);
        try
        {
            byte[] content = "hello rsync\n"u8.ToArray();
            FileEntry entry = Regular("a_match.txt", content.Length, EntryMtime.ToUnixTimeSeconds(), flistChecksum: null);
            string path = Path.Combine(dest, "a_match.txt");
            await File.WriteAllBytesAsync(path, content);
            File.SetLastWriteTimeUtc(path, PullSession.ClampedMtimeUtc(entry.ModifiedUnixSeconds));

            (PullSession.ChecksumOutcome outcome, ItemFlags iflags) =
                await PullSession.ComputeChecksumDecisionAsync(entry, path, Session, CancellationToken.None);

            Assert.Equal(PullSession.ChecksumOutcome.Transfer, outcome);
            Assert.Equal(ItemFlags.Transfer | ItemFlags.ReportChange, iflags);
        }
        finally
        {
            Directory.Delete(dest, recursive: true);
        }
    }
}
