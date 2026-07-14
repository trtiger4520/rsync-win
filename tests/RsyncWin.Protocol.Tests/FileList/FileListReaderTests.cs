using System.Globalization;
using System.IO.Pipelines;
using RsyncWin.Protocol.FileList;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Session;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Protocol.Tests.FileList;

/// <summary>
/// Replays every captured file list through the decoder. Ground truth comes from the capture
/// harness manifest and — for the 23-entry sizes tree — from rsync's own <c>--list-only</c>
/// stdout, which also pins the sorted (ndx) order line-for-line.
/// </summary>
public class FileListReaderTests
{
    private const int Proto31PrologueLength = 41;

    private static readonly FileListOptions TimesRecurse = new()
    {
        Protocol = 31,
        Id0Names = true, // compat 510 carries CF_ID0_NAMES
    };

    private static MultiplexReader CaptureReader(string vector) =>
        new(PipeReader.Create(new MemoryStream(TestFixtures.Bytes(vector, "s2c.bin")[Proto31PrologueLength..])));

    private static MultiplexReader SyntheticReader(params byte[][] payloads)
    {
        var stream = new MemoryStream();
        foreach (byte[] payload in payloads)
        {
            byte[] header = new byte[MuxHeader.Size];
            new MuxHeader(MessageTag.Data, payload.Length).Write(header);
            stream.Write(header);
            stream.Write(payload);
        }
        stream.Position = 0;
        return new MultiplexReader(PipeReader.Create(stream));
    }

    [Fact]
    public async Task PullRt_DecodesAndSorts_TheGoldenTree()
    {
        FileListResult result = await FileListReader.ReadAsync(CaptureReader("ssh31-pull-rt"), TimesRecurse);

        // Sorted (ndx) order — validated against the captured generator requests.
        (string Name, long Size, bool Dir, long Mtime)[] expected =
        [
            (".", 4096, true, 1577934245),
            ("b000_empty", 0, false, 1577934245),
            ("b001_small.txt", 12, false, 1577934245),
            ("b002_64k.bin", 65536, false, 1623758400),
            ("b003_300k.bin", 300000, false, 1623758400),
            ("b004_中文檔名.txt", 13, false, 1672531199),
            ("b005 name with space.txt", 11, false, 1672531199),
            ("subdir", 4096, true, 1577934245),
            ("subdir/nested.txt", 7, false, 1577934245),
        ];

        Assert.Equal(expected.Select(e => e.Name), result.Entries.Select(e => e.Name));
        Assert.Equal(expected.Select(e => e.Size), result.Entries.Select(e => e.Size));
        Assert.Equal(expected.Select(e => e.Dir), result.Entries.Select(e => e.IsDirectory));
        Assert.Equal(expected.Select(e => e.Mtime), result.Entries.Select(e => e.ModifiedUnixSeconds));
        Assert.Equal(0, result.IoError);
        Assert.Empty(result.UserNames);
        Assert.All(result.Entries.Where(e => !e.IsDirectory), e => Assert.True(e.IsRegularFile));
    }

    [Fact]
    public async Task Proto30_SameCompatSet_DecodesIdentically()
    {
        // The proto-30 flist frame is byte-identical to 31's for our compat set (verified by cmp
        // during spec validation); the decoder must agree.
        FileListResult thirty = await FileListReader.ReadAsync(
            CaptureReader("ssh30-pull-rt"), TimesRecurse with { Protocol = 30 });
        FileListResult thirtyOne = await FileListReader.ReadAsync(CaptureReader("ssh31-pull-rt"), TimesRecurse);

        Assert.Equal(
            thirtyOne.Entries.Select(e => (e.Name, e.Size, e.Mode, e.ModifiedUnixSeconds)),
            thirty.Entries.Select(e => (e.Name, e.Size, e.Mode, e.ModifiedUnixSeconds)));
    }

