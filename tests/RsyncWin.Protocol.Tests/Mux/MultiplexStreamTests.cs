using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
using RsyncWin.Protocol;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Session;

namespace RsyncWin.Protocol.Tests.Mux;

public class MultiplexStreamTests
{
    private const int Proto31PrologueLength = 41; // version(4) + compat(2) + vstring(31) + seed(4)

    private static MultiplexReader ReaderOver(byte[] bytes) =>
        new(PipeReader.Create(new MemoryStream(bytes)));

    private static byte[] Frame(MessageTag tag, byte[] payload)
    {
        byte[] frame = new byte[MuxHeader.Size + payload.Length];
        new MuxHeader(tag, payload.Length).Write(frame);
        payload.CopyTo(frame, MuxHeader.Size);
        return frame;
    }

    [Fact]
    public async Task CapturedStream_DataChannel_IsContinuousAcrossFrames()
    {
        // Independently demux the captured server stream, then check the reader reproduces the
        // same continuous byte sequence when pulled in deliberately awkward 7-byte reads.
        byte[] s2c = TestFixtures.Bytes("ssh31-pull-rt", "s2c.bin")[Proto31PrologueLength..];

        var expected = new MemoryStream();
        for (int offset = 0; offset + MuxHeader.Size <= s2c.Length;)
        {
            MuxHeader header = MuxHeader.Read(s2c.AsSpan(offset));
            offset += MuxHeader.Size;
            if (header.Tag == MessageTag.Data)
                expected.Write(s2c, offset, header.PayloadLength);
            offset += header.PayloadLength;
        }
        byte[] expectedData = expected.ToArray();
        Assert.True(expectedData.Length > 200, "capture should contain a substantial data channel");

        var reader = ReaderOver(s2c);
        var actual = new MemoryStream();
        for (int remaining = expectedData.Length; remaining > 0;)
        {
            int take = Math.Min(7, remaining);
            actual.Write(await reader.ReadDataExactlyAsync(take));
            remaining -= take;
        }
        Assert.Equal(expectedData, actual.ToArray());
    }

    [Fact]
    public async Task KeepAlives_AreSkipped_NotEof()
    {
        byte[] stream = [.. Frame(MessageTag.Data, []), .. Frame(MessageTag.Data, "ab"u8.ToArray())];
        Assert.Equal("ab"u8.ToArray(), await ReaderOver(stream).ReadDataExactlyAsync(2));
    }

    [Fact]
    public async Task OutOfBandMessages_AreDispatched_AndDataStaysIntact()
    {
        byte[] stream =
        [
            .. Frame(MessageTag.Data, "ab"u8.ToArray()),
            .. Frame(MessageTag.Info, "hello"u8.ToArray()),
            .. Frame(MessageTag.Data, "cd"u8.ToArray()),
        ];
        var messages = new List<(MessageTag Tag, string Text)>();
        var reader = ReaderOver(stream);
        reader.MessageReceived = (tag, payload) => messages.Add((tag, Encoding.ASCII.GetString(payload)));

        Assert.Equal("abcd"u8.ToArray(), await reader.ReadDataExactlyAsync(4));
        Assert.Equal([(MessageTag.Info, "hello")], messages);
    }

    [Fact]
    public async Task ErrorExit_SurfacesThePeersExitCode()
    {
        byte[] code = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(code, 23);
        byte[] stream = Frame(MessageTag.ErrorExit, code);

        var exception = await Assert.ThrowsAsync<ProtocolException>(
            async () => await ReaderOver(stream).ReadDataExactlyAsync(1));
        Assert.Equal(RsyncExitCode.PartialTransferError, exception.ExitCode);
    }

