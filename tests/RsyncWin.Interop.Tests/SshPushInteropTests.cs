using System.Security.Cryptography;
using RsyncWin.Engine;
using RsyncWin.Fs;
using RsyncWin.Protocol.Session;
using RsyncWin.Transport;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// THE push-direction interop milestone, live: push a whole local tree to a real rsync over ssh.exe,
/// byte-identical on the server (SHA-256 per file, computed inside the container), with the real
/// remote process exiting 0. Mirrors <see cref="SshPullInteropTests"/>'s substrate and technique,
/// with roles reversed: we are the sender, the container's rsync is the receiver/generator.
/// </summary>
[Trait("Category", "Interop")]
public sealed class SshPushInteropTests(SshRsyncContainer container) : IClassFixture<SshRsyncContainer>
{
    /// <summary>Deterministic pseudo-random content, no fixture shipped: same idiom as the pull
    /// delta test's openssl keystream, just generated in-process since the source tree here lives
    /// on the Windows side, not inside the container.</summary>
    private static byte[] DeterministicBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>
    /// Builds the local source tree: nested dirs (subdir, subdir/deep), an empty file, 64 KiB + 300
    /// KiB deterministic binaries, a unicode name, and a name with a space. Mtimes are left as
    /// whatever the real write produced -- never rounded or forced -- so genuine sub-second NTFS
    /// precision flows into the flist exactly like a real user's files would.
    /// </summary>
    private static async Task CreateLocalTreeAsync(string root, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(root, "subdir", "deep"));

        async Task WriteAsync(string relative, byte[] content)
        {
            string path = Path.Combine(root, relative);
            await File.WriteAllBytesAsync(path, content, cancellationToken);
        }

