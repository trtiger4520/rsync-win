using System.Security.Cryptography;
using RsyncWin.Engine;
using RsyncWin.Protocol.Session;
using RsyncWin.Transport;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Live daemon (<c>rsync://</c>) pull interop against a real rsyncd in Docker (<see cref="RsyncdContainer"/>):
/// anonymous and authenticated module pulls, the mtime+size fast path, and the two textual-preamble
/// failure paths (@ERROR for an unknown module, auth failure) surfaced as <see cref="ProtocolException"/>
/// with exit-5 semantics (docs/daemon-spec.md §1/§2). Mirrors <see cref="SshPullInteropTests"/>'s
/// technique (whole-tree SHA-256 against the container, computed via <c>ExecAsync</c>) with the ssh
/// transport swapped for <see cref="DaemonTcpTransport"/> plus the textual <see cref="DaemonPreamble"/>.
/// </summary>
[Trait("Category", "Interop")]
public sealed class DaemonPullInteropTests(RsyncdContainer container) : IClassFixture<RsyncdContainer>
{
    private static readonly HandshakeOptions Xxh128 = new() { ChecksumOffer = "xxh128" };

    private async Task<DaemonTcpTransport> ConnectAsync(CancellationToken cancellationToken) =>
        await DaemonTcpTransport.ConnectAsync(container.Host, container.Port, cancellationToken);

    /// <summary>Runs the textual preamble for <paramref name="module"/> and returns a
    /// <see cref="HandshakeOptions"/> pre-negotiated for the role session that follows, exactly the
    /// sequence <see cref="DaemonSessionReplayTests"/> pins byte-for-byte against captures.</summary>
    private static async Task<HandshakeOptions> RunPreambleAsync(
        DaemonTcpTransport transport, string module, ServerArgvBuilder serverArgs,
        string? user, string? password, CancellationToken cancellationToken)
    {
        DaemonPreambleResult preamble = await DaemonPreamble.RunAsync(
            transport, module, serverArgs, user: user, password: password,
            maxProtocol: 31, cancellationToken: cancellationToken);
        Assert.Equal(31, preamble.Protocol);
        return Xxh128 with { PreNegotiatedProtocolVersion = preamble.Protocol };
    }

    private async Task<string[]> HashEveryFileInContainerAsync(string remoteDir)
    {
        var remote = await container.ExecAsync(
            "sh", "-c", $"cd {remoteDir} && find . -type f -print0 | sort -z | xargs -0 -r sha256sum");
        Assert.Equal(0, remote.ExitCode);
        return remote.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static async Task AssertTreeMatchesAsync(string dest, string[] sha256Lines)
    {
        Assert.Equal(7, sha256Lines.Length); // 7 regular files under /t/tree, see RsyncdContainer
        foreach (string line in sha256Lines)
        {
            string expected = line[..64];
            string relative = line[64..].Trim().TrimStart('.', '/');
            string local = Path.Combine([dest, .. relative.Split('/')]);
            byte[] content = await File.ReadAllBytesAsync(local);
            Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(content)));
        }
    }

    [Fact]
    [Trait("Profile", "Smoke")]
    public async Task Pull_AnonymousTree_ByteIdentical_ThenRerunTransfersNothing()
    {
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-daemon-pull-{Guid.NewGuid():N}");
        try
        {
            var serverArgs = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["tree/"] };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            await using (var transport1 = await ConnectAsync(cts.Token))
            {
                HandshakeOptions handshake = await RunPreambleAsync(
                    transport1, "tree", serverArgs, user: null, password: null, cts.Token);
                PullSession.Result first = await PullSession.RunAsync(
                    transport1, serverArgs, dest, cts.Token, handshake: handshake);

                Assert.Equal(7, first.TransferredFiles);
                Assert.Empty(first.FailedFiles);
                Assert.Empty(first.RedoneFiles);
            }

            string[] sha256Lines = await HashEveryFileInContainerAsync("/t/tree");
            await AssertTreeMatchesAsync(dest, sha256Lines);

            // Re-pull into the SAME destination -- mtime+size fast path -- must transfer nothing.
            await using var transport2 = await ConnectAsync(cts.Token);
            HandshakeOptions handshake2 = await RunPreambleAsync(
                transport2, "tree", serverArgs, user: null, password: null, cts.Token);
            PullSession.Result second = await PullSession.RunAsync(
                transport2, serverArgs, dest, cts.Token, handshake: handshake2);

            Assert.Equal(0, second.TransferredFiles);
            Assert.Equal(0, second.TransferredBytes);
            Assert.Empty(second.FailedFiles);
        }
        finally
        {
            try { Directory.Delete(dest, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    [Trait("Profile", "Smoke")]
    public async Task Pull_AuthenticatedSecretModule_ByteIdentical()
    {
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-daemon-pull-auth-{Guid.NewGuid():N}");
        try
        {
            var serverArgs = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["secret/"] };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            await using var transport = await ConnectAsync(cts.Token);
            HandshakeOptions handshake = await RunPreambleAsync(
                transport, "secret", serverArgs, RsyncdContainer.AuthUser, RsyncdContainer.AuthPassword, cts.Token);
            PullSession.Result result = await PullSession.RunAsync(
                transport, serverArgs, dest, cts.Token, handshake: handshake);

            Assert.Equal(7, result.TransferredFiles);
            Assert.Empty(result.FailedFiles);

            // [secret] is served from the same /t/tree as [tree] (RsyncdContainer), same ground truth.
            string[] sha256Lines = await HashEveryFileInContainerAsync("/t/tree");
            await AssertTreeMatchesAsync(dest, sha256Lines);
        }
        finally
        {
            try { Directory.Delete(dest, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public async Task Pull_WrongPassword_FailsWithAuthErrorAndExitFiveSemantics()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverArgs = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["secret/"] };
        await using var transport = await ConnectAsync(cts.Token);

        ProtocolException ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            DaemonPreamble.RunAsync(
                transport, "secret", serverArgs,
                user: RsyncdContainer.AuthUser, password: "definitely-wrong-password",
                maxProtocol: 31, cancellationToken: cts.Token));

        Assert.Equal(RsyncExitCode.StartClientServerError, ex.ExitCode); // exit 5
        Assert.Contains("auth failed", ex.Message);
    }

    [Fact]
    public async Task Pull_UnknownModule_FailsWithUnknownModuleErrorAndExitFiveSemantics()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverArgs = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["nonexistent/"] };
        await using var transport = await ConnectAsync(cts.Token);

        ProtocolException ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            DaemonPreamble.RunAsync(
                transport, "nonexistent", serverArgs, maxProtocol: 31, cancellationToken: cts.Token));

        Assert.Equal(RsyncExitCode.StartClientServerError, ex.ExitCode); // exit 5
        Assert.Contains("Unknown module", ex.Message);
    }

    [Fact]
    [Trait("Profile", "Smoke")]
    public async Task ListModules_ReturnsTreeAndPushAndSecret()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var transport = await ConnectAsync(cts.Token);

        DaemonModuleListResult result = await DaemonPreamble.ListModulesAsync(
            transport, maxProtocol: 31, cancellationToken: cts.Token);

        Assert.Contains(result.ModuleLines, line => line.StartsWith("tree", StringComparison.Ordinal));
        Assert.Contains(result.ModuleLines, line => line.StartsWith("push", StringComparison.Ordinal));
        Assert.Contains(result.ModuleLines, line => line.StartsWith("secret", StringComparison.Ordinal));
    }
}
