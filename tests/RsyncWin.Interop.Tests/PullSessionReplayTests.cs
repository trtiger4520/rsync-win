using System.Security.Cryptography;
using RsyncWin.Engine;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Session;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Hermetic (not trait-gated): replays the entire captured proto-31 pull through
/// <see cref="PullSession"/>. The capture negotiated xxh128, so the replay offers it too and every
/// whole-file trailer is genuinely verified — a checksum bug fails these tests, not just interop.
/// </summary>
public class PullSessionReplayTests
{
    private static readonly HandshakeOptions Xxh128 = new() { ChecksumOffer = "xxh128" };

    // SHA-256 of the deterministic capture tree (recomputed from the capture.sh recipes).
    private static readonly (string Name, string Sha256)[] ExpectedFiles =
    [
        ("b000_empty", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"),
        ("b001_small.txt", "1258c37c8da7e96d3b53a03b134c9d8f0c1b3fe0c10c45eead0a47ff868f145d"),
        ("b002_64k.bin", "afdca747c04f45e53b27ad99e68b67fb3b8155e7ea846d0e32ffb916122a9c75"),
        ("b003_300k.bin", "417198a0fb41a4c8c340fdd4a58ac90be823a30df37f0386b7773a4246dea8db"),
        ("b004_中文檔名.txt", "f682a5ef26796a5f98678d3a028d07c8853e6c5fc01005b55bd95852d00fc917"),
        ("b005 name with space.txt", "446f72dd97ede3ad34e1f6b48da1bc18e84b2f86566c0ef38acf68e62b7386be"),
        (Path.Combine("subdir", "nested.txt"), "370a8c04b8a65bb4494275eec227f1b694db04c76da6b0b8ae88ed1ab19790a3"),
    ];

    private static byte[] Capture(string file) =>
        File.ReadAllBytes(Path.Combine(FindVectors(), "ssh31-pull-rt", file));

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

    [Fact]
    public async Task FullPullReplay_ReconstructsEveryFile_ByteExact()
    {
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-replay-{Guid.NewGuid():N}");
        try
        {
            await using var transport = new ScriptedTransport(Capture("s2c.bin"));
            PullSession.Result result = await PullSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/tree/"] },
                dest, handshake: Xxh128);

            Assert.Equal(7, result.TransferredFiles);
            Assert.Equal(365579, result.TransferredBytes);
            Assert.Empty(result.RedoneFiles);
            Assert.Empty(result.FailedFiles);
            Assert.Empty(result.SkippedNonRegular);
            Assert.Equal(365579, result.Stats.TotalSize);

            foreach ((string name, string expected) in ExpectedFiles)
            {
                byte[] content = await File.ReadAllBytesAsync(Path.Combine(dest, name));
                Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(content)));
            }

            // mtimes came off the wire: b001 was created at 2020-01-02T03:04:05Z.
            Assert.Equal(
                DateTimeOffset.FromUnixTimeSeconds(1577934245).UtcDateTime,
                File.GetLastWriteTimeUtc(Path.Combine(dest, "b001_small.txt")));
            Assert.True(Directory.Exists(Path.Combine(dest, "subdir")));

            // Temp files must never outlive the session, success or failure.
            Assert.Empty(Directory.GetFiles(dest, "*.rsyncwin-tmp", SearchOption.AllDirectories));
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

