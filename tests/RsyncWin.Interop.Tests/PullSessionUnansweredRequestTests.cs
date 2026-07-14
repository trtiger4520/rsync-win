using RsyncWin.Engine;
using RsyncWin.Protocol.Session;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Hermetic: a file the generator requests but the sender never answers — no reply, no
/// <c>MSG_NO_SEND</c> — and no early/premature DONE either (both phases end with a clean DONE that
/// arrives only after the writer has finished). Before the fix this silently vanished as success;
/// it must instead flow through the existing redo/failed machinery like a checksum mismatch does.
/// </summary>
public class PullSessionUnansweredRequestTests
{
    [Fact]
    public async Task NeverAnsweredRequest_EndsUpInFailedFiles_AfterOneRedo()
    {
        // Three regular files: "a" and "c" get answered normally; "b" never gets a reply in
        // either phase, despite two clean DONEs (phase 0's and phase 1's) actually arriving after
        // the writer completes each phase.
        var ndxCodec = new NdxCodec();
        byte[] serverBytes =
        [
            .. ScriptedSessionBuilder.HandshakePrologue,
            .. ScriptedSessionBuilder.Wrap(
                ScriptedSessionBuilder.BuildRegularFileEntry("a"),
                ScriptedSessionBuilder.BuildRegularFileEntry("b"),
                ScriptedSessionBuilder.BuildRegularFileEntry("c"),
                ScriptedSessionBuilder.FlistTerminator,
                ScriptedSessionBuilder.BuildEmptyFileReply(ndxCodec, 0), // "a" — answered
                ScriptedSessionBuilder.BuildEmptyFileReply(ndxCodec, 2), // "c" — answered; "b" (1) never is
                ScriptedSessionBuilder.NdxDone, // phase 0 echo — clean, writer already done
                ScriptedSessionBuilder.NdxDone, // phase 1 (redo of "b") echo — clean, "b" still unanswered
                ScriptedSessionBuilder.NdxDone, // sender's final DONE
                ScriptedSessionBuilder.StatsBlock,
                ScriptedSessionBuilder.NdxDone // goodbye DONE
            ),
        ];

        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-unanswered-{Guid.NewGuid():N}");
        try
        {
            await using var transport = new ScriptedTransport(serverBytes);
            PullSession.Result result = await PullSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/tree/"] },
                dest, handshake: new HandshakeOptions());

            Assert.Equal(2, result.TransferredFiles);
            Assert.Contains("b", result.RedoneFiles);   // one retry after phase 0, like a checksum mismatch
            Assert.Contains("b", result.FailedFiles);    // still unanswered after the redo phase — a real failure
            Assert.True(File.Exists(Path.Combine(dest, "a")));
            Assert.True(File.Exists(Path.Combine(dest, "c")));
            Assert.False(File.Exists(Path.Combine(dest, "b"))); // never received
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
