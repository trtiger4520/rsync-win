using System.Text;
using RsyncWin.Engine;
using RsyncWin.Fs;
using RsyncWin.Protocol.FileList;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Session;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Hermetic (not trait-gated): replays the entire captured proto-31 push through
/// <see cref="PushSession"/>. The capture negotiated xxh128, so the replay offers it too and every
/// whole-file trailer is genuinely computed by <see cref="PushSession"/> itself — a checksum bug
/// fails these tests, not just interop. Mirrors <see cref="PullSessionReplayTests"/>'s pattern.
/// </summary>
public class PushSessionReplayTests
{
    private static readonly HandshakeOptions Xxh128 = new() { ChecksumOffer = "xxh128" };

    private static readonly DateTime T1 = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2021, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T3 = new(2022, 12, 31, 23, 59, 59, DateTimeKind.Utc);

    private static byte[] Capture(string vector, string file) =>
        File.ReadAllBytes(Path.Combine(FindVectors(), vector, file));

    private static string FindVectors()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "test-fixtures", "vectors");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("test-fixtures/vectors not found above the test binary");
    }

    /// <summary>
    /// b002_64k.bin's real 65536-byte content ("tree64k" recipe, capture.sh) isn't checked in as its
    /// own fixture, but the same /t/tree is what <c>ssh31-pull-rt</c> transferred as a full literal
    /// pull — running that (already-tested) pull recovers the exact bytes without hardcoding an
    /// openssl keystream in C#.
    /// </summary>
    private static async Task<byte[]> ReconstructB002Async(CancellationToken cancellationToken)
    {
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-push-src-{Guid.NewGuid():N}");
        try
        {
            await using var transport = new ScriptedTransport(Capture("ssh31-pull-rt", "s2c.bin"));
            await PullSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/tree/"] },
                dest, cancellationToken, handshake: Xxh128);
            return await File.ReadAllBytesAsync(Path.Combine(dest, "b002_64k.bin"), cancellationToken);
        }
        finally
        {
            try
            {
                Directory.Delete(dest, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    /// <summary>
    /// Recreates the capture's 9-entry source tree (docs/capture.sh section 3 recipe: same names,
    /// sizes, and mtimes as the pull-direction captures) with real byte content, so a full push's
    /// literal payload is byte-comparable to the capture.
    /// </summary>
    private static async Task CreateSourceTreeAsync(
        string dest, byte[] b002Content, byte[] b003Content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(dest, "subdir"));

        async Task WriteAsync(string relative, byte[] content, DateTime mtimeUtc)
        {
            string path = Path.Combine(dest, relative);
            await File.WriteAllBytesAsync(path, content, cancellationToken);
            File.SetLastWriteTimeUtc(path, mtimeUtc);
        }

        await WriteAsync("b000_empty", [], T1);
        await WriteAsync("b001_small.txt", "hello rsync\n"u8.ToArray(), T1);
        await WriteAsync("b002_64k.bin", b002Content, T2);
        await WriteAsync("b003_300k.bin", b003Content, T2);
        await WriteAsync("b004_中文檔名.txt", "unicode name\n"u8.ToArray(), T3);
        await WriteAsync("b005 name with space.txt", "space name\n"u8.ToArray(), T3);
        await WriteAsync(Path.Combine("subdir", "nested.txt"), "nested\n"u8.ToArray(), T1);

        // Writing into a directory bumps its own mtime on Windows; reapply last, deepest first, to
        // match the capture's own `touch` order (capture.sh: subdir and the root both land on T1).
        Directory.SetLastWriteTimeUtc(Path.Combine(dest, "subdir"), T1);
        Directory.SetLastWriteTimeUtc(dest, T1);
    }

    /// <summary>
    /// <see cref="FileEnumerator"/> synthesizes directory size as 0 (NTFS has no meaningful directory
    /// byte size) but the real Linux capture sent the actual `stat` size (4096, ext4). Patching it
    /// here is capture-specific test fidelity, not something <see cref="FileEnumerator"/> itself
    /// should model for Windows.
    /// </summary>
    private static List<EnumeratedEntry> PatchDirectorySizes(IReadOnlyList<EnumeratedEntry> entries) =>
        [.. entries.Select(e => e.Wire.IsDirectory ? e with { Wire = e.Wire with { Size = 4096 } } : e)];

    /// <summary>
    /// The captured client sends the flist in genuine readdir order, not sorted order (both ends
    /// sort only after receipt — <c>docs/wire-notes.md</c> "Push direction"). Independently decoded
    /// from <c>ssh31-push-rt/c2s.bin</c> and <c>ssh31-push-uptodate/c2s.bin</c> (identical: same
    /// underlying /t/tree, same container run) by reading the flist off the wire BEFORE
    /// <see cref="FileListReader"/>'s own post-receipt sort — NOT the order this task's own note
    /// guessed. <see cref="FileEnumerator"/> always returns sorted order, so the replay test must
    /// re-order its output into this captured sequence for the sent flist bytes to match.
    /// </summary>
    private static readonly string[] CapturedTreeReaddirOrder =
    [
        ".", "b002_64k.bin", "b005 name with space.txt", "b001_small.txt", "b000_empty",
        "b004_中文檔名.txt", "b003_300k.bin", "subdir", "subdir/nested.txt",
    ];

    private static List<EnumeratedEntry> ReorderByName(IReadOnlyList<EnumeratedEntry> entries, IReadOnlyList<string> namesInOrder)
    {
        Dictionary<string, EnumeratedEntry> byName = entries.ToDictionary(e => e.Wire.Name);
        return [.. namesInOrder.Select(name => byName[name])];
    }

    /// <summary>
    /// Splices a fully-formed <see cref="MessageTag.ErrorXfer"/> mux frame into a captured s2c
    /// stream right at the boundary between the (non-muxed) handshake prologue and the first
    /// muxed frame — i.e. before anything else, DONE included. <see cref="MultiplexReader"/>
    /// dispatches an out-of-band tag to <c>MessageReceived</c> and keeps reading, so a frame
    /// spliced at ANY frame boundary is transparent to the rest of the choreography; the prologue
    /// boundary is simplest to compute since it needs no frame walking.
    /// </summary>
    private static byte[] InjectErrorXferFrame(byte[] s2c, string message)
    {
        // Mirrors HandshakeRunner.RunClientAsync's read order for the s2c side: version(4) +
        // varint(compat_flags) + vstring(server checksum list) + seed(4). Protocol 31 always
        // negotiates strings (CF_VARINT_FLIST_FLAGS is mandatory here), so the vstring is always
        // present.
        int offset = 4;
        (_, int varintConsumed) = VarintCodec.ReadVarint(s2c.AsSpan(offset));
        offset += varintConsumed;
        (_, int vstringConsumed) = VstringCodec.Read(s2c.AsSpan(offset));
        offset += vstringConsumed;
        offset += 4; // checksum seed

        byte[] payload = Encoding.UTF8.GetBytes(message);
        byte[] frame = new byte[MuxHeader.Size + payload.Length];
        new MuxHeader(MessageTag.ErrorXfer, payload.Length).Write(frame);
        payload.CopyTo(frame.AsSpan(MuxHeader.Size));

        byte[] spliced = new byte[s2c.Length + frame.Length];
        s2c.AsSpan(0, offset).CopyTo(spliced);
        frame.CopyTo(spliced.AsSpan(offset));
        s2c.AsSpan(offset).CopyTo(spliced.AsSpan(offset + frame.Length));
        return spliced;
    }

    private static byte[] Demux(byte[] framed)
    {
        var logical = new MemoryStream();
        for (int offset = 0; offset + MuxHeader.Size <= framed.Length;)
        {
            MuxHeader header = MuxHeader.Read(framed.AsSpan(offset));
            offset += MuxHeader.Size;
            if (header.Tag == MessageTag.Data)
                logical.Write(framed, offset, header.PayloadLength);
            offset += header.PayloadLength;
        }
        return logical.ToArray();
    }

    /// <summary>Demuxes our own written bytes and the captured c2s.bin oracle, each skipping its own
    /// prologue (version int + checksum-offer vstring — a push carries no seed on c2s, and no filter
    /// list follows the prologue at all), and returns (ours, theirs) ready for byte comparison.</summary>
    private static async Task<(byte[] Ours, byte[] Theirs)> CompareGeneratorBytesAsync(
        ScriptedTransport transport, string vector)
    {
        byte[] written = await transport.WrittenBytesAsync();
        int ourPrologue = 4 + 1 + "xxh128".Length;
        byte[] ours = Demux(written[ourPrologue..]);

        byte[] captured = Capture(vector, "c2s.bin");
        int capturedPrologue = 4 + 1 + "xxh128 xxh3 xxh64 md5 md4".Length;
        byte[] theirs = Demux(captured[capturedPrologue..]);

        return (ours, theirs);
    }

    [Fact]
    public async Task FullPushReplay_MatchesCapturedClientExactly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string sourceDir = Path.Combine(Path.GetTempPath(), $"rsyncwin-push-rt-{Guid.NewGuid():N}");
        try
        {
            byte[] b002 = await ReconstructB002Async(cts.Token);
            byte[] b003 = Capture("ssh31-push-delta", "result.bin"); // same 300000-byte tree300k content

            Directory.CreateDirectory(sourceDir);
            await CreateSourceTreeAsync(sourceDir, b002, b003, cts.Token);

            List<EnumeratedEntry> entries = ReorderByName(
                PatchDirectorySizes(FileEnumerator.Enumerate(sourceDir)), CapturedTreeReaddirOrder);

            await using var transport = new ScriptedTransport(Capture("ssh31-push-rt", "s2c.bin"));
            PushSession.Result result = await PushSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = false, Recurse = true, Paths = ["/t/pushdst/"] },
                entries, cts.Token, handshake: Xxh128);

            // 6 top-level files + subdir/nested.txt = 7 regular-file transfers; root '.' and subdir
            // are attribute-only (0x0008 and 0x6000 respectively, per the decoded capture requests).
            Assert.Equal(7, result.FilesSent);
            Assert.Equal(2, result.AttributeOnlyReplies);
            Assert.Empty(result.FailedFiles);

            (byte[] ours, byte[] theirs) = await CompareGeneratorBytesAsync(transport, "ssh31-push-rt");
            Assert.Equal(theirs, ours);
        }
        finally
        {
            try
            {
                Directory.Delete(sourceDir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    [Fact]
    public async Task UpToDateReplay_ZeroRequests_MatchesCapturedClientExactly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string sourceDir = Path.Combine(Path.GetTempPath(), $"rsyncwin-push-uptodate-{Guid.NewGuid():N}");
        try
        {
            // Content is irrelevant here (the server-generator sends zero requests either way), but
            // sizes/mtimes must still match so the flist itself stays byte-identical.
            byte[] b002 = new byte[65536];
            byte[] b003 = new byte[300000];

            Directory.CreateDirectory(sourceDir);
            await CreateSourceTreeAsync(sourceDir, b002, b003, cts.Token);

            List<EnumeratedEntry> entries = ReorderByName(
                PatchDirectorySizes(FileEnumerator.Enumerate(sourceDir)), CapturedTreeReaddirOrder);

            await using var transport = new ScriptedTransport(Capture("ssh31-push-uptodate", "s2c.bin"));
            PushSession.Result result = await PushSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = false, Recurse = true, Paths = ["/t/pushdst/"] },
                entries, cts.Token, handshake: Xxh128);

            Assert.Equal(0, result.FilesSent);
            Assert.Equal(0, result.AttributeOnlyReplies);
            Assert.Empty(result.FailedFiles);

            (byte[] ours, byte[] theirs) = await CompareGeneratorBytesAsync(transport, "ssh31-push-uptodate");
            Assert.Equal(theirs, ours);
        }
        finally
        {
            try
            {
                Directory.Delete(sourceDir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    [Fact]
    public async Task DeltaReplay_MatchesAgainstStaleBasisSignature_ByteExact()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string sourceDir = Path.Combine(Path.GetTempPath(), $"rsyncwin-push-delta-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(sourceDir);
            byte[] content = Capture("ssh31-push-delta", "result.bin"); // the clean 300000-byte source
            string path = Path.Combine(sourceDir, "b003_300k.bin");
            await File.WriteAllBytesAsync(path, content, cts.Token);
            // Pin the mtime to the capture's own flist mtime (a whole second → ModifiedNanoseconds 0)
            // so the enumerated flist is byte-identical to ssh31-push-delta's.
            File.SetLastWriteTimeUtc(path, DateTimeOffset.FromUnixTimeSeconds(1623758400).UtcDateTime);

            // Source the single entry through the real FileEnumerator (single-file source → one
            // basename entry, no "." root) rather than a hand-built FileEntry: this makes the whole
            // test an end-to-end FileEnumerator → FileListWriter byte-exact guard. RedoReplay below
            // keeps the hand-built form as an independent control on the same wire shape.
            var entry = FileEnumerator.Enumerate(path).Single();

            await using var transport = new ScriptedTransport(Capture("ssh31-push-delta", "s2c.bin"));
            PushSession.Result result = await PushSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = false, Paths = ["/t/pushdelta/"] },
                [entry], cts.Token, handshake: Xxh128);

            Assert.Equal(1, result.FilesSent);
            Assert.Empty(result.FailedFiles);
            Assert.Equal(298600, result.MatchedBytes);
            Assert.Equal(1400, result.LiteralBytes);

            (byte[] ours, byte[] theirs) = await CompareGeneratorBytesAsync(transport, "ssh31-push-delta");
            Assert.Equal(theirs, ours);
        }
        finally
        {
            try
            {
                Directory.Delete(sourceDir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    [Fact]
    public async Task RedoReplay_BothPhasesByteExact_FileNotFailed()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string sourceDir = Path.Combine(Path.GetTempPath(), $"rsyncwin-push-redo-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(sourceDir);
            byte[] content = Capture("ssh31-push-redo", "result.bin"); // the clean 4 MiB source
            Assert.Equal(4194304, content.Length);
            string path = Path.Combine(sourceDir, "big.bin");
            await File.WriteAllBytesAsync(path, content, cts.Token);

            var entry = new EnumeratedEntry(
                new FileEntry
                {
                    NameBytes = "big.bin"u8.ToArray(),
                    Mode = 0x81A4,
                    Size = 4194304,
                    ModifiedUnixSeconds = 1683263105, // 2023-05-05T05:05:05Z
                },
                path);

            await using var transport = new ScriptedTransport(Capture("ssh31-push-redo", "s2c.bin"));
            PushSession.Result result = await PushSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = false, Paths = ["/t/pushredo-dst/"] },
                [entry], cts.Token, handshake: Xxh128);

            Assert.Empty(result.FailedFiles);

            (byte[] ours, byte[] theirs) = await CompareGeneratorBytesAsync(transport, "ssh31-push-redo");
            Assert.Equal(theirs, ours);
        }
        finally
        {
            try
            {
                Directory.Delete(sourceDir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    /// <summary>
    /// P10 push --delete: the remote receiver deletes extraneous entries and reports each via a
    /// MSG_DELETED mux frame (tag 0x6c), right after the seed and before the transfer phase — there
    /// is NO del-stats block (ssh31-push-delete). This replays that s2c and pins that PushSession
    /// consumes the three MSG_DELETED frames transparently (no desync/hang), surfaces them on
    /// <see cref="PushSession.Result.DeletedPaths"/> deepest-first, and that our c2s opens with the
    /// empty filter list --delete adds.
    /// </summary>
    [Fact]
    public async Task DeleteReplay_ConsumesMsgDeleted_ReportsPaths_AndSendsFilterList()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string sourceDir = Path.Combine(Path.GetTempPath(), $"rsyncwin-push-delete-{Guid.NewGuid():N}");
        try
        {
            // Source /t/pdsrc: keep1.txt + keepdir/keep2.txt (content irrelevant — the canned s2c
            // requests only an attribute fix on the root, no transfers). Only the ndx space must line
            // up: "." sorts to ndx 0, which is the one entry the server itemizes.
            Directory.CreateDirectory(Path.Combine(sourceDir, "keepdir"));
            await File.WriteAllBytesAsync(Path.Combine(sourceDir, "keep1.txt"), "keep one\n"u8.ToArray(), cts.Token);
            await File.WriteAllBytesAsync(Path.Combine(sourceDir, "keepdir", "keep2.txt"), "keep two\n"u8.ToArray(), cts.Token);

            List<EnumeratedEntry> entries = [.. FileEnumerator.Enumerate(sourceDir)];

            await using var transport = new ScriptedTransport(Capture("ssh31-push-delete", "s2c.bin"));
            PushSession.Result result = await PushSession.RunAsync(
                transport,
                new ServerArgvBuilder { Sender = false, Recurse = true, Delete = true, Paths = ["/t/pddst/"] },
                entries, cts.Token, handshake: Xxh128);

            Assert.Equal(["extradir/inside.txt", "extradir", "extra.txt"], result.DeletedPaths);
            Assert.Equal(1, result.AttributeOnlyReplies); // the root "." mtime fix
            Assert.Equal(0, result.FilesSent);
            Assert.Empty(result.FailedFiles);

            // --delete adds the empty filter list (int32 0) to the c2s, before the flist.
            byte[] written = await transport.WrittenBytesAsync();
            int prologue = 4 + 1 + "xxh128".Length;
            byte[] logical = Demux(written[prologue..]);
            Assert.Equal((byte[])[0, 0, 0, 0], logical[..4]);
        }
        finally
        {
            try
            {
                Directory.Delete(sourceDir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    /// <summary>
    /// F1 (adversarial review, exit-code correctness): a remote MSG_ERROR_XFER used to be collected
    /// into a local list and dropped on the floor — the push completed as if nothing happened, and
    /// Program.cs had no way to map it to exit 23 the way rsync itself would. Pins that the message
    /// now surfaces on <see cref="PushSession.Result.ServerMessages"/> and that receiving it does not
    /// abort or hang the session (the tag is legal on <see cref="MultiplexReader"/>'s push-client
    /// allow-list; it is only ever dispatched, never thrown).
    /// </summary>
    [Fact]
    public async Task RemoteErrorXfer_SurfacesOnResult_AndSessionStillCompletes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string sourceDir = Path.Combine(Path.GetTempPath(), $"rsyncwin-push-errorxfer-{Guid.NewGuid():N}");
        try
        {
            // Content is irrelevant (ssh31-push-uptodate's generator sends zero requests either
            // way) — only sizes/mtimes need to match so the flist itself stays byte-identical.
            byte[] b002 = new byte[65536];
            byte[] b003 = new byte[300000];

            Directory.CreateDirectory(sourceDir);
            await CreateSourceTreeAsync(sourceDir, b002, b003, cts.Token);

            List<EnumeratedEntry> entries = ReorderByName(
                PatchDirectorySizes(FileEnumerator.Enumerate(sourceDir)), CapturedTreeReaddirOrder);

            byte[] s2c = InjectErrorXferFrame(Capture("ssh31-push-uptodate", "s2c.bin"), "test remote failure");
            await using var transport = new ScriptedTransport(s2c);
            PushSession.Result result = await PushSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = false, Recurse = true, Paths = ["/t/pushdst/"] },
                entries, cts.Token, handshake: Xxh128);

            Assert.Contains(
                result.ServerMessages, m => m.Tag == MessageTag.ErrorXfer && m.Text == "test remote failure");
            Assert.Equal(0, result.FilesSent); // session ran to completion despite the injected error
            Assert.Empty(result.FailedFiles); // the error is orthogonal to per-file reply failures
        }
        finally
        {
            try
            {
                Directory.Delete(sourceDir, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    /// <summary>
    /// F3 (adversarial review, oversize-source misreport): pins the extracted pure predicate at its
    /// only meaningful boundary, without a real 2 GiB fixture. <c>int.MaxValue</c> itself is still
    /// readable by <see cref="File.ReadAllBytesAsync(string, CancellationToken)"/> (it takes an
    /// array length, and arrays are indexed by int) — only lengths strictly greater are out of
    /// reach for this build.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(int.MaxValue, false)]
    [InlineData((long)int.MaxValue + 1, true)]
    [InlineData(long.MaxValue, true)]
    public void ExceedsSupportedSize_BoundaryIsIntMaxValue(long length, bool expected) =>
        Assert.Equal(expected, PushSession.ExceedsSupportedSize(length));
}
