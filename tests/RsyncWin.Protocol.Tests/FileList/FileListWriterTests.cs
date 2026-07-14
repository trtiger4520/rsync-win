using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using RsyncWin.Protocol.FileList;
using RsyncWin.Protocol.Mux;

namespace RsyncWin.Protocol.Tests.FileList;

/// <summary>
/// Encode-side counterpart of <see cref="FileListReaderTests"/>: builds the same 9-entry capture
/// tree from the recipe in <c>test-fixtures/capture/capture.sh</c> §3 and checks
/// <see cref="FileListWriter"/> reproduces the real rsync client's bytes exactly, plus a
/// reader round-trip covering <c>XMIT_MOD_NSEC</c>.
/// </summary>
public class FileListWriterTests
{
    private static readonly FileListOptions TimesRecurse = new() { Protocol = 31 };

    // Raw c2s offset where the flist's mux frame header starts (verified: `c4 00 00 07` at 30,
    // the 196-byte MSG_DATA payload — xflags of "." through the terminator + io_error — at 34..229).
    private const int PushRtMuxHeaderOffset = 30;
    private const int PushRtFrameLength = MuxHeader.Size + 196;

    private static FileEntry Entry(string name, int mode, long size, long mtimeUnixSeconds, int nsec = 0) =>
        new()
        {
            NameBytes = Encoding.UTF8.GetBytes(name),
            Mode = mode,
            Size = size,
            ModifiedUnixSeconds = mtimeUnixSeconds,
            ModifiedNanoseconds = nsec,
        };

    /// <summary>
    /// The 9-entry tree in the sender's real readdir order (docs/flist-spec.md §3, MEASURED), with
    /// sizes/mtimes from the capture recipe (TZ=UTC): t1=2020-01-02T03:04:05Z, t2=2021-06-
    /// 15T12:00:00Z, t3=2022-12-31T23:59:59Z.
    /// </summary>
    private static FileEntry[] CaptureTree()
    {
        const int dirMode = FileEntry.Directory | 0b111_101_101;   // 0o40755
        const int fileMode = FileEntry.RegularFile | 0b110_100_100; // 0o100644
        const long t1 = 1577934245, t2 = 1623758400, t3 = 1672531199;

        return
        [
            Entry(".", dirMode, 4096, t1),
            Entry("b002_64k.bin", fileMode, 65536, t2),
            Entry("b005 name with space.txt", fileMode, 11, t3),
            Entry("b001_small.txt", fileMode, 12, t1),
            Entry("b000_empty", fileMode, 0, t1),
            Entry("b004_中文檔名.txt", fileMode, 13, t3),
            Entry("b003_300k.bin", fileMode, 300000, t2),
            Entry("subdir", dirMode, 4096, t1),
            Entry("subdir/nested.txt", fileMode, 7, t1),
        ];
    }

    private static async Task<byte[]> WriteFramedAsync(FileEntry[] entries, FileListOptions options, int ioError = 0)
    {
        var pipe = new Pipe();
        var writer = new MultiplexWriter(pipe.Writer);
        FileListWriter.Write(writer, entries, options, ioError);
        await writer.FlushAsync();
        await pipe.Writer.CompleteAsync();

        ReadResult result = await pipe.Reader.ReadAsync();
        return result.Buffer.ToArray();
    }

    [Fact]
    public async Task PushRt_MatchesTheCapturedClientBytes_ByteExact()
    {
        byte[] framed = await WriteFramedAsync(CaptureTree(), TimesRecurse);

        byte[] captured = TestFixtures.Bytes("ssh31-push-rt", "c2s.bin")
            .Skip(PushRtMuxHeaderOffset).Take(PushRtFrameLength).ToArray();

        Assert.Equal(captured, framed);
    }

