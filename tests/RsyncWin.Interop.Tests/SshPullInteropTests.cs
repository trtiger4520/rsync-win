using System.Security.Cryptography;
using RsyncWin.Engine;
using RsyncWin.Fs;
using RsyncWin.Protocol.Session;
using RsyncWin.Transport;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// THE interop milestone, live: pull a whole tree from a real rsync over ssh.exe onto the Windows
/// filesystem, byte-identical (SHA-256 per file against hashes computed inside the container),
/// with the remote side exiting 0.
/// </summary>
// No CLI end-to-end pull test here: -e/--rsh now accepts a full remote-shell command (it can carry
// the container's port/key/known-hosts options directly), and the CLI-over-ssh round trip — including
// that passthrough via an ssh.cmd wrapper — is already covered by CliApplicationInteropTests. This
// class stays focused on the engine-level pull milestone.
[Trait("Category", "Interop")]
public sealed class SshPullInteropTests(SshRsyncContainer container) : IClassFixture<SshRsyncContainer>
{
    private async Task<string> HashInContainerAsync(string remotePath)
    {
        var result = await container.ExecAsync("sh", "-c", $"sha256sum {remotePath}");
        Assert.Equal(0, result.ExitCode);
        return result.StdOut[..64];
    }

    [Fact]
    [Trait("Profile", "Smoke")]
    public async Task Pull_WholeTree_ByteIdentical_AndExitsZero()
    {
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-pull-{Guid.NewGuid():N}");
        try
        {
            var argv = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/tree/"] };
            await using var transport = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

            PullSession.Result result = await PullSession.RunAsync(transport, argv, dest, cts.Token);

            Assert.Equal(7, result.TransferredFiles);
            Assert.Empty(result.RedoneFiles);
            Assert.Empty(result.FailedFiles);
            Assert.Empty(result.NotSentByServer);
            Assert.Equal(0, result.IoErrorFlags);
            Assert.Equal(0, await transport.WaitForExitAsync(cts.Token));

            // Ground truth from inside the container: hash every file on both sides.
            // NUL-separated: one filename contains a space, which plain xargs would split.
            var remote = await container.ExecAsync(
                "sh", "-c", "cd /t/tree && find . -type f -print0 | sort -z | xargs -0 -r sha256sum");
            Assert.Equal(0, remote.ExitCode);

            string[] lines = remote.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(7, lines.Length);
            foreach (string line in lines)
            {
                string expected = line[..64];
                string relative = line[64..].Trim().TrimStart('.', '/');
                string local = Path.Combine([dest, .. relative.Split('/')]);
                byte[] content = await File.ReadAllBytesAsync(local, cts.Token);
                Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(content)));
            }
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
    /// P5 mtime+size fast path, live: a second pull against a destination that already holds an
    /// up-to-date copy must request nothing — zero transferred files, zero bytes.
    /// </summary>
    [Fact]
    [Trait("Profile", "Smoke")]
    public async Task Pull_SecondRun_SameDestination_TransfersNothing()
    {
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-pull-rerun-{Guid.NewGuid():N}");
        try
        {
            var argv = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/tree/"] };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

            await using (var transport1 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build())))
            {
                PullSession.Result first = await PullSession.RunAsync(transport1, argv, dest, cts.Token);
                Assert.Equal(7, first.TransferredFiles);
                Assert.True(first.TransferredBytes > 0);
                Assert.Equal(0, await transport1.WaitForExitAsync(cts.Token));
            }

            // Transport is per-connection: a fresh transport + fresh session for the re-run.
            await using var transport2 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
            PullSession.Result second = await PullSession.RunAsync(transport2, argv, dest, cts.Token);