    [Fact]
    public async Task UndefinedTag_MeansDesync()
    {
        byte[] stream = new byte[4];
        new MuxHeader((MessageTag)55, 0).Write(stream);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await ReaderOver(stream).ReadDataExactlyAsync(1));
    }

    [Fact]
    public async Task RoleInvalidTag_MeansDesync_EvenThoughDefined()
    {
        // MSG_REDO is rsync-internal plumbing that never legitimately crosses our wire; garbage
        // that happens to parse as its header must fail loudly, not be silently skipped.
        byte[] stream = Frame(MessageTag.Redo, new byte[4]);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await ReaderOver(stream).ReadDataExactlyAsync(1));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public async Task ErrorExit_WithIllegalPayloadLength_IsAStreamError(int payloadLength)
    {
        byte[] stream = Frame(MessageTag.ErrorExit, new byte[payloadLength]);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await ReaderOver(stream).ReadDataExactlyAsync(1));
    }

    [Fact]
    public async Task ErrorExit_ZeroLength_IsTheGoodbyeLeg_WithCodeZero()
    {
        var exception = await Assert.ThrowsAsync<ProtocolException>(
            async () => await ReaderOver(Frame(MessageTag.ErrorExit, [])).ReadDataExactlyAsync(1));
        Assert.Equal(RsyncExitCode.Ok, exception.ExitCode);
    }

    [Fact]
    public async Task Noop_IsSkippedLikeAKeepAlive_NotDispatched()
    {
        byte[] stream = [.. Frame(MessageTag.Noop, []), .. Frame(MessageTag.Data, "ok"u8.ToArray())];
        var reader = ReaderOver(stream);
        var dispatched = new List<MessageTag>();
        reader.MessageReceived = (tag, _) => dispatched.Add(tag);

        Assert.Equal("ok"u8.ToArray(), await reader.ReadDataExactlyAsync(2));
        Assert.Empty(dispatched);
    }

    [Fact]
    public async Task TruncatedFrame_IsAStreamError()
    {
        byte[] stream = Frame(MessageTag.Data, "abcd"u8.ToArray())[..6]; // header + 2 of 4 bytes

        var reader = ReaderOver(stream);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await reader.ReadDataExactlyAsync(4));
    }

    [Fact]
    public async Task Writer_ExcludeTerminator_MatchesTheCapturedBytes()
    {
        // The client's first post-handshake write in the proto-31 capture: an empty exclude list,
        // int32 0 framed as MSG_DATA — bytes 04 00 00 07 00 00 00 00 at c2s offset 30.
        var pipe = new Pipe();
        var writer = new MultiplexWriter(pipe.Writer);
        writer.Write([0, 0, 0, 0]);
        await writer.FlushAsync();
        await pipe.Writer.CompleteAsync();

        ReadResult result = await pipe.Reader.ReadAsync();
        byte[] captured = TestFixtures.Bytes("ssh31-pull-rt", "c2s.bin")[30..38];
        Assert.Equal(captured, result.Buffer.ToArray());
        Assert.Equal((byte[])[0x04, 0x00, 0x00, 0x07, 0, 0, 0, 0], captured);
    }

    [Fact]
    public async Task Writer_BatchesWritesPerFlush_AndReaderRoundTrips()
    {
        var pipe = new Pipe();
        var writer = new MultiplexWriter(pipe.Writer);
        var reader = new MultiplexReader(pipe.Reader);

        writer.Write("abc"u8);
        writer.Write("de"u8);
        await writer.FlushAsync();   // one 5-byte frame
        await writer.WriteKeepAliveAsync();
        writer.Write("fg"u8);
        await writer.FlushAsync();   // one 2-byte frame

        Assert.Equal("abcdefg"u8.ToArray(), await reader.ReadDataExactlyAsync(7));
    }

    [Fact]
    public async Task Writer_SplitsOnlyWhenTheLengthFieldOverflows()
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0));
        var writer = new MultiplexWriter(pipe.Writer);

        byte[] big = new byte[RsyncConstants.MaxMuxPayload + 1];
        big[0] = 0x11;
        big[^1] = 0x22;
        writer.Write(big);
        await writer.FlushAsync();
        await pipe.Writer.CompleteAsync();

        ReadResult result = await pipe.Reader.ReadAsync();
        byte[] wire = result.Buffer.ToArray();
        Assert.Equal(big.Length + 2 * MuxHeader.Size, wire.Length);

        MuxHeader first = MuxHeader.Read(wire);
        Assert.Equal(RsyncConstants.MaxMuxPayload, first.PayloadLength);
        MuxHeader second = MuxHeader.Read(wire.AsSpan(MuxHeader.Size + first.PayloadLength));
        Assert.Equal(1, second.PayloadLength);
        Assert.Equal(0x11, wire[MuxHeader.Size]); // first frame's payload starts at big[0]
        Assert.Equal(0x22, wire[^1]);
    }
}