    [Fact]
    public async Task EncodeThenDecode_RoundTrips_IncludingModNsec()
    {
        FileEntry[] entries =
        [
            Entry(".", FileEntry.Directory | 0b111_101_101, 4096, 1577934245),
            Entry("a.txt", FileEntry.RegularFile | 0b110_100_100, 12, 1672531199, nsec: 462952183),
            Entry("b.txt", FileEntry.RegularFile | 0b110_100_100, 12, 1672531199), // SAME_TIME, no nsec
        ];

        byte[] framed = await WriteFramedAsync(entries, TimesRecurse, ioError: 0);

        var reader = new MultiplexReader(PipeReader.Create(new MemoryStream(framed)));
        FileListResult result = await FileListReader.ReadAsync(reader, TimesRecurse);

        Assert.Equal(0, result.IoError);
        // Post-transfer sort (FileNameComparer) happens to equal input order here: "." first, then
        // non-dirs in byte order ("a.txt" < "b.txt") — so a direct positional comparison is valid.
        Assert.Equal(
            entries.Select(e => (e.Name, e.Size, e.Mode, e.ModifiedUnixSeconds, e.ModifiedNanoseconds)),
            result.Entries.Select(e => (e.Name, e.Size, e.Mode, e.ModifiedUnixSeconds, e.ModifiedNanoseconds)));
        Assert.Equal(462952183, result.Entries.Single(e => e.Name == "a.txt").ModifiedNanoseconds);
    }

    [Fact]
    public async Task PreserveUid_IsRejected_NotYetImplemented()
    {
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
            await WriteFramedAsync(CaptureTree(), TimesRecurse with { PreserveUid = true }));
    }

    /// <summary>
    /// P10 push --checksum: a regular-file entry gains a 16-byte F_SUM as its LAST field; directories
    /// carry none (docs/flist-spec.md §14, capture ssh31-push-checksum). The reader side is already
    /// capture-pinned against real rsync (pull -c), so a write→read round-trip that recovers the exact
    /// F_SUM proves the writer emits the real-rsync layout. b000_empty's F_SUM is the real decoded
    /// value from the capture (xxh128("") seed 0), tying this to ground truth, not just self-consistency.
    /// </summary>
    [Fact]
    public async Task Checksum_EmitsFSumOnRegularFilesOnly_RoundTrips()
    {
        // xxh128("") seed 0 = ssh31-push-checksum's decoded b000_empty F_SUM (low64-LE ∥ high64-LE).
        byte[] emptyXxh128 = [0x7f, 0x49, 0x8d, 0x46, 0x24, 0xc3, 0x01, 0x60, 0xd8, 0x98, 0x47, 0x01, 0xd3, 0x06, 0xaa, 0x99];
        byte[] fileSum = [.. Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i))];

        FileEntry[] entries =
        [
            Entry(".", FileEntry.Directory | 0b111_101_101, 4096, 1577934245), // dir: no F_SUM
            Entry("b000_empty", FileEntry.RegularFile | 0b110_100_100, 0, 1577934245) with { FlistChecksum = emptyXxh128 },
            Entry("b001_small.txt", FileEntry.RegularFile | 0b110_100_100, 12, 1577934245) with { FlistChecksum = fileSum },
        ];

        var options = new FileListOptions { Protocol = 31, Checksum = true, ChecksumLength = 16 };
        byte[] framed = await WriteFramedAsync(entries, options);

        var reader = new MultiplexReader(PipeReader.Create(new MemoryStream(framed)));
        FileListResult result = await FileListReader.ReadAsync(reader, options);

        // Post-sort order: "." first, then non-dirs in byte order (b000 < b001).
        Assert.Null(result.Entries.Single(e => e.Name == ".").FlistChecksum);
        Assert.Equal(emptyXxh128, result.Entries.Single(e => e.Name == "b000_empty").FlistChecksum);
        Assert.Equal(fileSum, result.Entries.Single(e => e.Name == "b001_small.txt").FlistChecksum);
    }

    [Fact]
    public async Task Checksum_MissingFSumOnRegularFile_Throws()
    {
        FileEntry[] entries = [Entry("a.txt", FileEntry.RegularFile | 0b110_100_100, 12, 1577934245)]; // no FlistChecksum
        var options = new FileListOptions { Protocol = 31, Checksum = true, ChecksumLength = 16 };

        await Assert.ThrowsAsync<ArgumentException>(async () => await WriteFramedAsync(entries, options));
    }
}
