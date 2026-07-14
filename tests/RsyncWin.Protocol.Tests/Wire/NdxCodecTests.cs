using RsyncWin.Protocol.Wire;

namespace RsyncWin.Protocol.Tests.Wire;

/// <summary>Vectors from docs/codec-spec.md §4. NDX_DONE is a single 0x00 byte — not write_int(-1).</summary>
public class NdxCodecTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s.Replace(" ", ""));

    private static byte[] EncodeAll(NdxCodec codec, params int[] values)
    {
        var output = new List<byte>();
        Span<byte> buffer = stackalloc byte[NdxCodec.MaxLength];
        foreach (int v in values)
            output.AddRange(buffer[..codec.Write(buffer, v)]);
        return [.. output];
    }

    [Fact]
    public void SpecSequence_EncodesExactly()
    {
        // [0,1,2,5,3,NDX_DONE]: three +1 diffs, a +3, a backward jump (absolute form), then DONE.
        byte[] wire = EncodeAll(new NdxCodec(), 0, 1, 2, 5, 3, RsyncConstants.NdxDone);

        Assert.Equal(Hex("01 01 01 03 FE 80 03 00 00 00"), wire);
    }

    [Theory]
    [InlineData(300, "FE 01 2D")]          // diff 301 = 0x012D, 2-byte BIG-endian
    [InlineData(100000, "FE 80 A0 86 01")] // diff > 0x7FFF -> absolute form, rsync's own byte order
    public void FreshEncoder_SingleValues(int ndx, string wireHex)
    {
        Assert.Equal(Hex(wireHex), EncodeAll(new NdxCodec(), ndx));
    }

    [Fact]
    public void RepeatedIndex_MustEscape_BecauseBareZeroMeansDone()
    {
        byte[] wire = EncodeAll(new NdxCodec(), 7, 7);

        Assert.Equal(Hex("08 FE 00 00"), wire);
    }

    [Fact]
    public void NegativeIndices_UseTheMagnitudeRegister()
    {
        Assert.Equal(Hex("FF 01"), EncodeAll(new NdxCodec(), RsyncConstants.NdxFlistEof));

        // -101 then -2: magnitudes 101 (diff 100), then 2 (diff -99 -> absolute form).
        Assert.Equal(Hex("FF 64 FF FE 80 02 00 00"), EncodeAll(new NdxCodec(), -101, -2));
    }

    [Fact]
    public void NdxDone_DoesNotTouchState()
    {
        var codec = new NdxCodec();
        // 5 (diff 6), DONE, then 6: diff must still be computed against 5, not reset.
        byte[] wire = EncodeAll(codec, 5, RsyncConstants.NdxDone, 6);

        Assert.Equal(Hex("06 00 01"), wire);
    }

    [Fact]
    public void RoundTrip_RandomSequences_WithIndependentDecoderState()
    {
        var random = new Random(20260708); // fixed seed: deterministic test
        var encoder = new NdxCodec();
        var decoder = new NdxCodec();
        Span<byte> buffer = stackalloc byte[NdxCodec.MaxLength];

        int previous = 0;
        for (int i = 0; i < 5000; i++)
        {
            // Mix forward walks (the common case), jumps, repeats, and the special sentinels.
            int ndx = random.Next(6) switch
            {
                0 => previous,                          // repeat -> diff 0 escape
                1 => random.Next(0, int.MaxValue),      // wild jump
                2 => RsyncConstants.NdxDone,
                3 => RsyncConstants.NdxDelStats,
                4 => -random.Next(1, 200),              // small negative family
                _ => previous + random.Next(1, 300),    // forward walk
            };
            if (ndx >= 0)
                previous = ndx;

            int written = encoder.Write(buffer, ndx);
            (int decoded, int consumed) = decoder.Read(buffer[..written]);

            Assert.Equal(ndx, decoded);
            Assert.Equal(written, consumed);
        }
    }

    [Fact]
    public void CapturedGeneratorStream_DecodesAcrossFrameBoundaries()
    {
        // Demux the client's post-handshake stream and decode the opening of the generator
        // conversation. The captured sequence is:
        //   int32 0                     empty exclude list
        //   ndx 0  iflags 0x0008        the root dir "." — ITEM_REPORT_TIME, attribute-only,
        //                               no sum head (legal per spec §5)
        //   ndx 1  iflags 0xA000        first file — ITEM_TRANSFER|ITEM_IS_NEW, so the 0x01
        //                               byte here proves prevPositive carried over from ndx 0
        //                               ACROSS a mux frame boundary
        byte[] c2s = TestFixtures.Bytes("ssh31-pull-rt", "c2s.bin");

        // Multiplexing starts right after the version int (4) + the checksum vstring.
        (_, int vstringLen) = VstringCodec.Read(c2s.AsSpan(4));
        int frameStart = 4 + vstringLen;

        // Demux every MSG_DATA payload into one logical stream.
        var payload = new List<byte>();
        int cursor = frameStart;
        while (cursor + Protocol.Mux.MuxHeader.Size <= c2s.Length)
        {
            var header = Protocol.Mux.MuxHeader.Read(c2s.AsSpan(cursor));
            cursor += Protocol.Mux.MuxHeader.Size;
            if (header.Tag == Protocol.Mux.MessageTag.Data)
                payload.AddRange(c2s.AsSpan(cursor, header.PayloadLength));
            cursor += header.PayloadLength;
        }
        Assert.Equal(c2s.Length, cursor); // the whole capture is cleanly framed

        ReadOnlySpan<byte> stream = payload.ToArray();

        // Empty exclude list: a single zero int32.
        Assert.Equal(0u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(stream));
        stream = stream[4..];

        var decoder = new NdxCodec();

        (int ndx0, int used0) = decoder.Read(stream);
        Assert.Equal(0, ndx0);
        int iflags0 = stream[used0] | stream[used0 + 1] << 8;
        Assert.Equal(0x0008, iflags0); // ITEM_REPORT_TIME — attribute-only, no sum head follows
        stream = stream[(used0 + 2)..];

        (int ndx1, int used1) = decoder.Read(stream);
        Assert.Equal(1, ndx1);         // encoded as 0x01: diff from the CARRIED-OVER prev of 0
        Assert.Equal(1, used1);
        int iflags1 = stream[used1] | stream[used1 + 1] << 8;
        Assert.Equal(0xA000, iflags1); // ITEM_TRANSFER | ITEM_IS_NEW — full-transfer request
        // ...followed by the all-zero sum head (count=0, blength=0, s2length=0, rem=0).
        Assert.All(stream.Slice(used1 + 2, 16).ToArray(), b => Assert.Equal(0, b));
    }
}
