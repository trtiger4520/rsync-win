using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
using RsyncWin.Protocol.Checksums;
using RsyncWin.Protocol.Delta;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Session;

namespace RsyncWin.Protocol.Tests.Delta;

/// <summary>
/// <see cref="MatchSearcher"/> golden-vector and shape coverage: the sender's rolling-match token
/// stream compared byte-exact against real captured c2s traffic (<c>ssh31-push-delta</c>,
/// <c>ssh31-push-redo</c>, <c>ssh31-push-rt</c>'s null-head literal path), plus a synthetic unit
/// test for the want_i adjacency preference on identical repeated blocks.
/// </summary>
/// <remarks>
/// All comparisons demux via <see cref="MultiplexReader"/> on both sides (the captured raw bytes,
/// sliced after skipping the fixed-form handshake prologue; and our own <see cref="MultiplexWriter"/>
/// output, read back through a fresh reader) rather than a bespoke frame parser — the mux layer
/// already strips frame headers as you read through it, which is the "demux helper" this test
/// leans on.
/// </remarks>
public class MatchSearcherTests
{
    /// <summary>Negotiates against a capture's real server handshake to recover the exact seed/algorithm/seed-fix that capture used — never hardcoded, since checksum_seed is time-based per run.</summary>
    private static async Task<SessionContext> NegotiateAsync(string capture)
    {
        var throwaway = new Pipe();
        return await HandshakeRunner.RunClientAsync(
            PipeReader.Create(new MemoryStream(TestFixtures.Bytes(capture, "s2c.bin"))),
            throwaway.Writer, new HandshakeOptions());
    }

    /// <summary>Parses a <see cref="SignatureGenerator"/> wire blob back into the flat per-block shape <see cref="MatchSearcher"/> consumes — deliberately independent of <see cref="SumRequestReader"/>.</summary>
    private static List<BlockSignature> ParseEntries(SignatureResult signature)
    {
        var entries = new List<BlockSignature>(signature.Header.Count);
        int entrySize = 4 + signature.Header.StrongSumLength;
        int offset = SumHeader.Size;
        for (int i = 0; i < signature.Header.Count; i++)
        {
            uint weak = BinaryPrimitives.ReadUInt32LittleEndian(signature.Wire.AsSpan(offset, 4));
            byte[] strong = signature.Wire.AsSpan(offset + 4, signature.Header.StrongSumLength).ToArray();
            entries.Add(new BlockSignature(weak, strong));
            offset += entrySize;
        }
        return entries;
    }

    /// <summary>Runs <see cref="MatchSearcher.Search"/> and returns the demuxed logical bytes it wrote (tokens only, no trailer — that is the caller's job, not this method's).</summary>
    private static async Task<byte[]> RunSearchAsync(
        byte[] source, SumHeader header, IReadOnlyList<BlockSignature> blockSums,
        ChecksumAlgorithm algorithm, int seed, bool checksumSeedFix)
    {
        // Unbounded: a large token stream (redo/null-head cases, hundreds of KB) written in one
        // batch before anything reads it would otherwise deadlock against the default 64KB
        // PauseWriterThreshold (FlushAsync blocks for a reader that only runs after it returns).
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        var writer = new MultiplexWriter(pipe.Writer);
        MatchSearcher.Search(writer, source, header, blockSums, algorithm, seed, checksumSeedFix);
        await writer.FlushAsync();
        await pipe.Writer.CompleteAsync();
        ReadResult framedResult = await pipe.Reader.ReadAsync();
        byte[] framed = framedResult.Buffer.ToArray();

        var reader = new MultiplexReader(PipeReader.Create(new MemoryStream(framed)));
        int logicalLength = LogicalLength(framed);
        return logicalLength == 0 ? [] : await reader.ReadDataExactlyAsync(logicalLength);
    }

    /// <summary>Sums the payload lengths of every MSG_DATA frame — the exact logical byte count our own writer produced, so the reader below can demand exactly that many bytes back.</summary>
    private static int LogicalLength(byte[] framed)
    {
        int total = 0;
        for (int offset = 0; offset + MuxHeader.Size <= framed.Length;)
        {
            MuxHeader header = MuxHeader.Read(framed.AsSpan(offset));
            offset += MuxHeader.Size;
            if (header.Tag == MessageTag.Data)
                total += header.PayloadLength;
            offset += header.PayloadLength;
        }
        return total;
    }

