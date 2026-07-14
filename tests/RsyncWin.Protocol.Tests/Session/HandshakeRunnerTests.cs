using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using RsyncWin.Protocol.Checksums;
using RsyncWin.Protocol.Session;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Protocol.Tests.Session;

/// <summary>
/// Replays the captured server→client handshake prologues (rsync 3.4.3 over the fake-ssh tee) and
/// checks both directions: the parsed <see cref="SessionContext"/>, the exact bytes we write, and —
/// critically — that the runner consumes <em>exactly</em> the prologue, leaving the reader on the
/// first multiplexed byte. Off-by-one consumption here is the classic exit-12 desync.
/// </summary>
public class HandshakeRunnerTests
{
    private sealed record Run(SessionContext Context, byte[] Written, PipeReader Input);

    private static async Task<Run> RunAsync(byte[] serverBytes, HandshakeOptions options)
    {
        var input = PipeReader.Create(new MemoryStream(serverBytes));
        var output = new Pipe();

        SessionContext context = await HandshakeRunner.RunClientAsync(input, output.Writer, options);

        await output.Writer.CompleteAsync();
        ReadResult written = await output.Reader.ReadAsync();
        return new Run(context, written.Buffer.ToArray(), input);
    }

    private static async Task<byte[]> NextBytesAsync(PipeReader input, int count)
    {
        ReadResult result = await input.ReadAtLeastAsync(count);
        return result.Buffer.Slice(0, count).ToArray();
    }

    [Fact]
    public async Task Proto31Capture_ParsesPrologue_AndStopsOnTheFirstMuxByte()
    {
        var run = await RunAsync(TestFixtures.Bytes("ssh31-pull-rt", "s2c.bin"), new HandshakeOptions());

        Assert.Equal(31, run.Context.Protocol);          // min(31, server's 32)
        Assert.Equal(510, run.Context.CompatFlags);      // captured 81 fe — everything except INC_RECURSE
        Assert.Equal(0x6a4dac97, run.Context.ChecksumSeed);
        Assert.Equal(ChecksumAlgorithm.Md5, run.Context.TransferChecksum);
        Assert.True(run.Context.ChecksumSeedFix);
        Assert.True(run.Context.VarintFlistFlags);
        Assert.True(run.Context.SafeFileList);
        Assert.True(run.Context.MultiplexedOutput);

        // Our side of the wire: version 31 LE, then the md5-only offer vstring. Nothing else.
        Assert.Equal((byte[])[0x1f, 0, 0, 0, 0x03, (byte)'m', (byte)'d', (byte)'5'], run.Written);

        // The very next readable bytes must be the server's first mux frame header.
        Assert.Equal((byte[])[0xc4, 0x00, 0x00, 0x07], await NextBytesAsync(run.Input, 4));
    }

    [Fact]
    public async Task Proto30Capture_SameShapeAsProto31()
    {
        var run = await RunAsync(
            TestFixtures.Bytes("ssh30-pull-rt", "s2c.bin"),
            new HandshakeOptions { AdvertisedProtocol = 30 });

        Assert.Equal(30, run.Context.Protocol);
        Assert.Equal(510, run.Context.CompatFlags);
        Assert.Equal(0x6a4da9d7, run.Context.ChecksumSeed);
        Assert.Equal(ChecksumAlgorithm.Md5, run.Context.TransferChecksum);
        // 30 is the mux-out boundary: the captured proto-30 c2s stream frames the exclude int
        // (04 00 00 07 at offset 0x1E) where the proto-29 stream writes it raw. Pin >= 30, not > 30.
        Assert.True(run.Context.MultiplexedOutput);
        Assert.Equal((byte[])[0x1e, 0, 0, 0, 0x03, (byte)'m', (byte)'d', (byte)'5'], run.Written);
        Assert.Equal((byte[])[0xc4, 0x00, 0x00, 0x07], await NextBytesAsync(run.Input, 4));
    }

    [Fact]
    public async Task Proto29Capture_IsJustVersionAndSeed()
    {
        var run = await RunAsync(
            TestFixtures.Bytes("ssh29-pull-rt", "s2c.bin"),
            new HandshakeOptions { AdvertisedProtocol = 29 });

        Assert.Equal(29, run.Context.Protocol);
        Assert.Equal(0, run.Context.CompatFlags);
        Assert.Equal(0x6a4daa17, run.Context.ChecksumSeed);
        Assert.Equal(ChecksumAlgorithm.Md4, run.Context.TransferChecksum); // pre-negotiation default
        Assert.False(run.Context.MultiplexedOutput);                       // 29 writes raw client→server

        // At 29 the client contributes nothing but its version int.
        Assert.Equal((byte[])[0x1d, 0, 0, 0], run.Written);
        Assert.Equal((byte[])[0xce, 0x00, 0x00, 0x07], await NextBytesAsync(run.Input, 4));
    }

