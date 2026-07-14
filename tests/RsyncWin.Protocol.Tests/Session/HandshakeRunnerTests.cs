using System.Buffers;
using System.IO.Pipelines;
using RsyncWin.Protocol.Checksums;
using RsyncWin.Protocol.Session;

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
        var options = new HandshakeOptions { ChecksumOffer = "xxh128 md5" };

        await Assert.ThrowsAsync<ArgumentException>(
            () => RunAsync([0x20, 0, 0, 0], options));
    }
}
