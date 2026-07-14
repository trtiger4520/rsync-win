using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RsyncWin.Engine;
using RsyncWin.Protocol.Delta;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Session;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Hermetic (hand-built, not a capture replay): a single stale existing file whose phase-0 reply's
/// whole-file trailer deliberately mismatches, forcing the P6 redo path — <c>docs/transfer-spec.md</c>
/// §6 (redo mechanics) and the ndx-codec-state note in <c>docs/wire-notes.md</c>. No capture exercises
/// an induced mismatch, so this is scripted directly against <see cref="PullSession"/> the same way
/// <see cref="PullSessionUnansweredRequestTests"/> hand-builds its scenario.
/// </summary>
public class PullSessionRedoTests
{
    private const string FileName = "big.bin";

    [Fact]
    public async Task InducedMismatch_RedoesWithPersistentNdxState_AndFullLengthSignature()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // The local basis (stale, pre-existing): same size as the flist entry so only ReportTime
        // fires (iflags 0x8008), matching the shape every real "existing but stale" capture uses.
        byte[] basisContent = Enumerable.Repeat((byte)0xAA, 100).ToArray();
        const long entryMtime = 1_600_000_000; // 2020-09-13T12:26:40Z — deliberately not the local mtime

        byte[] wrongContent = Enumerable.Repeat((byte)0xCC, 100).ToArray();
        byte[] wrongTrailer = MD5.HashData(wrongContent);
        wrongTrailer[0] ^= 0x01; // correct-looking (real digest shape) but deliberately wrong

        byte[] correctContent = Enumerable.Repeat((byte)0xBB, 100).ToArray();
        byte[] correctTrailer = MD5.HashData(correctContent);