    [Fact]
    public async Task SecludedArgs_WritesTheNulArgListBeforeTheVersionInt()
    {
        // --secluded-args (-s): the held-back remote path travels as a pre-version NUL list.
        // Replays the real ssh31-secluded-spacepath server handshake; our c2s prologue must open
        // with "rsync\0.\0/t/space dir/\0\0" (byte-identical to the captured client) then the
        // version int and our md5 offer. The space in the path is preserved literally — the point
        // of -s.
        var run = await RunAsync(
            TestFixtures.Bytes("ssh31-secluded-spacepath", "s2c.bin"),
            new HandshakeOptions { SecludedArgs = ["/t/space dir/"] });

        Assert.Equal(31, run.Context.Protocol);

        List<byte> expected =
        [
            .. "rsync\0.\0/t/space dir/\0\0"u8.ToArray(), // pre-version NUL list (23 bytes)
            0x1f, 0, 0, 0,                                 // version 31 LE
            0x03, (byte)'m', (byte)'d', (byte)'5',         // our md5-only checksum offer vstring
        ];
        Assert.Equal(expected, run.Written);
    }

    [Fact]
    public async Task SecludedArgs_IgnoredOnDaemonMode_NoPreVersionList()
    {
        // Daemon sessions carry no version int, so there is nowhere to put a pre-version list —
        // SecludedArgs is ignored and only the checksum vstring is written (same as any daemon run).
        byte[] server = [0x81, 0xfe, 0x03, (byte)'m', (byte)'d', (byte)'5', 0x44, 0x33, 0x22, 0x11];

        var run = await RunAsync(
            server,
            new HandshakeOptions { PreNegotiatedProtocolVersion = 31, SecludedArgs = ["/t/whatever/"] });

        Assert.Equal((byte[])[0x03, (byte)'m', (byte)'d', (byte)'5'], run.Written);
    }

    [Fact]
    public async Task PeerWithoutNegotiationBit_GetsNoVstring_AndTheMd5Default()
    {
        // A protocol-30 peer that did not set CF_VARINT_FLIST_FLAGS (e.g. rsync 3.0.x): compat
        // varint 0x20 (seed-fix only), then the seed with no vstrings in between.
        byte[] server = [0x1e, 0, 0, 0, 0x20, 0x44, 0x33, 0x22, 0x11];
        var run = await RunAsync(server, new HandshakeOptions());

        Assert.Equal(30, run.Context.Protocol);
        Assert.Equal(0x20, run.Context.CompatFlags);
        Assert.Equal(0x11223344, run.Context.ChecksumSeed);
        Assert.Equal(ChecksumAlgorithm.Md5, run.Context.TransferChecksum);
        Assert.Equal((byte[])[0x1f, 0, 0, 0], run.Written); // no offer written either
    }

    [Theory]
    [InlineData(12)]  // ancient — below our floor
    [InlineData(41)]  // above MAX_PROTOCOL_VERSION: garbage or a polluted stream
    public async Task PeerVersionOutsideSaneRange_IsProtocolIncompatibility(int peerVersion)
    {
        byte[] server = [(byte)peerVersion, 0, 0, 0];

        var exception = await Assert.ThrowsAsync<ProtocolException>(
            () => RunAsync(server, new HandshakeOptions()));
        Assert.Equal(RsyncExitCode.ProtocolIncompatibility, exception.ExitCode);
    }

    [Fact]
    public async Task ServerForcingIncRecurse_IsRejected()
    {
        byte[] server = [0x20, 0, 0, 0, 0x01];

        var exception = await Assert.ThrowsAsync<ProtocolException>(
            () => RunAsync(server, new HandshakeOptions()));
        Assert.Equal(RsyncExitCode.ProtocolIncompatibility, exception.ExitCode);
    }

    [Fact]
    public async Task TruncatedPrologue_IsAStreamError()
    {
        // Version + compat + server vstring, then the stream dies two bytes into the seed.
        byte[] server = [0x20, 0, 0, 0, 0x81, 0xfe, 0x03, (byte)'m', (byte)'d', (byte)'5', 0xAA, 0xBB];

        await Assert.ThrowsAsync<InvalidDataException>(
            () => RunAsync(server, new HandshakeOptions()));
    }

    [Fact]
    public async Task OfferingAnUnimplementedChecksum_IsACallerBug()
    {
        var options = new HandshakeOptions { ChecksumOffer = "xxh3 md5" };

        await Assert.ThrowsAsync<ArgumentException>(
            () => RunAsync([0x20, 0, 0, 0], options));
    }

    // ---- Daemon mode (PreNegotiatedProtocolVersion) ------------------------------------------
    //
    // A daemon (rsync://) session negotiates its protocol version via the textual "@RSYNCD: x.y"
    // greeting, not the binary version ints. daemon31-pull-rt was captured from a real rsync 3.4.3
    // daemon; per docs/wire-notes.md ("Daemon nuance for P8") the binary stream after the textual
    // preamble is identical to the ssh path minus the two version int32s.

    /// <summary>Finds the offset right after the textual "@RSYNCD:" preamble (two lines, LF-terminated).</summary>
    private static int SkipTextualPreamble(byte[] bytes)
    {
        int firstLine = Array.IndexOf(bytes, (byte)'\n');
        int secondLine = Array.IndexOf(bytes, (byte)'\n', firstLine + 1);
        return secondLine + 1;
    }