    [Fact]
    public async Task Replay_ReplacesAReadOnlyDestinationFile()
    {
        // rsync's contract: a read-only destination file is replaced, not an error. File.Move
        // alone refuses read-only targets, so this pins the attribute-clearing path.
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-readonly-{Guid.NewGuid():N}");
        string target = Path.Combine(dest, "b001_small.txt");
        try
        {
            Directory.CreateDirectory(dest);
            await File.WriteAllTextAsync(target, "stale content the pull must replace");
            File.SetAttributes(target, FileAttributes.ReadOnly);

            await using var transport = new ScriptedTransport(Capture("s2c.bin"));
            PullSession.Result result = await PullSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/tree/"] },
                dest, handshake: Xxh128);

            Assert.Equal(7, result.TransferredFiles);
            Assert.Empty(result.RedoneFiles);
            Assert.Empty(result.FailedFiles);
            Assert.Equal(
                "1258c37c8da7e96d3b53a03b134c9d8f0c1b3fe0c10c45eead0a47ff868f145d",
                Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(target))));
        }
        finally
        {
            if (File.Exists(target))
                File.SetAttributes(target, FileAttributes.Normal);
            try
            {
                Directory.Delete(dest, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    [Fact]
    public async Task Replay_LeavesAPreExistingTempNameCollisionUntouched()
    {
        // The temp filename is now randomized per receive (P5), so a stale/foreign file that
        // happens to match the old deterministic "<final>.rsyncwin-tmp" name must survive the
        // pull completely untouched — the receiver never writes to or deletes it.
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-tempcollision-{Guid.NewGuid():N}");
        string collision = Path.Combine(dest, "b001_small.txt.rsyncwin-tmp");
        const string sentinel = "SENTINEL - must survive";
        try
        {
            Directory.CreateDirectory(dest);
            await File.WriteAllTextAsync(collision, sentinel);

            await using var transport = new ScriptedTransport(Capture("s2c.bin"));
            PullSession.Result result = await PullSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/tree/"] },
                dest, handshake: Xxh128);

            Assert.Equal(7, result.TransferredFiles);
            Assert.Empty(result.RedoneFiles);
            Assert.Empty(result.FailedFiles);
            Assert.True(File.Exists(collision));
            Assert.Equal(sentinel, await File.ReadAllTextAsync(collision));
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

    [Fact]
    public async Task GeneratorBytes_MatchTheCapturedClient_Exactly()
    {
        // Frame boundaries carry no semantics, so equality is asserted on the demuxed logical
        // stream: everything after the handshake must be byte-identical to what the real rsync
        // client sent for the same tree into an empty destination.
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-golden-{Guid.NewGuid():N}");
        try
        {
            await using var transport = new ScriptedTransport(Capture("s2c.bin"));
            await PullSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/tree/"] },
                dest, handshake: Xxh128);

            byte[] written = await transport.WrittenBytesAsync();
            int ourPrologue = 4 + 1 + "xxh128".Length; // version int + offer vstring
            byte[] ours = Demux(written[ourPrologue..]);

            byte[] captured = Capture("c2s.bin");
            int capturedPrologue = 4 + 1 + "xxh128 xxh3 xxh64 md5 md4".Length;
            byte[] theirs = Demux(captured[capturedPrologue..]);

            Assert.Equal(theirs, ours);
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

    [Fact]
    public async Task UpToDateReplay_SkipsEveryFile_ZeroGeneratorBytes()
    {
        // Destination already matches the capture tree's size+mtime everywhere: the mtime+size
        // fast path (P5) must skip all seven regular files — no request, no ndx, nothing written.
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-uptodate-{Guid.NewGuid():N}");
        try
        {
            Dictionary<string, byte[]> original = await CreateMatchingTreeAsync(dest);

            await using var transport = new ScriptedTransport(Capture("ssh31-pull-uptodate", "s2c.bin"));
            PullSession.Result result = await PullSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/tree/"] },
                dest, handshake: Xxh128);

            Assert.Equal(0, result.TransferredFiles);
            Assert.Empty(result.RedoneFiles);
            Assert.Empty(result.FailedFiles);

            foreach ((string name, byte[] content) in original)
                Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dest, name)));

            byte[] written = await transport.WrittenBytesAsync();
            int ourPrologue = 4 + 1 + "xxh128".Length;
            byte[] ours = Demux(written[ourPrologue..]);

            byte[] captured = Capture("ssh31-pull-uptodate", "c2s.bin");
            int capturedPrologue = 4 + 1 + "xxh128 xxh3 xxh64 md5 md4".Length;
            byte[] theirs = Demux(captured[capturedPrologue..]);

            Assert.Equal(theirs, ours);
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

    [Fact]
    public async Task PartialReplay_TransfersOnlyTheTwoStaleFiles()
    {
        // b001 is stale in content+mtime (iflags 0x800C); b002 has identical bytes but an older
        // mtime (iflags 0x8008, --whole-file so no delta). Every other file stays on the fast path.
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-partial-{Guid.NewGuid():N}");
        try
        {
            Dictionary<string, byte[]> original = await CreateMatchingTreeAsync(dest);

            var staleMtime = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            string b001 = Path.Combine(dest, "b001_small.txt");
            await File.WriteAllTextAsync(b001, "stale\n");
            File.SetLastWriteTimeUtc(b001, staleMtime);
            original.Remove("b001_small.txt");

            string b002 = Path.Combine(dest, "b002_64k.bin");
            // Same size as the flist entry, arbitrary bytes — only the mtime is stale for b002.
            await File.WriteAllBytesAsync(b002, new byte[65536]);
            File.SetLastWriteTimeUtc(b002, staleMtime);
            original.Remove("b002_64k.bin");

            // Both overrides live directly in the root and just bumped its mtime (Windows) — reset
            // it so the root directory itself still passes the fast path, matching the capture.
            Directory.SetLastWriteTimeUtc(dest, new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc));

            await using var transport = new ScriptedTransport(Capture("ssh31-pull-partial", "s2c.bin"));
            PullSession.Result result = await PullSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/tree/"] },
                dest, handshake: Xxh128);

            Assert.Equal(2, result.TransferredFiles);
            Assert.Empty(result.RedoneFiles);
            Assert.Empty(result.FailedFiles);

            Assert.Equal(
                "1258c37c8da7e96d3b53a03b134c9d8f0c1b3fe0c10c45eead0a47ff868f145d",
                Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(b001))));
            Assert.Equal(
                "afdca747c04f45e53b27ad99e68b67fb3b8155e7ea846d0e32ffb916122a9c75",
                Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(b002))));

            Assert.Equal(
                DateTimeOffset.FromUnixTimeSeconds(1577934245).UtcDateTime, // 2020-01-02T03:04:05Z
                File.GetLastWriteTimeUtc(b001));
            Assert.Equal(
                new DateTime(2021, 6, 15, 12, 0, 0, DateTimeKind.Utc),
                File.GetLastWriteTimeUtc(b002));

            foreach ((string name, byte[] content) in original)
                Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(dest, name)));

            byte[] written = await transport.WrittenBytesAsync();
            int ourPrologue = 4 + 1 + "xxh128".Length;
            byte[] ours = Demux(written[ourPrologue..]);

            byte[] captured = Capture("ssh31-pull-partial", "c2s.bin");
            int capturedPrologue = 4 + 1 + "xxh128 xxh3 xxh64 md5 md4".Length;
            byte[] theirs = Demux(captured[capturedPrologue..]);

            Assert.Equal(theirs, ours);
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
    /// Writes the seven-file capture tree into <paramref name="dest"/> with arbitrary (but
    /// deterministic, non-zero) content of the exact captured size and the exact captured mtime,
    /// so every entry passes the mtime+size fast path unless a test overrides it afterward.
    /// Returns each relative name mapped to the bytes actually written, for later "unchanged"
    /// assertions.
    /// </summary>
    private static async Task<Dictionary<string, byte[]>> CreateMatchingTreeAsync(string dest)
    {
        Directory.CreateDirectory(Path.Combine(dest, "subdir"));

        var t1 = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var t2 = new DateTime(2021, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var t3 = new DateTime(2022, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        (string Relative, int Size, DateTime MtimeUtc)[] plan =
        [
            ("b000_empty", 0, t1),
            ("b001_small.txt", 12, t1),
            ("b002_64k.bin", 65536, t2),
            ("b003_300k.bin", 300000, t2),
            ("b004_中文檔名.txt", 13, t3),
            ("b005 name with space.txt", 11, t3),
            (Path.Combine("subdir", "nested.txt"), 7, t1),
        ];

        var written = new Dictionary<string, byte[]>();
        foreach ((string relative, int size, DateTime mtimeUtc) in plan)
        {
            byte[] content = new byte[size];
            Array.Fill(content, (byte)0x5A); // distinctive non-zero filler, content is content-blind to the fast path
            string path = Path.Combine(dest, relative);
            await File.WriteAllBytesAsync(path, content);
            File.SetLastWriteTimeUtc(path, mtimeUtc);
            written[relative] = content;
        }

        // Writing files into a directory bumps its own mtime on Windows, so directory mtimes must
        // be (re)applied last, deepest first — mirroring the capture tree, where both the transfer
        // root and subdir were touched to t1 (capture.sh section 3), and the directory fast path
        // (PullSession.TryComputeDirectoryIflags) needs them to match exactly for a truly empty
        // generator stream.
        Directory.SetLastWriteTimeUtc(Path.Combine(dest, "subdir"), t1);
        Directory.SetLastWriteTimeUtc(dest, t1);

        return written;
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
}