    [Fact]
    public async Task PullA_CarriesExplicitIds_AndTheId0NameTail()
    {
        var options = TimesRecurse with
        {
            PreserveUid = true,
            PreserveGid = true,
            PreserveLinks = true,
            PreserveDevices = true,
            PreserveSpecials = true,
        };
        FileListResult result = await FileListReader.ReadAsync(CaptureReader("ssh31-pull-a"), options);

        Assert.Equal(9, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.Equal(0, e.Uid));
        Assert.All(result.Entries, e => Assert.Equal(0, e.Gid));
        // CF_ID0_NAMES: the terminating varint(0) of each id list carries root's name.
        Assert.Equal("root", result.UserNames[0]);
        Assert.Equal("root", result.GroupNames[0]);
    }

    [Fact]
    public async Task SizesList_MatchesRsyncsOwnListOutput_LineForLine()
    {
        FileListResult result = await FileListReader.ReadAsync(CaptureReader("ssh31-sizes-list"), TimesRecurse);

        string[] groundTruth = TestFixtures.Lines("ssh31-sizes-list", "client-stdout.txt");
        Assert.Equal(groundTruth.Length, result.Entries.Count);

        for (int i = 0; i < groundTruth.Length; i++)
        {
            // "-rw-r--r--     12,345 2020/01/02 03:04:05 name with possible spaces"
            string line = groundTruth[i];
            string[] head = line.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
            long size = long.Parse(head[1].Replace(",", ""), CultureInfo.InvariantCulture);
            var stamp = DateTime.ParseExact(
                head[2] + " " + head[3][..8], "yyyy/MM/dd HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            string name = head[3][9..];

            FileEntry entry = result.Entries[i];
            Assert.Equal(name, entry.Name);                       // pins the sort, entry by entry
            Assert.Equal(size, entry.Size);
            Assert.Equal(line[0] == 'd', entry.IsDirectory);
            Assert.Equal(((DateTimeOffset)stamp).ToUnixTimeSeconds(), entry.ModifiedUnixSeconds);
        }

        // The root entry carries XMIT_MOD_NSEC — the only capture-pinned sub-second mtime.
        Assert.Equal(462952183, result.Entries[0].ModifiedNanoseconds);
    }

    [Fact]
    public async Task SyntheticUidGid_PinsVarintWidthAndFieldOrder()
    {
        // The all-root captures cannot discriminate uid/gid order or varint width (both ids are
        // the single byte 0x00 there); 1000 and 2000 are two-byte varints and distinct, so a
        // swapped read order or a byte-width mistake fails here. Also walks the non-terminator
        // id-list entries, which no capture exercises.
        byte[] entry =
        [
            0x04,                   // xflags: the EXTENDED_FLAGS zero-substitute — no SAME_* bits
            0x01, (byte)'a',        // l2=1, "a"
            0x00, 0x00, 0x00,       // size varlong(3) = 0
            0x00, 0x00, 0x00, 0x00, // mtime varlong(4) = 0
            0xa4, 0x81, 0x00, 0x00, // mode 0100644
            0x83, 0xe8,             // uid varint 1000
            0x87, 0xd0,             // gid varint 2000
            0x00, 0x00,             // end of list, io_error 0
            0x83, 0xe8, 0x05, (byte)'a', (byte)'l', (byte)'i', (byte)'c', (byte)'e',
            0x00, 0x04, (byte)'r', (byte)'o', (byte)'o', (byte)'t',
            0x87, 0xd0, 0x05, (byte)'s', (byte)'t', (byte)'a', (byte)'f', (byte)'f',
            0x00, 0x04, (byte)'r', (byte)'o', (byte)'o', (byte)'t',
        ];

        FileListResult result = await FileListReader.ReadAsync(
            SyntheticReader(entry), TimesRecurse with { PreserveUid = true, PreserveGid = true });

        FileEntry file = Assert.Single(result.Entries);
        Assert.Equal(1000, file.Uid);
        Assert.Equal(2000, file.Gid);
        Assert.Equal(new Dictionary<int, string> { [1000] = "alice", [0] = "root" }, result.UserNames);
        Assert.Equal(new Dictionary<int, string> { [2000] = "staff", [0] = "root" }, result.GroupNames);
    }

    [Fact]
    public async Task HostileLongNameLength_IsAStreamError_NotAnOverflow()
    {
        // XMIT_LONG_NAME with l2 near int.MaxValue: the bounds check must reject it before the
        // l1+l2 sum can wrap negative and crash the allocator instead.
        byte[] l2 = new byte[VarintCodec.MaxVarintLength];
        int written = VarintCodec.WriteVarint(l2, int.MaxValue - 8);
        byte[] entry = [0x58, .. l2[..written]]; // SAME_UID|SAME_GID|LONG_NAME

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await FileListReader.ReadAsync(SyntheticReader(entry), TimesRecurse));
    }

