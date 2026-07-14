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
// P5 CLI end-to-end test skipped: the CLI's -e/--rsh takes a bare executable path with no way to
// express the container's ephemeral port/key/known-hosts options, and growing the flag surface just
// for a test is not warranted. Revisit when the CLI grows ssh-option passthrough (P9 flag surface).
[Trait("Category", "Interop")]
public sealed class SshPullInteropTests(SshRsyncContainer container) : IClassFixture<SshRsyncContainer>
{
    [Fact]
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
}
