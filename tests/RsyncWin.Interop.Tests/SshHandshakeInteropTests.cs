using RsyncWin.Protocol;
using RsyncWin.Protocol.Checksums;
using RsyncWin.Protocol.Session;
using RsyncWin.Transport;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// The P2 gate, live: spawn the in-box <c>ssh.exe</c> against a real rsync 3.4.3, run the
/// handshake, and confirm the negotiated facts. Every test is bounded — phase-boundary bugs
/// manifest as hangs, not failures.
/// </summary>
[Trait("Category", "Interop")]
public sealed class SshHandshakeInteropTests(SshRsyncContainer container) : IClassFixture<SshRsyncContainer>
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Protocol31_HandshakeAgainstRealRsync()
    {
        var argv = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/tree/"] };
        await using var transport = OpenSshProcessTransport.Start(
            OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
        using var cts = new CancellationTokenSource(HandshakeTimeout);

        SessionContext context;
        try
        {
            context = await HandshakeRunner.RunClientAsync(
                transport.Input, transport.Output, new HandshakeOptions(), cts.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException(
                $"handshake failed: {exception.Message}; ssh stderr: {await SafeStdErrAsync(transport)}");
        }

        Assert.Equal(31, context.Protocol); // min(our 31, rsync 3.4.3's 32)
        // Don't pin the full compat value live: bits 1–2 reflect the SERVER build's symlink-time /
        // iconv support, not our request. Assert the bits our session logic depends on.
        Assert.True(context.ChecksumSeedFix);
        Assert.True(context.VarintFlistFlags);
        Assert.True(context.SafeFileList);
        Assert.Equal(ChecksumAlgorithm.Md5, context.TransferChecksum);
        Assert.True(context.MultiplexedOutput);

        await AssertSshItselfSucceededAsync(transport);
    }

    [Fact]
    public async Task Protocol29_FallbackHandshake()
    {
        var argv = new ServerArgvBuilder { Sender = true, Recurse = true, Protocol = 29, Paths = ["/t/tree/"] };
        await using var transport = OpenSshProcessTransport.Start(
            OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
        using var cts = new CancellationTokenSource(HandshakeTimeout);

        var context = await HandshakeRunner.RunClientAsync(
            transport.Input, transport.Output, new HandshakeOptions { AdvertisedProtocol = 29 }, cts.Token);

        Assert.Equal(29, context.Protocol);
        Assert.Equal(0, context.CompatFlags);
        Assert.Equal(ChecksumAlgorithm.Md4, context.TransferChecksum);
        Assert.False(context.MultiplexedOutput);

        await AssertSshItselfSucceededAsync(transport);
    }

    [Fact]
    public async Task RejectedAuth_SurfacesAsSsh255_NotAProtocolFailure()
    {
        // A key the container has never seen: ssh fails before rsync starts. The handshake sees a
        // dead stream; the transport's exit code (255) plus stderr carry the real diagnosis —
        // exactly the split the CLI later maps to exit 5.
        string strangerKey = Path.Combine(Path.GetTempPath(), $"rsyncwin-stranger-{Guid.NewGuid():N}");
        using (var keygen = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe"))
        {
            ArgumentList = { "-q", "-t", "ed25519", "-N", "", "-f", strangerKey },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!)
        {
            using var keygenCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await keygen.WaitForExitAsync(keygenCts.Token);
            Assert.Equal(0, keygen.ExitCode);
        }

        try
        {
            var argv = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/tree/"] };
            await using var transport = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath,
                container.SshArgsWithKey(strangerKey, argv.Build()));
            using var cts = new CancellationTokenSource(HandshakeTimeout);

            await Assert.ThrowsAsync<InvalidDataException>(async () => await HandshakeRunner.RunClientAsync(
                transport.Input, transport.Output, new HandshakeOptions(), cts.Token));

            int exitCode = await transport.WaitForExitAsync(cts.Token);
            Assert.Equal(255, exitCode);
        }
        finally
        {
            File.Delete(strangerKey);
            File.Delete(strangerKey + ".pub");
        }
    }

    private static async Task AssertSshItselfSucceededAsync(OpenSshProcessTransport transport)
    {
        // We abandon the session right after the handshake, so the remote rsync exits with a
        // protocol error — that is expected. What must NOT happen is ssh's own failure code.
        await transport.Output.CompleteAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int exitCode = await transport.WaitForExitAsync(cts.Token);
        Assert.True(exitCode != 255, $"ssh itself failed: {await SafeStdErrAsync(transport)}");
    }

    private static async Task<string> SafeStdErrAsync(OpenSshProcessTransport transport)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            return await transport.ReadAllStandardErrorAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return "(stderr unavailable)";
        }
    }
}
