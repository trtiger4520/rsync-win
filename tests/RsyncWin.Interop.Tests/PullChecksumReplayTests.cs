using System.Security.Cryptography;
using RsyncWin.Engine;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Session;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Hermetic (not trait-gated): replays the captured <c>ssh31-pull-checksum</c> (<c>-rtc</c>, a
/// genuine delta-mode <c>--checksum</c> pull) through <see cref="PullSession"/>. The destination is
/// reconstructed to exactly the capture's pre-pull tree — including <c>dst-b_fast.bin</c>, the exact
/// on-disk basis the real client signed — so this exercises the attribute-only <c>0x0008</c> wire
/// path end-to-end: a file whose content already matches but whose mtime is stale gets no sum head,
/// no transfer, and is never posted to the reply channel, yet the server still echoes it back.
/// </summary>
public class PullChecksumReplayTests
{
    private static readonly HandshakeOptions Xxh128 = new() { ChecksumOffer = "xxh128" };

    private static byte[] Capture(string vector, string file) =>
        File.ReadAllBytes(Path.Combine(FindVectors(), vector, file));

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
    public async Task ChecksumPullReplay_TransfersStaleFilesAndFixesAttributeOnlyMatch()
    {
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-checksum-{Guid.NewGuid():N}");
        try
        {
            byte[] staleBFast = Capture("ssh31-pull-checksum", "dst-b_fast.bin");
            await CreateCapturedDestinationTreeAsync(dest, staleBFast);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var transport = new ScriptedTransport(Capture("ssh31-pull-checksum", "s2c.bin"));
            PullSession.Result result = await PullSession.RunAsync(
                transport,
                new ServerArgvBuilder { Sender = true, Recurse = true, Checksum = true, Paths = ["/t/c1src/"] },
                dest, cts.Token, handshake: Xxh128);

            Assert.Equal(2, result.TransferredFiles); // b_fast.bin + c_new.txt only — a_match is attribute-only
            Assert.Empty(result.RedoneFiles);
            Assert.Empty(result.FailedFiles);

            // a_match.txt: the 0x0008 attribute-only path — content untouched, stale mtime corrected.
            string aMatch = Path.Combine(dest, "a_match.txt");
            Assert.Equal("hello rsync\n", await File.ReadAllTextAsync(aMatch));
            Assert.Equal(
                new DateTime(2021, 6, 15, 12, 0, 0, DateTimeKind.Utc),
                File.GetLastWriteTimeUtc(aMatch));

            // c_new.txt: absent before the pull, materialized by it.
            Assert.True(File.Exists(Path.Combine(dest, "c_new.txt")));

            // b_fast.bin: the delta transfer rewrote it — no longer equal to the stale pre-pull
            // basis. (That the reconstructed bytes are correct, i.e. equal the real source, is
            // guaranteed by FileReceiver's whole-file checksum verification against the capture's
            // genuine trailer — RedoneFiles/FailedFiles empty above is exactly that gate passing.)
            byte[] bFastAfter = await File.ReadAllBytesAsync(Path.Combine(dest, "b_fast.bin"));
            Assert.Equal(staleBFast.Length, bFastAfter.Length);
            Assert.NotEqual(
                Convert.ToHexStringLower(SHA256.HashData(staleBFast)),
                Convert.ToHexStringLower(SHA256.HashData(bFastAfter)));

            // Frame boundaries carry no semantics: assert on the demuxed logical stream, prologue
            // stripped. Unlike the whole-file captures in PullSessionReplayTests, ssh31-pull-checksum
            // is a genuine -rtc (delta-mode) capture, so b_fast's real signature — generated against
            // a basis that is byte-identical to the captured pre-pull dst-b_fast.bin — matches the
            // captured client's request byte-for-byte with no patching needed; c_new gets a null
            // head on both sides (no local basis to sign); a_match is never posted as a transfer
            // request at all (a hang here would time out instead of failing this assertion).
            byte[] written = await transport.WrittenBytesAsync();
            int ourPrologue = 4 + 1 + "xxh128".Length;
            byte[] ours = Demux(written[ourPrologue..]);

            byte[] captured = Capture("ssh31-pull-checksum", "c2s.bin");
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

    /// <summary>
    /// Reconstructs the capture's pre-pull <c>/t/c1dst</c> exactly (capture.sh, ssh31-pull-checksum
    /// section): <c>a_match.txt</c> content-matching but stale, <c>b_fast.bin</c> the captured basis
    /// with a fresh mtime, <c>subdir/nested.txt</c> fresh, <c>c_new.txt</c> absent. Directory mtimes
    /// are applied last, deepest first, mirroring <c>PullSessionReplayTests.CreateMatchingTreeAsync</c>
    /// — writing files into a directory bumps its own mtime on Windows.
    /// </summary>
    private static async Task CreateCapturedDestinationTreeAsync(string dest, byte[] bFastBytes)
    {
        Directory.CreateDirectory(Path.Combine(dest, "subdir"));

        var staleMtime = new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var freshMtime = new DateTime(2021, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        string aMatch = Path.Combine(dest, "a_match.txt");
        await File.WriteAllBytesAsync(aMatch, "hello rsync\n"u8.ToArray());
        File.SetLastWriteTimeUtc(aMatch, staleMtime);

        string bFast = Path.Combine(dest, "b_fast.bin");
        await File.WriteAllBytesAsync(bFast, bFastBytes);
        File.SetLastWriteTimeUtc(bFast, freshMtime);

        string nested = Path.Combine(dest, "subdir", "nested.txt");
        await File.WriteAllBytesAsync(nested, "nested\n"u8.ToArray());
        File.SetLastWriteTimeUtc(nested, freshMtime);

        Directory.SetLastWriteTimeUtc(Path.Combine(dest, "subdir"), freshMtime);
        Directory.SetLastWriteTimeUtc(dest, freshMtime);
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
