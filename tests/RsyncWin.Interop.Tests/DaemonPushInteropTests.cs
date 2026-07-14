using System.Security.Cryptography;
using RsyncWin.Engine;
using RsyncWin.Fs;
using RsyncWin.Protocol.Session;
using RsyncWin.Transport;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Live daemon (<c>rsync://</c>) push interop against a real rsyncd in Docker (<see cref="RsyncdContainer"/>):
/// a whole tree pushed into the writable [push] module, byte-identical on the server; the mtime+size
/// fast path on a same-tree re-push; and a push to the read-only [tree] module surfacing the real
/// daemon's in-mux MSG_ERROR/MSG_ERROR_EXIT error (docs/daemon-spec.md §5) without hanging. Mirrors
/// <see cref="SshPushInteropTests"/>'s technique and source-tree shape, with the ssh transport swapped
/// for <see cref="DaemonTcpTransport"/> plus the textual <see cref="DaemonPreamble"/>. [push] is
/// anonymous, so no auth push case is needed here.
/// </summary>
[Trait("Category", "Interop")]
public sealed class DaemonPushInteropTests(RsyncdContainer container) : IClassFixture<RsyncdContainer>
{
    private static readonly HandshakeOptions Xxh128 = new() { ChecksumOffer = "xxh128" };

    private async Task<DaemonTcpTransport> ConnectAsync(CancellationToken cancellationToken) =>
        await DaemonTcpTransport.ConnectAsync(container.Host, container.Port, cancellationToken);

    private static async Task<HandshakeOptions> RunPreambleAsync(
        DaemonTcpTransport transport, string module, ServerArgvBuilder serverArgs, CancellationToken cancellationToken)
    {
        DaemonPreambleResult preamble = await DaemonPreamble.RunAsync(
            transport, module, serverArgs, maxProtocol: 31, cancellationToken: cancellationToken);
        Assert.Equal(31, preamble.Protocol);
        return Xxh128 with { PreNegotiatedProtocolVersion = preamble.Protocol };
    }

    /// <summary>Deterministic pseudo-random content, no fixture shipped -- same idiom as
    /// <see cref="SshPushInteropTests"/>'s own helper.</summary>
    private static byte[] DeterministicBytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Builds the local source tree exactly like <see cref="SshPushInteropTests.CreateLocalTreeAsync"/>:
    /// nested dirs, an empty file, 64 KiB + 300 KiB deterministic binaries, a unicode name, and a name
    /// with a space. Mtimes are left as whatever the real write produced.</summary>
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

    private async Task<string> HashInContainerAsync(string remotePath)
    {
        var result = await container.ExecAsync("sh", "-c", $"sha256sum {remotePath}");
        Assert.Equal(0, result.ExitCode);
        return result.StdOut[..64];
    }

    [Fact]
    public async Task Push_Tree_ByteIdenticalOnServer()
    {
        string localRoot = Path.Combine(Path.GetTempPath(), $"rsyncwin-daemon-push-tree-{Guid.NewGuid():N}");
        try
        {
            await container.ResetPushModuleAsync(); // clean slate regardless of other tests' order
            await CreateLocalTreeAsync(localRoot, CancellationToken.None);
            List<EnumeratedEntry> entries = [.. FileEnumerator.Enumerate(localRoot)];

            var serverArgs = new ServerArgvBuilder { Sender = false, Recurse = true, Paths = ["push/"] };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            await using var transport = await ConnectAsync(cts.Token);
            HandshakeOptions handshake = await RunPreambleAsync(transport, "push", serverArgs, cts.Token);
            PushSession.Result result = await PushSession.RunAsync(
                transport, serverArgs, entries, cts.Token, handshake: handshake);

            Assert.Equal(LocalTreeFileCount, result.FilesSent);
            Assert.Empty(result.FailedFiles);

            var remote = await container.ExecAsync(
                "sh", "-c", "cd /t/dpush && find . -type f -print0 | sort -z | xargs -0 -r sha256sum");
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
            await container.ResetPushModuleAsync();
            try { Directory.Delete(localRoot, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    /// <summary>P5-mirrored mtime+size fast path, daemon push direction, live: a second push of the
    /// SAME local tree (real, unrounded NTFS mtimes) must request nothing -- zero files sent.</summary>
    [Fact]
    public async Task Push_SecondRun_SameTree_TransfersNothing()
    {
        string localRoot = Path.Combine(Path.GetTempPath(), $"rsyncwin-daemon-push-rerun-{Guid.NewGuid():N}");
        try
        {
            await container.ResetPushModuleAsync();
            await CreateLocalTreeAsync(localRoot, CancellationToken.None);
            List<EnumeratedEntry> entries = [.. FileEnumerator.Enumerate(localRoot)];

            var serverArgs = new ServerArgvBuilder { Sender = false, Recurse = true, Paths = ["push/"] };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            await using (var transport1 = await ConnectAsync(cts.Token))
            {
                HandshakeOptions handshake1 = await RunPreambleAsync(transport1, "push", serverArgs, cts.Token);
                PushSession.Result first = await PushSession.RunAsync(
                    transport1, serverArgs, entries, cts.Token, handshake: handshake1);
                Assert.Equal(LocalTreeFileCount, first.FilesSent);
                Assert.Empty(first.FailedFiles);
            }

            // Fresh connection + fresh session for the re-run, same entries (same real mtimes captured
            // once) since nothing changed locally.
            await using var transport2 = await ConnectAsync(cts.Token);
            HandshakeOptions handshake2 = await RunPreambleAsync(transport2, "push", serverArgs, cts.Token);
            PushSession.Result second = await PushSession.RunAsync(
                transport2, serverArgs, entries, cts.Token, handshake: handshake2);

            Assert.Equal(0, second.FilesSent);
            Assert.Equal(0, second.LiteralBytes);
            Assert.Empty(second.FailedFiles);
        }
        finally
        {
            await container.ResetPushModuleAsync();
            try { Directory.Delete(localRoot, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    /// <summary>
    /// docs/daemon-spec.md §5, live: pushing to the read-only [tree] module passes the textual
    /// preamble (OK, compat, seed) then the real daemon errors in-mux -- MSG_ERROR text frames
    /// followed by MSG_ERROR_EXIT carrying exit code 1. PushSession has no code that echoes a len-0
    /// MSG_ERROR_EXIT frame back (see docs/daemon-spec.md §5's note on the client's echo); this test
    /// is the first live check of whether that gap makes a REAL daemon hang waiting for the echo
    /// instead of just closing. Bounded by the repo-standard hang-detection timeout so a hang fails
    /// the test loudly instead of blocking the suite.
    /// </summary>
    [Fact]
    public async Task Push_ToReadOnlyModule_SurfacesServerErrorTextAndExitCodeOne_NoHang()
    {
        string localRoot = Path.Combine(Path.GetTempPath(), $"rsyncwin-daemon-push-readonly-{Guid.NewGuid():N}");
        try
        {
            await CreateLocalTreeAsync(localRoot, CancellationToken.None);
            List<EnumeratedEntry> entries = [.. FileEnumerator.Enumerate(localRoot)];

            var serverArgs = new ServerArgvBuilder { Sender = false, Recurse = true, Paths = ["tree/"] };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            await using var transport = await ConnectAsync(cts.Token);
            HandshakeOptions handshake = await RunPreambleAsync(transport, "tree", serverArgs, cts.Token);

            ProtocolException ex = await Assert.ThrowsAsync<ProtocolException>(() =>
                PushSession.RunAsync(transport, serverArgs, entries, cts.Token, handshake: handshake));

            Assert.Equal(RsyncExitCode.SyntaxError, ex.ExitCode); // carried exit code 1
            Assert.Contains("module is read only", ex.Message);
        }
        finally
        {
            try { Directory.Delete(localRoot, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }
}