    [Fact]
    public async Task DaemonCapture_SkipsVersionInts_AndParsesTheRestOfThePrologue()
    {
        byte[] serverAll = TestFixtures.Bytes("daemon31-pull-rt", "s2c.bin");
        byte[] binaryPhase = serverAll[SkipTextualPreamble(serverAll)..];

        var run = await RunAsync(binaryPhase, new HandshakeOptions { PreNegotiatedProtocolVersion = 31 });

        Assert.Equal(31, run.Context.Protocol);
        Assert.Equal(0x1FE, run.Context.CompatFlags); // same compat bits as the ssh31 capture
        Assert.True(run.Context.VarintFlistFlags);
        Assert.Equal(ChecksumAlgorithm.Md5, run.Context.TransferChecksum);

        // Independently walk the same bytes (compat varint, then the server's vstring reply) to
        // find where the seed lives, instead of hardcoding its offset — the seed differs per
        // capture run, so it must be read from the file, not pinned as a literal.
        (int _, int compatConsumed) = VarintCodec.ReadVarint(binaryPhase);
        (byte[] _, int vstringConsumed) = VstringCodec.Read(binaryPhase.AsSpan(compatConsumed));
        int seedOffset = compatConsumed + vstringConsumed;
        int expectedSeed = BinaryPrimitives.ReadInt32LittleEndian(binaryPhase.AsSpan(seedOffset, 4));

        Assert.Equal(expectedSeed, run.Context.ChecksumSeed);

        // No version ints in daemon mode: the client writes only its checksum-offer vstring.
        Assert.Equal((byte[])[0x03, (byte)'m', (byte)'d', (byte)'5'], run.Written);

        // The reader stops exactly on the first mux byte, taken from right after the seed.
        byte[] expectedMuxStart = binaryPhase[(seedOffset + 4)..(seedOffset + 8)];
        Assert.Equal(expectedMuxStart, await NextBytesAsync(run.Input, 4));
    }

    [Fact]
    public async Task DaemonCapture_ClientWritesOnlyAVstring_NoLeadingVersionBytes()
    {
        // The real rsync client that produced this capture offers its full default list
        // (xxh128 xxh3 xxh64 md5 md4); we only implement md5/md4/xxh64/xxh128 today (no xxh3), so
        // our own offer is narrower and cannot equal the captured bytes verbatim. What IS pinned
        // and checked here: the captured client segment is structurally exactly one vstring (no
        // version ints, nothing trailing before mux) — the same shape our own daemon-mode write
        // must produce for whatever we offer.
        byte[] clientAll = TestFixtures.Bytes("daemon31-pull-rt", "c2s.bin");
        int pos = SkipTextualPreamble(clientAll); // past "@RSYNCD: ...\n"

        int treeLine = Array.IndexOf(clientAll, (byte)'\n', pos);
        pos = treeLine + 1; // past "tree\n"

        // NUL-terminated daemon args, ending at the first empty string (the list terminator).
        while (true)
        {
            int nul = Array.IndexOf(clientAll, (byte)0, pos);
            bool isEmpty = nul == pos;
            pos = nul + 1;
            if (isEmpty)
                break;
        }

        (byte[] capturedOffer, int _) = VstringCodec.Read(clientAll.AsSpan(pos));
        Assert.Contains("md5", System.Text.Encoding.ASCII.GetString(capturedOffer));

        // Our own daemon-mode write, replayed against the same server capture as the first test.
        byte[] serverAll = TestFixtures.Bytes("daemon31-pull-rt", "s2c.bin");
        byte[] binaryPhase = serverAll[SkipTextualPreamble(serverAll)..];
        var run = await RunAsync(binaryPhase, new HandshakeOptions { PreNegotiatedProtocolVersion = 31 });

        (byte[] ourOffer, int ourConsumed) = VstringCodec.Read(run.Written);
        Assert.Equal(run.Written.Length, ourConsumed); // nothing written but the vstring itself
        Assert.Equal("md5", System.Text.Encoding.ASCII.GetString(ourOffer));
    }

    [Fact]
    public async Task DaemonMode_Protocol29_MatchesTheSshPath_NoCompatNoWrite()
    {
        // Below 30 there is no compat varint and no vstring exchange on either path — only the
        // seed is ever read. Daemon mode additionally skips the version ints, so nothing at all
        // is written.
        byte[] server = [0x11, 0x22, 0x33, 0x44]; // the seed, LE

        var run = await RunAsync(server, new HandshakeOptions { PreNegotiatedProtocolVersion = 29 });

        Assert.Equal(29, run.Context.Protocol);
        Assert.Equal(0, run.Context.CompatFlags);
        Assert.Equal(0x44332211, run.Context.ChecksumSeed);
        Assert.Equal(ChecksumAlgorithm.Md4, run.Context.TransferChecksum); // pre-negotiation default
        Assert.False(run.Context.MultiplexedOutput);
        Assert.Empty(run.Written);
    }
}
