using RsyncWin.Engine;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Session;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Hermetic (not trait-gated): <c>--delete</c> on a pull is choreographically a plain pull —
/// verified byte-exact against <c>ssh31-pull-delete</c> (no wire byte, filter, or del-stats added) —
/// so deletion is purely a local <see cref="RsyncWin.Fs.LocalTreePruner"/> pass after the transfer.
/// </summary>
public class PullSessionDeleteTests
{
    private static readonly HandshakeOptions Xxh128 = new() { ChecksumOffer = "xxh128" };
    private static readonly DateTime KeepMtime = new(2021, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    private static byte[] Capture(string file) =>
        File.ReadAllBytes(Path.Combine(FindVectors(), "ssh31-pull-delete", file));

    private static string FindVectors()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "test-fixtures", "vectors");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("test-fixtures/vectors not found above the test binary");
    }

    [Fact]
    public async Task Delete_PrunesExtraneousEntries_ByteExact()
    {
        // Recreates the capture's dest tree (capture.sh's /t/c2dst): two kept entries whose
        // content+mtime already match the src (fast path, zero generator bytes for them) plus one
        // extraneous file and one extraneous dir with a file inside. The dest ROOT mtime is
        // deliberately left as "now" (never touched) so it differs from the src root's captured
        // 2021-06-15 mtime — that mismatch is exactly what produces the capture's one REPORT_TIME
        // echo on the root itself.
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-delete-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dest, "keepdir"));
            Directory.CreateDirectory(Path.Combine(dest, "extradir"));
            await File.WriteAllTextAsync(Path.Combine(dest, "keep1.txt"), "keep one\n");
            await File.WriteAllTextAsync(Path.Combine(dest, "keepdir", "keep2.txt"), "keep two\n");
            await File.WriteAllTextAsync(Path.Combine(dest, "extra.txt"), "extraneous\n");
            await File.WriteAllTextAsync(Path.Combine(dest, "extradir", "inside.txt"), "inside extra\n");

            File.SetLastWriteTimeUtc(Path.Combine(dest, "keep1.txt"), KeepMtime);
            File.SetLastWriteTimeUtc(Path.Combine(dest, "keepdir", "keep2.txt"), KeepMtime);
            Directory.SetLastWriteTimeUtc(Path.Combine(dest, "keepdir"), KeepMtime);

            await using var transport = new ScriptedTransport(Capture("s2c.bin"));
            PullSession.Result result = await PullSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/c2src/"] },
                dest, handshake: Xxh128, delete: true);

            Assert.False(result.PruneSkippedDueToIoError);

            Assert.False(File.Exists(Path.Combine(dest, "extra.txt")));
            Assert.False(Directory.Exists(Path.Combine(dest, "extradir")));
            Assert.True(File.Exists(Path.Combine(dest, "keep1.txt")));
            Assert.True(File.Exists(Path.Combine(dest, "keepdir", "keep2.txt")));
            Assert.Equal("keep one\n", await File.ReadAllTextAsync(Path.Combine(dest, "keep1.txt")));
            Assert.Equal("keep two\n", await File.ReadAllTextAsync(Path.Combine(dest, "keepdir", "keep2.txt")));

            // extra.txt (1 regular file) + extradir/inside.txt (1 regular file) + extradir itself (1 dir).
            Assert.Equal(2, result.Prune.DeletedRegularFiles);
            Assert.Equal(1, result.Prune.DeletedDirectories);
            Assert.Equal(0, result.Prune.DeletedSymlinks);

            // Frame boundaries carry no semantics: assert on the demuxed logical stream, prologue
            // stripped — --delete must add ZERO wire bytes (docs/wire-notes.md, ssh31-pull-delete).
            byte[] written = await transport.WrittenBytesAsync();
            int ourPrologue = 4 + 1 + "xxh128".Length;
            byte[] ours = Demux(written[ourPrologue..]);

            byte[] captured = Capture("c2s.bin");
            int capturedPrologue = 4 + 1 + "xxh128 xxh3 xxh64 md5 md4".Length;
            byte[] theirs = Demux(captured[capturedPrologue..]);

            Assert.Equal(theirs, ours);
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

    [Fact]
    public async Task Delete_SkipsPrune_WhenFlistHasIoError()
    {
        // rsync suppresses deletion on a flist io_error, to avoid deleting due to a partial listing.
        // No capture exercises a real io_error (forcing rsync to emit a partial-listing error
        // deterministically from a throwaway container isn't practical), so this is hand-built via
        // ScriptedSessionBuilder like PullSessionCollisionTests/PullSessionEarlyDoneTests: the flist
        // terminator is exactly xflags varint(0) + io_error varint — swapping the usual 0x00 for
        // 0x01 exercises the real FileListReader/PullSession suppression path directly, not a stand-in.
        var ndxCodec = new NdxCodec();
        byte[] ioErrorTerminator = [0x00, 0x01]; // xflags varint(0), io_error varint(1)
        byte[] serverBytes =
        [
            .. ScriptedSessionBuilder.HandshakePrologue,
            .. ScriptedSessionBuilder.Wrap(
                ScriptedSessionBuilder.BuildRegularFileEntry("f.txt"),
                ioErrorTerminator,
                ScriptedSessionBuilder.BuildEmptyFileReply(ndxCodec, 0),
                ScriptedSessionBuilder.NdxDone,   // phase 0 echo
                ScriptedSessionBuilder.NdxDone,   // phase 1 (redo) echo — nothing to redo
                ScriptedSessionBuilder.NdxDone,   // sender's final DONE
                ScriptedSessionBuilder.StatsBlock,
                ScriptedSessionBuilder.NdxDone    // goodbye DONE
            ),
        ];

        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-delete-ioerror-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dest);
            string extra = Path.Combine(dest, "extra.txt");
            await File.WriteAllTextAsync(extra, "must survive");

            await using var transport = new ScriptedTransport(serverBytes);
            PullSession.Result result = await PullSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/tree/"] },
                dest, handshake: new HandshakeOptions(), delete: true);

            Assert.Equal(1, result.IoErrorFlags);
            Assert.True(result.PruneSkippedDueToIoError);
            Assert.Empty(result.Prune.DeletedPaths);
            Assert.True(File.Exists(extra)); // the io_error suppressed the whole prune, untouched
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

    [Fact]
    public async Task Delete_SkipsPrune_WhenMidSessionIoErrorFrameArrives()
    {
        // Distinct from Delete_SkipsPrune_WhenFlistHasIoError above: the flist terminator's
        // io_error field stays 0 here, and suppression instead comes from a genuine mid-session
        // MSG_IO_ERROR mux frame (tag 22) arriving between the phase-0 echo and the redo/goodbye
        // burst — the other branch of PullSession's `ioErrorFlags |= ...` fold (PullSession.cs).
        var ndxCodec = new NdxCodec();
        byte[] serverBytes =
        [
            .. ScriptedSessionBuilder.HandshakePrologue,
            .. ScriptedSessionBuilder.Wrap(
                ScriptedSessionBuilder.BuildRegularFileEntry("f.txt"),
                ScriptedSessionBuilder.FlistTerminator,
                ScriptedSessionBuilder.BuildEmptyFileReply(ndxCodec, 0),
                ScriptedSessionBuilder.NdxDone),      // phase 0 echo
            .. ScriptedSessionBuilder.BuildIoErrorFrame(1), // mid-session io_error, its own mux frame
            .. ScriptedSessionBuilder.Wrap(
                ScriptedSessionBuilder.NdxDone,       // phase 1 (redo) echo — nothing to redo
                ScriptedSessionBuilder.NdxDone,       // sender's final DONE
                ScriptedSessionBuilder.StatsBlock,
                ScriptedSessionBuilder.NdxDone),      // goodbye DONE
        ];

        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-delete-midioerror-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dest);
            string extra = Path.Combine(dest, "extra.txt");
            await File.WriteAllTextAsync(extra, "must survive");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await using var transport = new ScriptedTransport(serverBytes);
            PullSession.Result result = await PullSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/tree/"] },
                dest, cts.Token, handshake: new HandshakeOptions(), delete: true);

            Assert.True(result.PruneSkippedDueToIoError);
            Assert.NotEqual(0, result.IoErrorFlags);
            Assert.Empty(result.Prune.DeletedPaths);
            Assert.True(File.Exists(extra)); // the mid-session io_error suppressed the whole prune, untouched
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

    private static byte[] Demux(byte[] framed)
    {
        var logical = new MemoryStream();
        for (int offset = 0; offset + MuxHeader.Size <= framed.Length;)
        {
            MuxHeader header = MuxHeader.Read(framed.AsSpan(offset));
            offset += MuxHeader.Size;
            if (header.Tag == MessageTag.Data)
                logical.Write(framed, offset, header.PayloadLength);
            offset += header.PayloadLength;
        }
        return logical.ToArray();
    }
}
