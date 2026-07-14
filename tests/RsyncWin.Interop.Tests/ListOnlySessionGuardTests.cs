using System.IO.Pipelines;
using RsyncWin.Engine;
using RsyncWin.Protocol.Session;
using RsyncWin.Transport;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Hermetic (not trait-gated): drives <see cref="ListOnlySession"/> against scripted server bytes.
/// </summary>
public class ListOnlySessionGuardTests
{
    private sealed class ScriptedTransport(byte[] serverBytes) : IRsyncTransport
    {
        private readonly Pipe _discard = new(new PipeOptions(pauseWriterThreshold: 0));

        public PipeReader Input { get; } = PipeReader.Create(new MemoryStream(serverBytes));
        public PipeWriter Output => _discard.Writer;
        public PipeReader StandardError { get; } = PipeReader.Create(new MemoryStream([]));
        public ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Proto31Peer_WithoutVarintFlistFlags_IsRejectedLoudly()
    {
        // The rsync 3.1.0–3.2.3 shape: protocol 31, but compat_flags without bit 7 (here: only
        // the seed-fix bit). Byte-mode file lists would desync or mutually hang — the session
        // must refuse before touching the flist.
        byte[] server = [0x20, 0, 0, 0, 0x20, 0xAA, 0xBB, 0xCC, 0xDD];
        await using var transport = new ScriptedTransport(server);
        var argv = new ServerArgvBuilder { Sender = true, Recurse = true, ListOnly = true, Paths = ["/x/"] };

        var exception = await Assert.ThrowsAsync<ProtocolException>(
            () => ListOnlySession.RunAsync(transport, argv));
        Assert.Equal(RsyncExitCode.ProtocolIncompatibility, exception.ExitCode);
    }
}
