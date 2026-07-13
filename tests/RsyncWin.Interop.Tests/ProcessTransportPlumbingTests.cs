using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using RsyncWin.Transport;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Process-plumbing checks for <see cref="OpenSshProcessTransport"/> using in-box Windows tools —
/// deliberately NOT trait-gated: no Docker, no rsync, runs in the fast tier. True binary fidelity
/// of the stream path is pinned by the live handshake interop tests (4-byte LE ints negotiated
/// with a real rsync); these tests pin the pumping, shutdown, and error-reporting mechanics.
/// </summary>
public class ProcessTransportPlumbingTests
{
    private static string CmdExe => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public void DefaultSshExePath_UsesThePlatformConvention()
    {
        string expected = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe")
            : "ssh";

        Assert.Equal(expected, OpenSshProcessTransport.DefaultSshExePath);
    }

    private static async Task<byte[]> DrainAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        var all = new MemoryStream();
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            foreach (ReadOnlyMemory<byte> segment in result.Buffer)
                all.Write(segment.Span);
            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
                return all.ToArray();
        }
    }

    [Fact]
    public async Task Stdout_FlowsThroughInput_AndExitCodeSurvives()
    {
        await using var transport = OpenSshProcessTransport.Start(CmdExe, ["/c", "echo hi"]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        byte[] output = await DrainAsync(transport.Input, cts.Token);
        Assert.Equal("hi\r\n", Encoding.ASCII.GetString(output));
        Assert.Equal(0, await transport.WaitForExitAsync(cts.Token));
    }

    [Fact]
    public async Task Stdin_ReachesTheProcess_AndEofPropagatesOnComplete()
    {
        // findstr with an always-matching pattern echoes stdin lines back — enough to prove the
        // write path reaches the child and EOF propagates when Output completes. Run it directly:
        // through cmd.exe the ^ would be eaten as cmd's escape character.
        await using var transport = OpenSshProcessTransport.Start(
            Path.Combine(Environment.SystemDirectory, "findstr.exe"), ["^"]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        byte[] payload = Encoding.ASCII.GetBytes("alpha\r\nbravo\r\n");
        await transport.Output.WriteAsync(payload, cts.Token);
        await transport.Output.CompleteAsync();

        Assert.Equal(payload, await DrainAsync(transport.Input, cts.Token));
        Assert.Equal(0, await transport.WaitForExitAsync(cts.Token));
    }

    [Fact]
    public async Task LargeStderrBurst_NeverDeadlocks_EvenWithNoStderrReader()
    {
        // 1 MiB of stderr — far past the OS pipe buffer. If the dedicated drain loop were missing
        // or blocked, the child would stall writing stderr and never exit; this is THE deadlock
        // the three-pump design exists to prevent, so pin it.
        string burstFile = Path.Combine(Path.GetTempPath(), $"rsyncwin-burst-{Guid.NewGuid():N}.txt");
        byte[] line = Encoding.ASCII.GetBytes(new string('x', 126) + "\r\n");
        await using (var file = File.Create(burstFile))
        {
            for (int i = 0; i < 8192; i++)
                await file.WriteAsync(line);
        }

        try
        {
            await using var transport = OpenSshProcessTransport.Start(
                CmdExe, ["/c", $"type {burstFile} 1>&2"]);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            Assert.Empty(await DrainAsync(transport.Input, cts.Token));
            Assert.Equal(0, await transport.WaitForExitAsync(cts.Token));
            Assert.Equal(8192 * 128, (await transport.ReadAllStandardErrorAsync(cts.Token)).Length);
        }
        finally
        {
            File.Delete(burstFile);
        }
    }

    [Fact]
    public async Task StderrIsReadable_WhileTheProcessIsStillAlive()
    {
        // The exit-5 reporting path: a handshake fails while ssh is still running, and the caller
        // wants its stderr NOW. The compound child writes its diagnostics then blocks on stdin.
        await using var transport = OpenSshProcessTransport.Start(
            CmdExe, ["/c", "echo oops 1>&2 & findstr x"]);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        string text = "";
        for (int attempt = 0; attempt < 50 && text.Length == 0; attempt++)
        {
            text = await transport.ReadAllStandardErrorAsync(cts.Token);
            if (text.Length == 0)
                await Task.Delay(100, cts.Token);
        }
        Assert.Contains("oops", text);
        // Disposal now has to shut down a live, stdin-blocked child — bounded, no hang.
    }

    [Fact]
    public async Task DisposeAsync_IsIdempotent()
    {
        var transport = OpenSshProcessTransport.Start(CmdExe, ["/c", "echo hi"]);

        await transport.DisposeAsync();
        await transport.DisposeAsync(); // second call must be a no-op, not a crash
    }
}