        await WriteAsync("empty.bin", []);
        await WriteAsync("small.txt", "twelve bytes"u8.ToArray());
        await WriteAsync("64k.bin", DeterministicBytes(65536, seed: 64));
        await WriteAsync("300k.bin", DeterministicBytes(300000, seed: 300));
        await WriteAsync("中文檔名.txt", "unicode content"u8.ToArray());
        await WriteAsync("name with space.txt", "space content"u8.ToArray());
        await WriteAsync(Path.Combine("subdir", "nested.txt"), "nested"u8.ToArray());
        await WriteAsync(Path.Combine("subdir", "deep", "deeper.txt"), "deeper"u8.ToArray());
    }

    /// <summary>8 regular files created by <see cref="CreateLocalTreeAsync"/> (root '.', subdir, and
    /// subdir/deep are attribute-only directory entries, not counted in FilesSent).</summary>
    private const int LocalTreeFileCount = 8;

    /// <summary>Fresh, chowned destination directory under /t/push-live inside the container --
    /// created as root by docker exec, so it must be chowned to syncer for the ssh session to
    /// write into it (mirrors the container's own /t/tree setup in <see cref="SshRsyncContainer"/>).</summary>
    private async Task<string> CreateRemoteDirAsync(string name)
    {
        string remoteDir = $"/t/push-live/{name}";
        var mk = await container.ExecAsync("sh", "-c", $"mkdir -p {remoteDir} && chown -R syncer:syncer /t/push-live");
        Assert.Equal(0, mk.ExitCode);
        return remoteDir;
    }

    private async Task<string> HashInContainerAsync(string remotePath)
    {
        var result = await container.ExecAsync("sh", "-c", $"sha256sum {remotePath}");
        Assert.Equal(0, result.ExitCode);
        return result.StdOut[..64];
    }

    /// <summary>Parses a "&lt;label&gt; N,NNN bytes" line out of real rsync's <c>--stats</c> output.</summary>
    private static long ParseStatBytes(string stdOut, string label)
    {
        string line = stdOut.Split('\n').Single(l => l.StartsWith(label, StringComparison.Ordinal));
        string digits = line[label.Length..].Trim();
        digits = digits[..digits.IndexOf(' ')].Replace(",", "");
        return long.Parse(digits);
    }

    [Fact]
    public async Task Push_Tree_ByteIdenticalOnServer()
    {
        string localRoot = Path.Combine(Path.GetTempPath(), $"rsyncwin-push-tree-{Guid.NewGuid():N}");
        try
        {
            await CreateLocalTreeAsync(localRoot, CancellationToken.None);
            string remoteDir = await CreateRemoteDirAsync("tree");

            var argv = new ServerArgvBuilder { Sender = false, Recurse = true, Paths = [$"{remoteDir}/"] };
            List<EnumeratedEntry> entries = [.. FileEnumerator.Enumerate(localRoot)];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            await using var transport = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
            PushSession.Result result = await PushSession.RunAsync(transport, argv, entries, cts.Token);

            Assert.Equal(LocalTreeFileCount, result.FilesSent);
            Assert.Empty(result.FailedFiles);
            Assert.Equal(0, await transport.WaitForExitAsync(cts.Token));

            // Ground truth from inside the container: hash every file on both sides.
            var remote = await container.ExecAsync(
                "sh", "-c", $"cd {remoteDir} && find . -type f -print0 | sort -z | xargs -0 -r sha256sum");
            Assert.Equal(0, remote.ExitCode);

            string[] lines = remote.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(LocalTreeFileCount, lines.Length);
            foreach (string line in lines)
            {
                string expected = line[..64];
                string relative = line[64..].Trim().TrimStart('.', '/');
                string local = Path.Combine([localRoot, .. relative.Split('/')]);
                byte[] content = await File.ReadAllBytesAsync(local, cts.Token);
                Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(content)));
            }
        }
        finally
        {
            try
            {
                Directory.Delete(localRoot, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    [Fact]
    public async Task Push_DeepLongPath_ReconstructsByteExactOnServer()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("Long-path interop validation requires a Windows test host");

        string localRoot = Path.Combine(Path.GetTempPath(), $"rsyncwin-push-long-{Guid.NewGuid():N}");
        string[] segments = Enumerable.Range(0, 10)
            .Select(i => $"segment-{i:D2}-{new string('x', 24)}")
            .ToArray();

        try
        {
            string current = localRoot;
            foreach (string segment in segments)
            {
                current = Path.Combine(current, segment);
                Directory.CreateDirectory(current);
            }

            string localFile = Path.Combine(current, "payload.txt");
            await File.WriteAllTextAsync(localFile, "long push\n");
            Assert.True(localFile.Length > 260, $"test path must exceed MAX_PATH, got {localFile.Length}");

            string remoteDir = await CreateRemoteDirAsync("long-path");
            string remoteFile = $"{remoteDir}/{string.Join('/', [.. segments, "payload.txt"])}";
            var argv = new ServerArgvBuilder { Sender = false, Recurse = true, Paths = [$"{remoteDir}/"] };
            List<EnumeratedEntry> entries = [.. FileEnumerator.Enumerate(localRoot)];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

            await using var transport = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
            PushSession.Result result = await PushSession.RunAsync(transport, argv, entries, cts.Token);

            Assert.Equal(1, result.FilesSent);
            Assert.Empty(result.FailedFiles);
            Assert.Equal(0, await transport.WaitForExitAsync(cts.Token));
            Assert.Equal(
                Convert.ToHexStringLower(SHA256.HashData("long push\n"u8.ToArray())),
                await HashInContainerAsync(remoteFile));
        }
        finally
        {
            try
            {
                Directory.Delete(localRoot, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    /// <summary>
    /// P5-mirrored mtime+size fast path, push direction, live: a second push of the SAME local tree
    /// (real, unrounded NTFS mtimes) to the same destination must request nothing from the real
    /// server's quick-check -- zero files sent, zero literal bytes. If this fails, that is a product
    /// bug in the XMIT_MOD_NSEC path, not a test to weaken.
    /// </summary>
    [Fact]
    public async Task Push_SecondRun_SameDestination_TransfersNothing()
    {
        string localRoot = Path.Combine(Path.GetTempPath(), $"rsyncwin-push-rerun-{Guid.NewGuid():N}");
        try
        {
            await CreateLocalTreeAsync(localRoot, CancellationToken.None);
            string remoteDir = await CreateRemoteDirAsync("rerun");
            var argv = new ServerArgvBuilder { Sender = false, Recurse = true, Paths = [$"{remoteDir}/"] };
            List<EnumeratedEntry> entries = [.. FileEnumerator.Enumerate(localRoot)];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            await using (var transport1 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build())))
            {
                PushSession.Result first = await PushSession.RunAsync(transport1, argv, entries, cts.Token);
                Assert.Equal(LocalTreeFileCount, first.FilesSent);
                Assert.Equal(0, await transport1.WaitForExitAsync(cts.Token));
            }

            // Transport is per-connection: a fresh transport + fresh session for the re-run, same
            // entries (same real mtimes captured once) since nothing changed locally.
            await using var transport2 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
            PushSession.Result second = await PushSession.RunAsync(transport2, argv, entries, cts.Token);

            Assert.Equal(0, second.FilesSent);
            Assert.Equal(0, second.LiteralBytes);
            Assert.Empty(second.FailedFiles);
            Assert.Equal(0, await transport2.WaitForExitAsync(cts.Token));
        }
        finally
        {
            try
            {
                Directory.Delete(localRoot, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    /// <summary>
    /// P6 delta-efficiency gate, push direction, live: a 4 MiB local file with one modified 8-byte
    /// region re-pushes only the dirty block(s) against the existing server-side basis. Cross-checked
    /// byte-for-byte against a real local rsync run over the same basis, entirely inside the
    /// container (mirrors <see cref="SshPullInteropTests"/>'s technique): the server-side basis our
    /// own push #1 deposited is snapshotted, the same 8-byte edit is replayed on that snapshot with
    /// <c>dd</c>, and a real rsync client compares literal/matched bytes for that pair.
    /// </summary>
    [Fact]
    public async Task Push_OneModifiedBlockOfLargeFile_TransfersOnlyDelta()
    {
        const long FileLength = 4 * 1024 * 1024; // 4 MiB -> blength = 2048 (sum_sizes_sqroot)
        const long BlockLength = 2048;
        const long ModifyOffset = 2_000_000;

        string localRoot = Path.Combine(Path.GetTempPath(), $"rsyncwin-push-delta-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(localRoot);
            byte[] original = DeterministicBytes((int)FileLength, seed: 4096);
            string localPath = Path.Combine(localRoot, "big.bin");
            await File.WriteAllBytesAsync(localPath, original);

            string remoteDir = await CreateRemoteDirAsync("delta");
            var argv = new ServerArgvBuilder { Sender = false, Recurse = true, Paths = [$"{remoteDir}/"] };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));

            // ---- push #1: full transfer establishes the basis on the server --------------------
            await using (var transport1 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build())))
            {
                List<EnumeratedEntry> entries1 = [.. FileEnumerator.Enumerate(localRoot)];
                PushSession.Result first = await PushSession.RunAsync(transport1, argv, entries1, cts.Token);
                Assert.Equal(1, first.FilesSent);
                Assert.Equal(0, await transport1.WaitForExitAsync(cts.Token));
            }

            string originalHash = Convert.ToHexStringLower(SHA256.HashData(original));
            Assert.Equal(originalHash, await HashInContainerAsync($"{remoteDir}/big.bin"));

            // Snapshot the pristine server-side basis aside for the real-rsync cross-check below.
            var snapshot = await container.ExecAsync("sh", "-c",
                "mkdir -p /t/push-live/delta-ref && " +
                $"cp {remoteDir}/big.bin /t/push-live/delta-ref/orig.bin");
            Assert.Equal(0, snapshot.ExitCode);

            // ---- modify one aligned region locally, force a real mtime change ------------------
            // (an in-place same-length write can land in the same 1-second mtime bucket as the
            // original -- see the pull test's identical caution -- so the new mtime is pushed
            // years forward, never left to chance).
            byte[] modified = (byte[])original.Clone();
            "QQQQQQQQ"u8.CopyTo(modified.AsSpan((int)ModifyOffset));
            await File.WriteAllBytesAsync(localPath, modified);
            File.SetLastWriteTimeUtc(localPath, DateTime.UtcNow.AddYears(5));
            string modifiedHash = Convert.ToHexStringLower(SHA256.HashData(modified));
            Assert.NotEqual(originalHash, modifiedHash);

            // ---- push #2: delta transfer against the existing server-side basis ----------------
            List<EnumeratedEntry> entries2 = [.. FileEnumerator.Enumerate(localRoot)];
            await using var transport2 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
            PushSession.Result second = await PushSession.RunAsync(transport2, argv, entries2, cts.Token);
            Assert.Equal(0, await transport2.WaitForExitAsync(cts.Token));

            Assert.Equal(1, second.FilesSent);
            Assert.Empty(second.FailedFiles);
            Assert.Equal(modifiedHash, await HashInContainerAsync($"{remoteDir}/big.bin"));

            // The 8 modified bytes can dirty at most 2 blocks at a boundary; allow 3 for slack.
            long minMatched = FileLength - 3 * BlockLength;
            long maxLiteral = 3 * BlockLength + 8192;
            Assert.True(second.MatchedBytes >= minMatched,
                $"expected matched >= {minMatched}, got {second.MatchedBytes}");
            Assert.True(second.LiteralBytes <= maxLiteral,
                $"expected literal <= {maxLiteral}, got {second.LiteralBytes}");
            Assert.True(second.LiteralBytes < FileLength / 2,
                $"literal {second.LiteralBytes} should be far below the {FileLength}-byte file");

            // ---- cross-check against a real local rsync run over the same basis, in-container --
            // Both copies must NOT collide in the same 1-second mtime bucket (plain `cp` sets mtime
            // to "now"; two `cp`s microseconds apart landing in the same second would make rsync's
            // own quick-check skip the transfer entirely -- verified empirically before adding this).
            var modifyRef = await container.ExecAsync("sh", "-c",
                "mkdir -p /t/push-live/delta-ref/modified /t/push-live/delta-ref/dest && " +
                "cp /t/push-live/delta-ref/orig.bin /t/push-live/delta-ref/modified/big.bin && " +
                $"printf 'QQQQQQQQ' | dd of=/t/push-live/delta-ref/modified/big.bin bs=1 seek={ModifyOffset} conv=notrunc 2>/dev/null && " +
                "touch -d '2030-01-01 00:00:00' /t/push-live/delta-ref/modified/big.bin && " +
                "cp /t/push-live/delta-ref/orig.bin /t/push-live/delta-ref/dest/big.bin && " +
                "touch -d '2020-01-01 00:00:00' /t/push-live/delta-ref/dest/big.bin");
            Assert.Equal(0, modifyRef.ExitCode);
            var realRsync = await container.ExecAsync(
                "rsync", "--no-whole-file", "--stats",
                "/t/push-live/delta-ref/modified/big.bin", "/t/push-live/delta-ref/dest/");
            Assert.Equal(0, realRsync.ExitCode);

            long realLiteral = ParseStatBytes(realRsync.StdOut, "Literal data:");
            long realMatched = ParseStatBytes(realRsync.StdOut, "Matched data:");
            Assert.Equal(realMatched, second.MatchedBytes);
            Assert.Equal(realLiteral, second.LiteralBytes);
        }
        finally
        {
            try
            {
                Directory.Delete(localRoot, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    /// <summary>
    /// P7 push-side vanished-source seam, live: <c>entries</c> capture each file's AbsolutePath at
    /// enumeration time, but <see cref="PushSession"/> only reads the bytes later, lazily, when the
    /// remote generator actually requests that ndx (see <c>PushSession.ReplySender.ReplyAsync</c>'s
    /// "Capture-unpinned" remark). Deleting a source file in between exercises exactly that fallback
    /// against a REAL remote generator for the first time -- this scenario has no capture vector.
    /// </summary>
    [Fact]
    public async Task Push_SourceFileVanishesMidSession()
    {
        string localRoot = Path.Combine(Path.GetTempPath(), $"rsyncwin-push-vanish-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(localRoot);
            await File.WriteAllBytesAsync(Path.Combine(localRoot, "keep.txt"), "keep"u8.ToArray());
            string vanishingPath = Path.Combine(localRoot, "vanish.txt");
            await File.WriteAllBytesAsync(vanishingPath, "gone"u8.ToArray());

            string remoteDir = await CreateRemoteDirAsync("vanish");
            var argv = new ServerArgvBuilder { Sender = false, Recurse = true, Paths = [$"{remoteDir}/"] };
            List<EnumeratedEntry> entries = [.. FileEnumerator.Enumerate(localRoot)];

            // Delete AFTER enumeration (AbsolutePath already captured) but BEFORE the transfer runs.
            File.Delete(vanishingPath);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using var transport = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
            PushSession.Result result = await PushSession.RunAsync(transport, argv, entries, cts.Token);

            Assert.Contains("vanish.txt", result.FailedFiles);
            Assert.Equal(1, result.FilesSent); // keep.txt still transferred normally

            // Mirrors the CLI's own exit-code mapping (Program.cs): any FailedFiles maps to
            // PartialTransferError (23) -- the session must complete with that mapping available,
            // not hang or abort.
            int mappedExitCode = result.FailedFiles.Count > 0
                ? (int)RsyncExitCode.PartialTransferError
                : (int)RsyncExitCode.Ok;
            Assert.Equal(23, mappedExitCode);
        }
        finally
        {
            try
            {
                Directory.Delete(localRoot, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
