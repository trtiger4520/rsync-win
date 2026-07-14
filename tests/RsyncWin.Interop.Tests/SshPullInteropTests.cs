using System.Security.Cryptography;
using RsyncWin.Engine;
using RsyncWin.Protocol.Session;
using RsyncWin.Transport;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// THE interop milestone, live: pull a whole tree from a real rsync over ssh.exe onto the Windows
/// filesystem, byte-identical (SHA-256 per file against hashes computed inside the container),
/// with the remote side exiting 0.
/// </summary>
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
}