            Assert.Equal(0, second.TransferredFiles);
            Assert.Equal(0, second.TransferredBytes);
            Assert.Empty(second.RedoneFiles);
            Assert.Empty(second.FailedFiles);
            Assert.Equal(0, await transport2.WaitForExitAsync(cts.Token));
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
    /// P5 Windows path sanitization, live: names hostile to Windows (backslash, colon, a reserved
    /// device name, a trailing dot) must land under the destination, mapped by
    /// <see cref="WindowsPathMapper"/> — never escape the destination directory.
    /// </summary>
    [Fact]
    public async Task Pull_HostileNames_AreSanitizedAndStayUnderDestination()
    {
        string[] hostileNames = ["back\\slash.txt", "colon:name.txt", "CON", "trailing.dot."];

        var mkTree = await container.ExecAsync(
            "sh", "-c",
            "mkdir -p /t/hostile && cd /t/hostile && " +
            "printf 'a' > \"$(printf 'back\\\\slash.txt')\" && " +
            "printf 'b' > 'colon:name.txt' && " +
            "printf 'c' > CON && " +
            "printf 'd' > 'trailing.dot.' && " +
            "chown -R syncer:syncer /t/hostile");
        Assert.Equal(0, mkTree.ExitCode);

        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-pull-hostile-{Guid.NewGuid():N}");
        try
        {
            var argv = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/hostile/"] };
            await using var transport = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

            PullSession.Result result = await PullSession.RunAsync(transport, argv, dest, cts.Token);

            Assert.Equal(0, await transport.WaitForExitAsync(cts.Token));

            string root = Path.GetFullPath(dest);
            if (!Path.EndsInDirectorySeparator(root))
                root += Path.DirectorySeparatorChar;
            foreach (string file in Directory.GetFiles(dest, "*", SearchOption.AllDirectories))
            {
                Assert.StartsWith(root, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase);
            }

            foreach (string hostileName in hostileNames)
            {
                (string mapped, _) = WindowsPathMapper.Map(hostileName);
                string expectedPath = Path.Combine(dest, mapped);
                Assert.True(File.Exists(expectedPath), $"expected mapped file to exist: {expectedPath}");
            }

            foreach (string hostileName in hostileNames)
                Assert.Contains(hostileName, result.MappedNames);
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
    public async Task Pull_DeepLongPath_ReconstructsByteExactOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw Xunit.Sdk.SkipException.ForSkip("Long-path interop validation requires a Windows test host");

        string[] segments = Enumerable.Range(0, 10)
            .Select(i => $"segment-{i:D2}-{new string('x', 24)}")
            .ToArray();
        string relativeFile = string.Join('/', [.. segments, "payload.txt"]);
        string remoteRoot = "/t/longpull";
        string remoteFile = $"{remoteRoot}/{relativeFile}";
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-pull-long-{Guid.NewGuid():N}");

        try
        {
            var setup = await container.ExecAsync(
                "sh", "-c",
                $"rm -rf {remoteRoot} && mkdir -p {remoteRoot}/{string.Join('/', segments)} && " +
                $"printf 'long pull\\n' > {remoteFile} && chown -R syncer:syncer {remoteRoot}");
            Assert.Equal(0, setup.ExitCode);

            var argv = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = [$"{remoteRoot}/"] };
            await using var transport = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

            PullSession.Result result = await PullSession.RunAsync(transport, argv, dest, cts.Token);

            Assert.Equal(1, result.TransferredFiles);
            Assert.Empty(result.FailedFiles);
            Assert.Equal(0, await transport.WaitForExitAsync(cts.Token));

            string localFile = Path.Combine([dest, .. segments, "payload.txt"]);
            Assert.True(localFile.Length > 260, $"test path must exceed MAX_PATH, got {localFile.Length}");
            Assert.Equal("long pull\n", await File.ReadAllTextAsync(localFile, cts.Token));
            Assert.Equal(
                Convert.ToHexStringLower(SHA256.HashData("long pull\n"u8.ToArray())),
                await HashInContainerAsync(remoteFile));

            string root = Path.GetFullPath(dest);
            string rootWithSeparator = Path.EndsInDirectorySeparator(root)
                ? root
                : root + Path.DirectorySeparatorChar;
            foreach (string path in Directory.GetFiles(dest, "*", SearchOption.AllDirectories))
                Assert.StartsWith(rootWithSeparator, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await container.ExecAsync("rm", "-rf", remoteRoot);
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
    /// P6 delta-efficiency gate, live: a 4 MiB basis with one modified 8-byte region re-pulls only
    /// the dirty block(s), not the whole file. Cross-checked byte-for-byte against a real local
    /// rsync run over the same basis inside the container (same sender, same block sizing, so the
    /// literal/matched split must agree exactly).
    /// </summary>
    [Fact]
    public async Task Pull_OneModifiedBlockOfLargeFile_TransfersOnlyDeltaAndMatchesRealRsyncStats()
    {
        const long FileLength = 4 * 1024 * 1024; // 4 MiB -> blength = 2048 (sqrt(4 MiB) exactly, sum_sizes_sqroot)
        const long BlockLength = 2048;
        const long ModifyOffset = 2_000_000;

        // Separate directories: "src" is the only thing pulled (must contain exactly one file, or
        // TransferredFiles would count the orig/ref copies too); "orig" keeps the pristine basis
        // aside for the real-rsync cross-check below.
        // openssl aes-128-ctr keystream idiom from test-fixtures/capture/capture.sh: deterministic,
        // reproducible pseudo-random bytes without shipping a fixture into the repo.
        var setup = await container.ExecAsync("sh", "-c",
            "apk add --no-cache openssl >/dev/null 2>&1 && " +
            "mkdir -p /t/delta-live/src /t/delta-live/orig /t/delta-live/ref && cd /t/delta-live && " +
            "openssl enc -aes-128-ctr -pass 'pass:rsyncwin-p6-delta-src' -nosalt </dev/zero 2>/dev/null " +
            $"| head -c {FileLength} > src/big.bin && " +
            "cp src/big.bin orig/big.bin && " + // pristine basis, kept aside for the real-rsync cross-check below
            "chown -R syncer:syncer /t/delta-live");
        Assert.Equal(0, setup.ExitCode);

        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-pull-delta-{Guid.NewGuid():N}");
        try
        {
            var argv = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/delta-live/src/"] };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));

            // ---- pull #1: full transfer establishes the basis on disk --------------------------
            await using (var transport1 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build())))
            {
                PullSession.Result first = await PullSession.RunAsync(transport1, argv, dest, cts.Token);
                Assert.Equal(1, first.TransferredFiles);
                Assert.Equal(0, await transport1.WaitForExitAsync(cts.Token));
            }

            string originalHash = await HashInContainerAsync("/t/delta-live/orig/big.bin");
            Assert.Equal(originalHash, LocalSha256(Path.Combine(dest, "big.bin")));

            // ---- modify one aligned region of the SOURCE, force the mtime to actually change ---
            // (an in-place same-length write can land in the same 1-second mtime bucket as the
            // original, which would make the mtime+size fast path skip the file entirely).
            var modify = await container.ExecAsync("sh", "-c",
                $"printf 'QQQQQQQQ' | dd of=/t/delta-live/src/big.bin bs=1 seek={ModifyOffset} conv=notrunc 2>/dev/null && " +
                "touch -d '2030-01-01 00:00:00' /t/delta-live/src/big.bin");
            Assert.Equal(0, modify.ExitCode);
            string modifiedHash = await HashInContainerAsync("/t/delta-live/src/big.bin");
            Assert.NotEqual(originalHash, modifiedHash);

            // ---- pull #2: delta transfer against the existing basis ----------------------------
            await using (var transport2 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build())))
            {
                PullSession.Result second = await PullSession.RunAsync(transport2, argv, dest, cts.Token);
                Assert.Equal(0, await transport2.WaitForExitAsync(cts.Token));

                Assert.Equal(1, second.TransferredFiles);
                Assert.Equal(modifiedHash, LocalSha256(Path.Combine(dest, "big.bin")));

                // The 8 modified bytes can dirty at most 2 blocks at a boundary; allow 3 for slack.
                long minMatched = FileLength - 3 * BlockLength;
                long maxLiteral = 3 * BlockLength + 8192;
                Assert.True(second.MatchedBytes >= minMatched,
                    $"expected matched >= {minMatched}, got {second.MatchedBytes}");
                Assert.True(second.TransferredBytes <= maxLiteral,
                    $"expected literal <= {maxLiteral}, got {second.TransferredBytes}");
                Assert.True(second.TransferredBytes < FileLength / 2,
                    $"literal {second.TransferredBytes} should be far below the {FileLength}-byte file");

                // ---- cross-check against a real local rsync run over the same basis ------------
                var refCopy = await container.ExecAsync(
                    "sh", "-c", "cp /t/delta-live/orig/big.bin /t/delta-live/ref/big.bin");
                Assert.Equal(0, refCopy.ExitCode);
                var realRsync = await container.ExecAsync(
                    "rsync", "--no-whole-file", "--stats", "/t/delta-live/src/big.bin", "/t/delta-live/ref/");
                Assert.Equal(0, realRsync.ExitCode);

                long realLiteral = ParseStatBytes(realRsync.StdOut, "Literal data:");
                long realMatched = ParseStatBytes(realRsync.StdOut, "Matched data:");

                Assert.Equal(realMatched, second.MatchedBytes);
                Assert.Equal(realLiteral, second.TransferredBytes);
            }

            // ---- pull #3: no further changes -- fast path still intact after basis-signing -----
            await using var transport3 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
            PullSession.Result third = await PullSession.RunAsync(transport3, argv, dest, cts.Token);
            Assert.Equal(0, third.TransferredFiles);
            Assert.Equal(0, third.TransferredBytes);
            Assert.Equal(0, await transport3.WaitForExitAsync(cts.Token));
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

        async Task<string> HashInContainerAsync(string remotePath)
        {
            var result = await container.ExecAsync("sh", "-c", $"sha256sum {remotePath}");
            Assert.Equal(0, result.ExitCode);
            return result.StdOut[..64];
        }
    }

    private static string LocalSha256(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Parses a "&lt;label&gt; N,NNN bytes" line out of real rsync's <c>--stats</c> output.</summary>
    private static long ParseStatBytes(string stdOut, string label)
    {
        string line = stdOut.Split('\n').Single(l => l.StartsWith(label, StringComparison.Ordinal));
        string digits = line[label.Length..].Trim();
        digits = digits[..digits.IndexOf(' ')].Replace(",", "");
        return long.Parse(digits);
    }
}
