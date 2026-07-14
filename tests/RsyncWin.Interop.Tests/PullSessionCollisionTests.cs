using RsyncWin.Engine;
using RsyncWin.Protocol.Session;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Hermetic: <see cref="WindowsPathMapper"/> is many-to-one — "a:b" and "a_b" both map to "a_b".
/// Flist order is authoritative (CLAUDE.md), so the first entry must win and the later, colliding
/// one must be excluded from the transfer entirely rather than silently clobbering the winner.
/// </summary>
[Trait("Category", "WindowsFs")]
public class PullSessionCollisionTests
{
    [Fact]
    public async Task CollidingName_IsSkipped_WinnerIsTransferred()
    {
        // FileNameComparer sorts by raw byte order: ':' (0x3a) < '_' (0x5f), so "a:b" (ndx 0, the
        // winner — its mapped path "a_b" is claimed first) sorts before "a_b" itself (ndx 1, the
        // loser: its mapped path is identical, unmapped, and collides with the winner's).
        var ndxCodec = new NdxCodec();
        byte[] serverBytes =
        [
            .. ScriptedSessionBuilder.HandshakePrologue,
            .. ScriptedSessionBuilder.Wrap(
                ScriptedSessionBuilder.BuildRegularFileEntry("a:b"),
                ScriptedSessionBuilder.BuildRegularFileEntry("a_b"),
                ScriptedSessionBuilder.FlistTerminator,
                ScriptedSessionBuilder.BuildEmptyFileReply(ndxCodec, 0), // only the winner is requested
                ScriptedSessionBuilder.NdxDone,   // phase 0 echo
                ScriptedSessionBuilder.NdxDone,   // phase 1 (redo) echo — nothing to redo
                ScriptedSessionBuilder.NdxDone,   // sender's final DONE
                ScriptedSessionBuilder.StatsBlock,
                ScriptedSessionBuilder.NdxDone   // goodbye DONE
            ),
        ];

        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-collision-{Guid.NewGuid():N}");
        try
        {
            await using var transport = new ScriptedTransport(serverBytes);
            PullSession.Result result = await PullSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/tree/"] },
                dest, handshake: new HandshakeOptions());

            Assert.Equal(1, result.TransferredFiles);
            Assert.Empty(result.RedoneFiles);
            Assert.Empty(result.FailedFiles);
            Assert.True(File.Exists(Path.Combine(dest, "a_b")));

            (string Name, string Reason) skip = Assert.Single(result.SkippedNonRegular);
            Assert.Equal("a_b", skip.Name);
            Assert.Contains("collision", skip.Reason);
            Assert.Contains("a:b", skip.Reason);
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