    [Fact]
    public async Task PushDeltaCapture_TokenStream_MatchesCapturedBytesExactly()
    {
        // Source: the clean 300000-byte tree file (result.bin, post-successful-push, byte-identical
        // to what the client sent — verified against ssh31-push-rt's own reconstructed literal
        // payload for the same file in this same test class below).
        byte[] source = TestFixtures.Bytes("ssh31-push-delta", "result.bin");

        // Basis: the stale pre-existing destination content the real capture's generator signed
        // (docs/transfer-spec.md pin — same patch recipe as SignatureGeneratorTests.ReconstructDeltaBasis
        // for the pull-direction twin of this capture).
        byte[] basis = (byte[])source.Clone();
        Encoding.ASCII.GetBytes("XXXXXXXX").CopyTo(basis, 1000);
        Encoding.ASCII.GetBytes("YYYY").CopyTo(basis, 150000);

        SessionContext session = await NegotiateAsync("ssh31-push-delta");
        SignatureResult signature = await SignatureGenerator.GenerateAsync(
            new MemoryStream(basis, writable: false),
            session.TransferChecksum, session.ChecksumSeed, session.ChecksumSeedFix);

        Assert.Equal(new SumHeader(429, 700, 2, 400), signature.Header);

        byte[] actual = await RunSearchAsync(
            source, signature.Header, ParseEntries(signature),
            session.TransferChecksum, session.ChecksumSeed, session.ChecksumSeedFix);

        // Captured c2s, prologue skipped: flist(1 entry) + ndx + iflags + 16B head echo occupy
        // logical [0,47); tokens (through END) occupy [47,3167); the whole-file trailer (not our
        // job) follows at [3167,3183) — all four offsets independently re-derived from the capture
        // for this task, not taken on faith.
        var captured = new MultiplexReader(PipeReader.Create(new MemoryStream(
            TestFixtures.Bytes("ssh31-push-delta", "c2s.bin")[30..])));
        await captured.ReadDataExactlyAsync(47);
        byte[] expected = await captured.ReadDataExactlyAsync(3167 - 47);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task PushRedoCapture_BothPhases_MatchCapturedBytesExactly()
    {
        // result.bin is the redo capture's FINAL destination state; the redo eventually succeeds,
        // so by construction it is byte-identical to the sender's own (never-corrupted) source.
        byte[] source = TestFixtures.Bytes("ssh31-push-redo", "result.bin");
        Assert.Equal(4194304, source.Length);

        // Phase-0 basis: source with its first 262144 bytes replaced by unrelated filler (the real
        // capture used a "redobasis" deterministic blob there; the exact filler bytes are
        // immaterial — literal tokens carry OUR source bytes regardless of what the basis held, so
        // any content that reliably fails to match is equivalent for a byte-exact token comparison).
        byte[] phase0Basis = (byte[])source.Clone();
        Array.Clear(phase0Basis, 0, 262144);

        // Redo basis: phase-0 basis with the induced-mismatch patch applied (docs/transfer-spec.md
        // §6 redo mechanics) — same recipe the real capture's corrupter used.
        byte[] redoBasis = (byte[])phase0Basis.Clone();
        Encoding.ASCII.GetBytes("ZZZZ").CopyTo(redoBasis, 4100000);

        SessionContext session = await NegotiateAsync("ssh31-push-redo");

        SignatureResult phase0Signature = await SignatureGenerator.GenerateAsync(
            new MemoryStream(phase0Basis, writable: false),
            session.TransferChecksum, session.ChecksumSeed, session.ChecksumSeedFix);
        Assert.Equal(new SumHeader(2048, 2048, 2, 0), phase0Signature.Header);

        SignatureResult redoSignature = await SignatureGenerator.GenerateAsync(
            new MemoryStream(redoBasis, writable: false),
            session.TransferChecksum, session.ChecksumSeed, session.ChecksumSeedFix, s2Length: 16);
        Assert.Equal(new SumHeader(2048, 2048, 16, 0), redoSignature.Header);

        byte[] actualPhase0 = await RunSearchAsync(
            source, phase0Signature.Header, ParseEntries(phase0Signature),
            session.TransferChecksum, session.ChecksumSeed, session.ChecksumSeedFix);
        byte[] actualRedo = await RunSearchAsync(
            source, redoSignature.Header, ParseEntries(redoSignature),
            session.TransferChecksum, session.ChecksumSeed, session.ChecksumSeedFix);

        // Captured c2s, prologue skipped: phase-0 reply's ndx+iflags+head end at logical offset 41,
        // its tokens (through END) run to 269901; the redo re-request (persistent-ndx-state escape
        // form FE 00 00) starts at 269918, its head ends at 269939, its tokens run to 541847 —
        // every one of these independently re-derived from the capture, not the task prompt's
        // approximate "starts at log 28" (that number belongs to a different, shorter flist and
        // does not match this capture; the redo-start offset 269918 DID independently verify).
        var captured = new MultiplexReader(PipeReader.Create(new MemoryStream(
            TestFixtures.Bytes("ssh31-push-redo", "c2s.bin")[30..])));
        await captured.ReadDataExactlyAsync(41);
        byte[] expectedPhase0 = await captured.ReadDataExactlyAsync(269901 - 41);
        await captured.ReadDataExactlyAsync(16); // phase-0 trailer, not ours
        await captured.ReadDataExactlyAsync(269918 - 269917); // DONE#1 (1 byte)
        await captured.ReadDataExactlyAsync(269939 - 269918); // redo's ndx+iflags+head
        byte[] expectedRedo = await captured.ReadDataExactlyAsync(541847 - 269939);

        Assert.Equal(expectedPhase0, actualPhase0);
        Assert.Equal(expectedRedo, actualRedo);
    }

    [Fact]
    public async Task PushRtCapture_NullHead_ChunksExactlyLikeTheCapturedLiteralStream()
    {
        // The null-head (full-transfer) request for b003_300k.bin in ssh31-push-rt: same file
        // content as the push-delta capture (both trees use the identical detbytes recipe),
        // reconstructed here straight from the captured literal payload rather than assumed.
        var capturedForContent = new MultiplexReader(PipeReader.Create(new MemoryStream(
            TestFixtures.Bytes("ssh31-push-rt", "c2s.bin")[30..])));
        // Skip through: flist (9 entries) + 3 earlier replies (b000_empty, b001_small.txt,
        // b002_64k.bin) + this reply's own ndx+iflags+head, landing exactly on its first token.
        // (Offsets below are independently located — see the task report — not guessed.)
        await capturedForContent.ReadDataExactlyAsync(65895);
        byte[] expectedTokens = await capturedForContent.ReadDataExactlyAsync(365939 - 65895); // through END

        byte[] source = TestFixtures.Bytes("ssh31-push-delta", "result.bin"); // same 300000-byte content
        Assert.Equal(300000, source.Length);

        byte[] actualTokens = await RunSearchAsync(
            source, SumHeader.Null, [], ChecksumAlgorithm.XxHash128, seed: 0, checksumSeedFix: false);

        Assert.Equal(expectedTokens, actualTokens);
    }

    [Fact]
    public async Task IdenticalRepeatedBlocks_PreferAdjacentBlock_EmitsSequentialReferences()
    {
        // want_i heuristic (docs/transfer-spec.md, behavior-derived): four identical 700-byte
        // blocks in the basis all share the same weak+strong sum. Without the adjacency
        // preference, a naive "first candidate" search would emit REF 0 four times; the correct
        // behavior emits 0,1,2,3 so runs of duplicate blocks map to sequential references.
        byte[] block = new byte[700];
        new Random(1234).NextBytes(block);

        byte[] tail = "not-a-block-match"u8.ToArray();

        byte[] basis = new byte[700 * 4];
        for (int i = 0; i < 4; i++)
            block.CopyTo(basis, i * 700);

        byte[] source = new byte[basis.Length + tail.Length];
        basis.CopyTo(source, 0);
        tail.CopyTo(source, basis.Length);

        var header = new SumHeader(4, 700, 8, 0);
        uint blockWeak = RollingChecksum.Compute(block);
        Span<byte> blockDigest = stackalloc byte[16];
        StrongChecksum.ComputeBlockSum(ChecksumAlgorithm.Md5, seed: 0, checksumSeedFix: false, block, blockDigest);
        byte[] blockStrong = blockDigest[..8].ToArray();

        var blockSums = new List<BlockSignature>();
        for (int i = 0; i < 4; i++)
            blockSums.Add(new BlockSignature(blockWeak, blockStrong));

        byte[] written = await RunSearchAsync(
            source, header, blockSums, ChecksumAlgorithm.Md5, seed: 0, checksumSeedFix: false);

        var tokens = new List<int>();
        int offset = 0;
        while (offset < written.Length)
        {
            int value = BinaryPrimitives.ReadInt32LittleEndian(written.AsSpan(offset, 4));
            offset += 4;
            tokens.Add(value);
            if (value > 0)
                offset += value; // skip the literal payload
        }

        // -1, -2, -3, -4 decode to block indices 0, 1, 2, 3 (Token.BlockIndex = -(raw+1)).
        Assert.Equal([-1, -2, -3, -4, tail.Length, 0], tokens);
    }

    /// <summary>
    /// Adversarial-review pin (F4, test-strength): <c>MatchSearcher</c> correctly slides one byte
    /// when a weak-sum hit fails strong verification (lines 130-159), but no prior test constructed
    /// an actual weak collision — a regression that accepted a match on the weak sum alone would
    /// still pass every other test in this file. This one engineers a real collision: the rolling
    /// weak checksum is <c>(s1, s2) = (sum, sum-of-prefix-sums)</c>, both of which are invariant
    /// under swapping the byte pair (a,b) with (c,d) whenever a+b == c+d — so <c>[1,4,2,3]</c> and
    /// its pair-swap <c>[2,3,1,4]</c> share a weak sum while differing in content, which the test
    /// verifies directly (via <see cref="RollingChecksum.Compute"/> and
    /// <see cref="StrongChecksum.ComputeBlockSum"/>) before relying on it.
    /// </summary>
    [Fact]
    public async Task WeakCollisionWithStrongMismatch_SlidesPast_TrueMatchLaterStillEmitsReference()
    {
        byte[] trueBlock = [1, 4, 2, 3];
        byte[] collidingWindow = [2, 3, 1, 4]; // pair-swap of trueBlock: a+b == c+d preserves (s1, s2)

        // Verify the engineered collision holds before leaning on it: same weak sum, different
        // content, and — the actual property under test — different strong sums, so a correct
        // searcher must reject collidingWindow and keep scanning.
        Assert.Equal(RollingChecksum.Compute(trueBlock), RollingChecksum.Compute(collidingWindow));
        Assert.NotEqual(trueBlock, collidingWindow);

        const int strongLength = 8;
        Span<byte> trueDigest = stackalloc byte[16];
        Span<byte> collidingDigest = stackalloc byte[16];
        StrongChecksum.ComputeBlockSum(ChecksumAlgorithm.Md5, seed: 0, checksumSeedFix: false, trueBlock, trueDigest);
        StrongChecksum.ComputeBlockSum(ChecksumAlgorithm.Md5, seed: 0, checksumSeedFix: false, collidingWindow, collidingDigest);
        byte[] trueStrong = trueDigest[..strongLength].ToArray();
        Assert.False(collidingDigest[..strongLength].SequenceEqual(trueStrong));

        byte[] gap = "GAPGAP"u8.ToArray(); // non-matching filler between the collision and the real block
        byte[] tail = "TAIL"u8.ToArray(); // non-matching filler after the real block

        byte[] source = [.. collidingWindow, .. gap, .. trueBlock, .. tail];

        var header = new SumHeader(1, 4, strongLength, 0);
        var blockSums = new List<BlockSignature> { new(RollingChecksum.Compute(trueBlock), trueStrong) };

        byte[] written = await RunSearchAsync(
            source, header, blockSums, ChecksumAlgorithm.Md5, seed: 0, checksumSeedFix: false);

        var tokens = new List<int>();
        int offset = 0;
        while (offset < written.Length)
        {
            int value = BinaryPrimitives.ReadInt32LittleEndian(written.AsSpan(offset, 4));
            offset += 4;
            tokens.Add(value);
            if (value > 0)
                offset += value;
        }

        // The colliding window (offset 0) and the gap (offset 4-9) must both ride out as one 10-byte
        // literal run — never skipped as a match — followed by the real block reference (-1, block
        // index 0) for trueBlock, then the trailing literal, then END. A searcher that bypassed
        // strong verification would instead accept collidingWindow itself as a match (an immediate
        // -1 with no leading literal token, and a completely different token sequence downstream),
        // which this exact-sequence assertion catches.
        Assert.Equal([collidingWindow.Length + gap.Length, -1, tail.Length, 0], tokens);
    }
}