    [Fact]
    public async Task NegativeSize_IsRejected_WithExit4Semantics()
    {
        byte[] negativeSize = new byte[VarintCodec.MaxVarlongLength];
        int written = VarintCodec.WriteVarlong(negativeSize, -1, minBytes: 3);
        byte[] entry = [0x18, 0x01, (byte)'a', .. negativeSize[..written]];

        var exception = await Assert.ThrowsAsync<ProtocolException>(async () =>
            await FileListReader.ReadAsync(SyntheticReader(entry), TimesRecurse));
        Assert.Equal(RsyncExitCode.UnsupportedAction, exception.ExitCode);
    }

    [Fact]
    public async Task DotDotComponent_IsRejected()
    {
        byte[] entry = [0x18, 0x04, (byte)'a', (byte)'/', (byte)'.', (byte)'.'];

        var exception = await Assert.ThrowsAsync<ProtocolException>(async () =>
            await FileListReader.ReadAsync(SyntheticReader(entry), TimesRecurse));
        Assert.Equal(RsyncExitCode.UnsupportedAction, exception.ExitCode);
    }

    [Fact]
    public async Task PullChecksum_AppendsFSum_OnRegularFilesOnly()
    {
        var options = TimesRecurse with { Checksum = true, ChecksumLength = 16 };
        FileListResult result = await FileListReader.ReadAsync(CaptureReader("ssh31-pull-checksum"), options);

        (string Name, bool Dir)[] expected =
        [
            (".", true),
            ("a_match.txt", false),
            ("b_fast.bin", false),
            ("c_new.txt", false),
            ("subdir", true),
            ("subdir/nested.txt", false),
        ];
        Assert.Equal(expected.Select(e => e.Name), result.Entries.Select(e => e.Name));
        Assert.Equal(expected.Select(e => e.Dir), result.Entries.Select(e => e.IsDirectory));

        // Directories carry no F_SUM.
        Assert.All(result.Entries.Where(e => e.IsDirectory), e => Assert.Null(e.FlistChecksum));
        // Every regular file carries a full-length xxh128 digest.
        Assert.All(result.Entries.Where(e => e.IsRegularFile), e => Assert.Equal(16, e.FlistChecksum?.Length));

        FileEntry match = result.Entries.Single(e => e.Name == "a_match.txt");
        FileEntry fast = result.Entries.Single(e => e.Name == "b_fast.bin");
        Assert.Equal("09852ee072aa634cf08d35dc887a7999", Convert.ToHexStringLower(match.FlistChecksum!));
        Assert.Equal("51850289d127a046fc41e93a737b421f", Convert.ToHexStringLower(fast.FlistChecksum!));

        // A wrong F_SUM length would desync the tail — reaching io_error cleanly proves it's right.
        Assert.Equal(0, result.IoError);
    }

    [Fact]
    public async Task UnrequestedHardlinkData_FailsLoudly_NotSilently()
    {
        // xflags 0x218 (SAME_UID|SAME_GID|HLINKED) as varint 82 18: decoding the hlink varint we
        // never negotiated would silently desync — the reader must refuse instead.
        byte[] entry = [0x82, 0x18];

        var exception = await Assert.ThrowsAsync<ProtocolException>(async () =>
            await FileListReader.ReadAsync(SyntheticReader(entry), TimesRecurse));
        Assert.Equal(RsyncExitCode.UnsupportedAction, exception.ExitCode);
    }
}