        var serverNdx = new NdxCodec(); // the SENDER's own independent outbound ndx state
        byte[] serverBytes =
        [
            .. ScriptedSessionBuilder.HandshakePrologue,
            .. ScriptedSessionBuilder.Wrap(
                BuildRegularFileEntryWithSize(FileName, basisContent.Length, entryMtime),
                ScriptedSessionBuilder.FlistTerminator,
                // Phase 0 reply: any legal (unused, no block-reference tokens here) sum-head echo,
                // then a literal token carrying the wrong content, then the deliberately wrong trailer.
                BuildFileReply(serverNdx, 0, 0x8008, SumHeadBytes(0, 700, 2, 0),
                    BuildLiteralTokenStreamAndTrailer(wrongContent, wrongTrailer)),
                ScriptedSessionBuilder.NdxDone, // echo #1 — phase 0 done
                // Phase 1 (redo) reply: same ndx (re-requested), correct content + correct trailer.
                BuildFileReply(serverNdx, 0, 0x8008, SumHeadBytes(0, 700, 16, 0),
                    BuildLiteralTokenStreamAndTrailer(correctContent, correctTrailer)),
                ScriptedSessionBuilder.NdxDone, // echo #2 — phase 1 done
                ScriptedSessionBuilder.NdxDone, // sender's final DONE
                ScriptedSessionBuilder.StatsBlock,
                ScriptedSessionBuilder.NdxDone // goodbye DONE
            ),
        ];

        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-redo-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dest);
            string target = Path.Combine(dest, FileName);
            await File.WriteAllBytesAsync(target, basisContent, cts.Token);
            File.SetLastWriteTimeUtc(target, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            await using var transport = new ScriptedTransport(serverBytes);
            PullSession.Result result = await PullSession.RunAsync(
                transport, new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/tree/"] },
                dest, cts.Token, handshake: new HandshakeOptions());

            Assert.Equal(1, result.TransferredFiles);
            Assert.Equal([FileName], result.RedoneFiles);
            Assert.Empty(result.FailedFiles);
            Assert.Equal(correctContent, await File.ReadAllBytesAsync(target, cts.Token));

            // Independently recompute both signatures the same way WriteRegularFileRequestAsync
            // does, over the SAME (still-stale) basis bytes — the failed phase-0 attempt never
            // touched the destination, so the redo signs the identical content again.
            SignatureResult phase0Signature = await SignatureGenerator.GenerateAsync(
                new MemoryStream(basisContent, writable: false),
                result.Session.TransferChecksum, result.Session.ChecksumSeed, result.Session.ChecksumSeedFix,
                cancellationToken: cts.Token);
            SignatureResult redoSignature = await SignatureGenerator.GenerateAsync(
                new MemoryStream(basisContent, writable: false),
                result.Session.TransferChecksum, result.Session.ChecksumSeed, result.Session.ChecksumSeedFix,
                s2Length: 16, cancellationToken: cts.Token);
            Assert.Equal(16, redoSignature.Header.StrongSumLength);
            Assert.Equal(phase0Signature.Header.Count, redoSignature.Header.Count);
            Assert.Equal(phase0Signature.Header.BlockLength, redoSignature.Header.BlockLength);
            Assert.Equal(phase0Signature.Header.Remainder, redoSignature.Header.Remainder);

            byte[] expectedOurs =
            [
                0x00, 0x00, 0x00, 0x00, // empty filter list int32(0)
                0x01, // ndx 0, first time: diff from -1 -> 1, single byte
                0x08, 0x80, // iflags LE 0x8008 (TRANSFER|REPORT_TIME)
                .. phase0Signature.Wire,
                0x00, // DONE#1
                0xFE, 0x00, 0x00, // ndx 0 again: diff 0-0=0 -> persistent-state escape form (pinned)
                0x08, 0x80, // same iflags, recomputed the same way
                .. redoSignature.Wire,
                0x00, 0x00, 0x00, // DONE#2, DONE#3, DONE#4 (redo burst)
                0x00, // client's final goodbye DONE
            ];

            byte[] written = await transport.WrittenBytesAsync();
            int ourPrologue = 4 + 1 + "md5".Length;
            byte[] ours = Demux(written[ourPrologue..]);

            Assert.Equal(expectedOurs, ours);
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

    /// <summary>Mirrors <see cref="ScriptedSessionBuilder.BuildRegularFileEntry"/> but with a real,
    /// caller-chosen size instead of the hardcoded zero — needed for a signable basis.</summary>
    private static byte[] BuildRegularFileEntryWithSize(string name, long size, long mtime)
    {
        var buffer = new List<byte>();
        WriteVarint(buffer, 0x04); // ExtendedFlags zero-substitute xflags — no SAME_* bits
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        buffer.Add((byte)nameBytes.Length); // l2, no LONG_NAME
        buffer.AddRange(nameBytes);
        WriteVarlong(buffer, size, minBytes: 3);
        WriteVarlong(buffer, mtime, minBytes: 4);
        Span<byte> mode = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(mode, 0x81A4); // regular file, 0644
        buffer.AddRange(mode.ToArray());
        return [.. buffer];
    }

    private static byte[] SumHeadBytes(int count, int blockLength, int strongSumLength, int remainder)
    {
        Span<byte> head = stackalloc byte[SumHeader.Size];
        new SumHeader(count, blockLength, strongSumLength, remainder).Write(head);
        return head.ToArray();
    }

    private static byte[] BuildLiteralTokenStreamAndTrailer(byte[] content, byte[] trailer)
    {
        var buffer = new List<byte>();
        Span<byte> tokenLength = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(tokenLength, content.Length);
        buffer.AddRange(tokenLength.ToArray());
        buffer.AddRange(content);
        buffer.AddRange(new byte[4]); // end token: int32 0
        buffer.AddRange(trailer);
        return [.. buffer];
    }

    private static byte[] BuildFileReply(
        NdxCodec codec, int ndx, ushort iflags, byte[] sumHeadEcho, byte[] tokenStreamAndTrailer)
    {
        var buffer = new List<byte>();
        Span<byte> ndxBytes = stackalloc byte[NdxCodec.MaxLength];
        int ndxLength = codec.Write(ndxBytes, ndx);
        buffer.AddRange(ndxBytes[..ndxLength].ToArray());
        buffer.Add((byte)(iflags & 0xFF));
        buffer.Add((byte)(iflags >> 8));
        buffer.AddRange(sumHeadEcho);
        buffer.AddRange(tokenStreamAndTrailer);
        return [.. buffer];
    }

    private static void WriteVarint(List<byte> buffer, int value)
    {
        Span<byte> tmp = stackalloc byte[VarintCodec.MaxVarintLength];
        int n = VarintCodec.WriteVarint(tmp, value);
        buffer.AddRange(tmp[..n].ToArray());
    }

    private static void WriteVarlong(List<byte> buffer, long value, int minBytes)
    {
        Span<byte> tmp = stackalloc byte[VarintCodec.MaxVarlongLength];
        int n = VarintCodec.WriteVarlong(tmp, value, minBytes);
        buffer.AddRange(tmp[..n].ToArray());
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
