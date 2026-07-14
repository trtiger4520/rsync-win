using System.Security.Cryptography;
using RsyncWin.Engine;
using RsyncWin.Fs;
using RsyncWin.Protocol.Delta;
using RsyncWin.Protocol.Session;
using RsyncWin.Transport;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Live P10 gates over ssh.exe against a real rsync 3.4.3: --secluded-args (-s) with a spaced remote
/// path, push --checksum (transfer then re-push transfers nothing by F_SUM), and push --delete
/// (extraneous entries deleted and reported via MSG_DELETED). Phase-boundary bugs hang, so every
/// test is under a hang-detection timeout.
/// </summary>
[Trait("Category", "Interop")]
public sealed class SshP10InteropTests(SshRsyncContainer container) : IClassFixture<SshRsyncContainer>
{
    private static byte[] DeterministicBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static async Task CreateLocalTreeAsync(string root, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(root, "subdir"));
        async Task WriteAsync(string relative, byte[] content) =>
            await File.WriteAllBytesAsync(Path.Combine(root, relative), content, cancellationToken);

        await WriteAsync("small.txt", "twelve bytes"u8.ToArray());
        await WriteAsync("64k.bin", DeterministicBytes(65536, seed: 64));
        await WriteAsync("中文檔名.txt", "unicode content"u8.ToArray());
        await WriteAsync(Path.Combine("subdir", "nested.txt"), "nested"u8.ToArray());
    }

    private const int LocalTreeFileCount = 4;

    private async Task<string> CreateRemoteDirAsync(string name)
    {
        string remoteDir = $"/t/p10-live/{name}";
        var mk = await container.ExecAsync("sh", "-c", $"mkdir -p {remoteDir} && chown -R syncer:syncer /t/p10-live");
        Assert.Equal(0, mk.ExitCode);
        return remoteDir;
    }

    // ---- --secluded-args (-s) --------------------------------------------------------------------

    /// <summary>
    /// Pull a file whose remote directory name contains a space, with -s. Without secluded-args the
    /// remote shell would split "space live" into two arguments; -s sends the path as a pre-handshake
    /// NUL list, keeping it intact. Gate: the file lands locally byte-identical and the remote exits 0.
    /// </summary>
    [Fact]
    public async Task SecludedArgs_PullSpacedRemotePath_ByteIdentical()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        // Create a remote dir + file whose paths contain spaces.
        var setup = await container.ExecAsync("sh", "-c",
            "mkdir -p '/t/space live/inner dir' && " +
            "printf 'spaced payload' > '/t/space live/inner dir/file name.txt' && " +
            "chown -R syncer:syncer '/t/space live'");
        Assert.Equal(0, setup.ExitCode);

        string remotePath = "/t/space live/inner dir/";
        string localDest = Path.Combine(Path.GetTempPath(), $"rsyncwin-secluded-{Guid.NewGuid():N}");
        try
        {
            var argv = new ServerArgvBuilder
            {
                Sender = true, Recurse = true, SecludedArgs = true, Paths = [remotePath],
            };
            var handshake = new HandshakeOptions { SecludedArgs = [remotePath] };

            await using var transport = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
            PullSession.Result result = await PullSession.RunAsync(
                transport, argv, localDest, cts.Token, handshake: handshake);

            Assert.Equal(0, await transport.WaitForExitAsync(cts.Token));
            Assert.Empty(result.FailedFiles);

            byte[] pulled = await File.ReadAllBytesAsync(Path.Combine(localDest, "file name.txt"), cts.Token);
            Assert.Equal("spaced payload"u8.ToArray(), pulled);
        }
        finally
        {
            TryDeleteDir(localDest);
        }
    }

    // ---- push --checksum -------------------------------------------------------------------------

    /// <summary>
    /// Push with -c: files transfer to a fresh destination byte-identically, and a second -c push of
    /// the SAME content transfers nothing (the flist F_SUM matches the server's basis). Then a
    /// content change is transferred even though size+mtime are held constant — the whole point of -c.
    /// </summary>
    [Fact]
    public async Task PushChecksum_TransfersThenReTransfersNothing_ButChangedContentTransfers()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string localRoot = Path.Combine(Path.GetTempPath(), $"rsyncwin-pushc-{Guid.NewGuid():N}");
        try
        {
            await CreateLocalTreeAsync(localRoot, cts.Token);
            string remoteDir = await CreateRemoteDirAsync("checksum");
            var argv = new ServerArgvBuilder
            {
                Sender = false, Recurse = true, Checksum = true, Paths = [$"{remoteDir}/"],
            };

            // ---- push #1: full -c transfer -----------------------------------------------------
            await using (var t1 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build())))
            {
                PushSession.Result first = await PushSession.RunAsync(
                    t1, argv, [.. FileEnumerator.Enumerate(localRoot)], cts.Token);
                Assert.Equal(LocalTreeFileCount, first.FilesSent);
                Assert.Empty(first.FailedFiles);
                Assert.Equal(0, await t1.WaitForExitAsync(cts.Token));
            }

            string smallHash = Convert.ToHexStringLower(
                SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(localRoot, "small.txt"), cts.Token)));
            var remoteHash = await container.ExecAsync("sh", "-c", $"sha256sum {remoteDir}/small.txt");
            Assert.Equal(smallHash, remoteHash.StdOut[..64]);

            // ---- push #2: same content, -c must transfer NOTHING -------------------------------
            await using (var t2 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build())))
            {
                PushSession.Result second = await PushSession.RunAsync(
                    t2, argv, [.. FileEnumerator.Enumerate(localRoot)], cts.Token);
                Assert.Equal(0, second.FilesSent);
                Assert.Equal(0, second.LiteralBytes);
                Assert.Equal(0, await t2.WaitForExitAsync(cts.Token));
            }

            // ---- change content but hold size+mtime; -c must still transfer it -----------------
            string smallPath = Path.Combine(localRoot, "small.txt");
            DateTime mtime = File.GetLastWriteTimeUtc(smallPath);
            await File.WriteAllBytesAsync(smallPath, "TWELVE BYTES"u8.ToArray(), cts.Token); // same 12-byte length
            File.SetLastWriteTimeUtc(smallPath, mtime);

            await using (var t3 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build())))
            {
                PushSession.Result third = await PushSession.RunAsync(
                    t3, argv, [.. FileEnumerator.Enumerate(localRoot)], cts.Token);
                Assert.Equal(1, third.FilesSent); // -c saw the content change despite equal size+mtime
                Assert.Equal(0, await t3.WaitForExitAsync(cts.Token));
            }

            string changedHash = Convert.ToHexStringLower(SHA256.HashData("TWELVE BYTES"u8.ToArray()));
            var remoteChanged = await container.ExecAsync("sh", "-c", $"sha256sum {remoteDir}/small.txt");
            Assert.Equal(changedHash, remoteChanged.StdOut[..64]);
        }
        finally
        {
            TryDeleteDir(localRoot);
        }
    }

    /// <summary>
    /// Reviewer finding 1 fix: under <c>-c</c> the F_SUM precompute opens every source file up front.
    /// A file that vanishes/locks between enumeration and that precompute must NOT abort the whole
    /// push (routine on Windows: locked PST/DB files) — rsync emits a zero F_SUM and carries on with
    /// the file recorded as failed (exit 23). Here a source file is deleted after enumeration; the
    /// push must still transfer the others and report the vanished one, not crash with zero sent.
    /// </summary>
    [Fact]
    public async Task PushChecksum_VanishedSourceFile_DoesNotAbort_TransfersTheRest()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string localRoot = Path.Combine(Path.GetTempPath(), $"rsyncwin-cvanish-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(localRoot);
            await File.WriteAllBytesAsync(Path.Combine(localRoot, "keep.txt"), "keep"u8.ToArray(), cts.Token);
            string vanishing = Path.Combine(localRoot, "vanish.txt");
            await File.WriteAllBytesAsync(vanishing, "gone"u8.ToArray(), cts.Token);

            string remoteDir = await CreateRemoteDirAsync("cvanish");
            var argv = new ServerArgvBuilder { Sender = false, Recurse = true, Checksum = true, Paths = [$"{remoteDir}/"] };
            List<EnumeratedEntry> entries = [.. FileEnumerator.Enumerate(localRoot)];

            File.Delete(vanishing); // after enumeration, before the -c F_SUM precompute + transfer

            await using var transport = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
            PushSession.Result result = await PushSession.RunAsync(transport, argv, entries, cts.Token);

            Assert.Equal(0, await transport.WaitForExitAsync(cts.Token));
            Assert.Contains("vanish.txt", result.FailedFiles);
            Assert.Equal(1, result.FilesSent); // keep.txt still went through

            var check = await container.ExecAsync("sh", "-c", $"sha256sum {remoteDir}/keep.txt");
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("keep"u8.ToArray())), check.StdOut[..64]);
        }
        finally
        {
            TryDeleteDir(localRoot);
        }
    }

    // ---- push --delete ---------------------------------------------------------------------------

    /// <summary>
    /// Push with --delete: an extraneous file on the receiver is deleted and reported via MSG_DELETED
    /// (surfaced on <see cref="PushSession.Result.DeletedPaths"/>), while the real kept files are
    /// untouched and the remote exits 0.
    /// </summary>
    [Fact]
    public async Task PushDelete_RemovesExtraneousReceiverEntries_AndReportsThem()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string localRoot = Path.Combine(Path.GetTempPath(), $"rsyncwin-pushdel-{Guid.NewGuid():N}");
        try
        {
            await CreateLocalTreeAsync(localRoot, cts.Token);
            string remoteDir = await CreateRemoteDirAsync("delete");
            var argv = new ServerArgvBuilder
            {
                Sender = false, Recurse = true, Delete = true, Paths = [$"{remoteDir}/"],
            };

            // Seed the destination = source, then plant one extraneous file + one extraneous dir.
            await using (var t1 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath,
                container.SshArgs(new ServerArgvBuilder { Sender = false, Recurse = true, Paths = [$"{remoteDir}/"] }.Build())))
            {
                PushSession.Result seed = await PushSession.RunAsync(
                    t1, new ServerArgvBuilder { Sender = false, Recurse = true, Paths = [$"{remoteDir}/"] },
                    [.. FileEnumerator.Enumerate(localRoot)], cts.Token);
                Assert.Equal(0, await t1.WaitForExitAsync(cts.Token));
            }
            var plant = await container.ExecAsync("sh", "-c",
                $"printf 'extra' > {remoteDir}/extra.txt && " +
                $"mkdir -p {remoteDir}/extradir && printf 'inside' > {remoteDir}/extradir/inside.txt && " +
                $"chown -R syncer:syncer {remoteDir}");
            Assert.Equal(0, plant.ExitCode);

            // ---- push with --delete: the extras must be pruned server-side ---------------------
            await using var transport = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
            PushSession.Result result = await PushSession.RunAsync(
                transport, argv, [.. FileEnumerator.Enumerate(localRoot)], cts.Token);
            Assert.Equal(0, await transport.WaitForExitAsync(cts.Token));
            Assert.Empty(result.FailedFiles);

            Assert.Contains("extra.txt", result.DeletedPaths);
            Assert.Contains(result.DeletedPaths, p => p.TrimEnd('/') == "extradir");

            // Ground truth: the extras are gone, a kept file remains.
            var check = await container.ExecAsync("sh", "-c",
                $"test ! -e {remoteDir}/extra.txt && test ! -e {remoteDir}/extradir && test -e {remoteDir}/small.txt && echo OK");
            Assert.Equal("OK", check.StdOut.Trim());
        }
        finally
        {
            TryDeleteDir(localRoot);
        }
    }

    // ---- -z compression (zlibx) ------------------------------------------------------------------

    private static readonly HandshakeOptions ZlibxHandshake = new()
    {
        Compression = CompressionMethod.Zlibx,
    };

    /// <summary>
    /// Pull with <c>-z</c> (zlibx): the real rsync sender compresses the token stream; our zlibx
    /// decoder must reconstruct every file byte-identically and exit 0. The tree mixes a highly
    /// compressible file, a run file, and an incompressible blob so both the deflated and
    /// stored-block paths are exercised.
    /// </summary>
    [Fact]
    public async Task Compress_PullTree_ReconstructsByteIdentical()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var setup = await container.ExecAsync("sh", "-c",
            "mkdir -p /t/zsrc && " +
            "yes 'the quick brown fox jumps over the lazy dog' | head -c 200000 > /t/zsrc/repeat.txt && " +
            "head -c 100000 /dev/zero | tr '\\0' 'A' > /t/zsrc/runs.txt && " +
            "head -c 65536 /dev/urandom > /t/zsrc/random.bin && " +
            "printf 'small compressible payload aaaaaaaaaa\\n' > /t/zsrc/small.txt && " +
            "chown -R syncer:syncer /t/zsrc");
        Assert.Equal(0, setup.ExitCode);

        string localDest = Path.Combine(Path.GetTempPath(), $"rsyncwin-zpull-{Guid.NewGuid():N}");
        try
        {
            var argv = new ServerArgvBuilder { Sender = true, Recurse = true, Compress = true, Paths = ["/t/zsrc/"] };
            await using var transport = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
            PullSession.Result result = await PullSession.RunAsync(
                transport, argv, localDest, cts.Token, handshake: ZlibxHandshake);

            Assert.Equal(0, await transport.WaitForExitAsync(cts.Token));
            Assert.Empty(result.FailedFiles);

            var remote = await container.ExecAsync("sh", "-c",
                "cd /t/zsrc && find . -type f -print0 | sort -z | xargs -0 -r sha256sum");
            Assert.Equal(0, remote.ExitCode);
            foreach (string line in remote.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string expected = line[..64];
                string relative = line[64..].Trim().TrimStart('.', '/');
                byte[] pulled = await File.ReadAllBytesAsync(Path.Combine(localDest, relative), cts.Token);
                Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(pulled)));
            }
        }
        finally
        {
            TryDeleteDir(localDest);
        }
    }

    /// <summary>
    /// Push with <c>-z</c> (zlibx): our zlibx encoder compresses the token stream; a real rsync
    /// receiver must reconstruct every file byte-identically, and a second -z push transfers nothing.
    /// Byte-exact wire replay is impossible (deflate output is implementation-defined), so this is the
    /// gate for the encoder: a real server decodes what we produce.
    /// </summary>
    [Fact]
    public async Task Compress_PushTree_ServerReconstructs_AndRePushTransfersNothing()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        string localRoot = Path.Combine(Path.GetTempPath(), $"rsyncwin-zpush-{Guid.NewGuid():N}");
        try
        {
            await CreateLocalTreeAsync(localRoot, cts.Token);
            // Add a large compressible file so the deflate path carries real content.
            await File.WriteAllBytesAsync(
                Path.Combine(localRoot, "big.txt"),
                System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("the quick brown fox 0123456789\n", 8000))),
                cts.Token);
            string remoteDir = await CreateRemoteDirAsync("zpush");
            var argv = new ServerArgvBuilder { Sender = false, Recurse = true, Compress = true, Paths = [$"{remoteDir}/"] };

            await using (var t1 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build())))
            {
                PushSession.Result first = await PushSession.RunAsync(
                    t1, argv, [.. FileEnumerator.Enumerate(localRoot)], cts.Token, handshake: ZlibxHandshake);
                Assert.Equal(LocalTreeFileCount + 1, first.FilesSent); // 4 tree files + big.txt
                Assert.Empty(first.FailedFiles);
                Assert.Equal(0, await t1.WaitForExitAsync(cts.Token));
            }

            // Ground truth: every file byte-identical on the server.
            var remote = await container.ExecAsync("sh", "-c",
                $"cd {remoteDir} && find . -type f -print0 | sort -z | xargs -0 -r sha256sum");
            Assert.Equal(0, remote.ExitCode);
            foreach (string line in remote.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string expected = line[..64];
                string relative = line[64..].Trim().TrimStart('.', '/');
                byte[] local = await File.ReadAllBytesAsync(Path.Combine([localRoot, .. relative.Split('/')]), cts.Token);
                Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(local)));
            }

            // Re-push with -z: nothing changed → zero files sent.
            await using var t2 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
            PushSession.Result second = await PushSession.RunAsync(
                t2, argv, [.. FileEnumerator.Enumerate(localRoot)], cts.Token, handshake: ZlibxHandshake);
            Assert.Equal(0, second.FilesSent);
            Assert.Equal(0, await t2.WaitForExitAsync(cts.Token));
        }
        finally
        {
            TryDeleteDir(localRoot);
        }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
