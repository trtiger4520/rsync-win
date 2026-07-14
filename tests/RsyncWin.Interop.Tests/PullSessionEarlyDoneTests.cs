using RsyncWin.Engine;
using RsyncWin.Protocol.Session;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Hermetic: if the sender sends <c>NDX_DONE</c> before the request writer has finished writing
/// every request for the phase, the phase's reply reader returns normally while the writer is
/// still parked on the bounded request channel — a protocol violation that must surface as
/// <see cref="InvalidDataException"/>, not hang forever waiting for a writer that will never
/// unblock on its own.
/// </summary>
public class PullSessionEarlyDoneTests
{
    [Fact]
    public async Task PrematureDone_ThrowsInsteadOfHanging()
    {
        // More entries than the request channel's bounded capacity (64, PullSession's
        // RequestChannelCapacity): the writer fills the channel and blocks on the 65th write,
        // genuinely parked — exactly the scenario a premature DONE must not leave hanging.
        const int fileCount = 70;
        var flist = new List<byte>();
        for (int i = 0; i < fileCount; i++)
            flist.AddRange(ScriptedSessionBuilder.BuildRegularFileEntry($"f{i:D3}"));
        flist.AddRange(ScriptedSessionBuilder.FlistTerminator);

        byte[] serverBytes =
        [
            .. ScriptedSessionBuilder.HandshakePrologue,
            .. ScriptedSessionBuilder.Wrap(
                flist,
                ScriptedSessionBuilder.NdxDone), // DONE arrives before any of the 70 requests are answered
        ];

        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-earlydone-{Guid.NewGuid():N}");
        try
        {
            await using var transport = new ScriptedTransport(serverBytes);
            Task<PullSession.Result> runTask = PullSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/tree/"] },
                dest, handshake: new HandshakeOptions());

            Task finished = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.Same(runTask, finished); // must not be the timeout that won the race — no hang

            var exception = await Assert.ThrowsAsync<InvalidDataException>(() => runTask);
            Assert.Contains("DONE", exception.Message);
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
