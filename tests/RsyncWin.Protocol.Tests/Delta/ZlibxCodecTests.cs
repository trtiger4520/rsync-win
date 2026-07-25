using System.IO.Pipelines;
using RsyncWin.Protocol.Checksums;
using RsyncWin.Protocol.Delta;
using RsyncWin.Protocol.Mux;

namespace RsyncWin.Protocol.Tests.Delta;

/// <summary>
/// zlibx (<c>-z</c>) codec symmetry: the <see cref="MatchSearcher"/> encoder and the
/// <see cref="FileReceiver"/> decoder are byte-round-trip inverses. The decoder is separately pinned
/// against real rsync captures (<c>PullZlibxReplayTests</c>); this proves our <em>encoder</em> emits
/// a stream that decodes back to the original — the hermetic half of the push-side gate (the live
/// half is a real rsync server reconstructing our compressed push).
/// </summary>
public class ZlibxCodecTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(700)]
    [InlineData(65536)]
    [InlineData(300000)]
    public void InflateRun_IsTheInverseOfDeflateRun(int length)
    {
        byte[] data = new byte[length];
        new Random(length + 1).NextBytes(data);
        // A mix of compressible runs and random bytes.
        for (int i = 0; i < length / 3; i++)
            data[i] = (byte)'A';

        byte[] compressed = ZlibxTokenCodec.DeflateRun(data);
        byte[] restored = length == 0 ? [] : ZlibxTokenCodec.InflateRun(compressed);

        Assert.Equal(data, restored);
    }

    [Fact]
    public async Task FullTransfer_EncodeThenDecode_ReconstructsSource()
    {
        byte[] source = new byte[300000];
        new Random(7).NextBytes(source);
        for (int i = 0; i < 120000; i++) source[i] = (byte)(i % 7); // a compressible stretch

        byte[] reconstructed = await RoundTripAsync(source, basis: null, SumHeader.Null, []);
        Assert.Equal(source, reconstructed);
    }

    [Fact]
    public async Task Delta_EncodeThenDecode_ReconstructsSource_AgainstStaleBasis()
    {
        byte[] source = new byte[300000];
        new Random(11).NextBytes(source);

        byte[] basis = (byte[])source.Clone();
        "XXXXXXXX"u8.CopyTo(basis.AsSpan(1000));
        "YYYY"u8.CopyTo(basis.AsSpan(150000));

        SignatureResult signature = await SignatureGenerator.GenerateAsync(
            new MemoryStream(basis, writable: false), ChecksumAlgorithm.XxHash128, seed: 0x1234, checksumSeedFix: true);

        byte[] reconstructed = await RoundTripAsync(source, basis, signature.Header, ParseEntries(signature),
            seed: 0x1234, checksumSeedFix: true);
        Assert.Equal(source, reconstructed);
    }

    /// <summary>
    /// Pins the two BCL behaviors <see cref="ZlibxTokenCodec.RunInflater"/> is built on, neither of
    /// which is a documented contract: a <see cref="System.IO.Compression.DeflateStream"/> whose source
    /// runs dry returns 0 rather than throwing, and it RESUMES with its window intact when more input
    /// arrives. The window part is what decodes rsync's real cross-run back-references
    /// (<c>ssh31-pull-z-crossrun</c>) — the assertion below first proves the fixture actually contains
    /// one, so this cannot silently degrade into testing nothing.
    /// </summary>
    [Fact]
    public void RunInflater_ResumesAfterInputExhausted_AndKeepsWindowAcrossRuns()
    {
        byte[] shared = new byte[4096];
        new Random(23).NextBytes(shared);
        byte[] tail = new byte[512];
        new Random(29).NextBytes(tail);

        byte[] run0 = shared;
        byte[] run1 = [.. shared, .. tail]; // repeats run0 -> the continuous deflater back-references it
        IReadOnlyList<byte[]> payloads = DeflateContinuously(run0, run1);

        // The fixture must really cross-reference, or this test proves nothing about the window.
        Assert.False(InflatedStandalone(payloads[1]) is { } solo && solo.SequenceEqual(run1),
            "run 1 must NOT decode standalone — otherwise there is no cross-run reference to test");

        using var inflater = new ZlibxTokenCodec.RunInflater();
        Assert.Equal(run0, DecodeRun(inflater, payloads[0]));
        Assert.Equal(run1, DecodeRun(inflater, payloads[1]));
    }

    /// <summary>
    /// The receiver drains after every DEFLATED_DATA chunk, not only at run boundaries, so the
    /// inflater is routinely left needing input part-way through a deflate block. Feeding one run in
    /// small pieces with a drain after each must produce exactly the same bytes.
    /// </summary>
    [Fact]
    public void RunInflater_DrainedMidRun_DecodesTheRunExactly()
    {
        byte[] run = new byte[70000];
        new Random(31).NextBytes(run);
        for (int i = 0; i < 40000; i++) run[i] = (byte)(i % 5); // a compressible stretch
        byte[] payload = DeflateContinuously(run)[0];

        using var inflater = new ZlibxTokenCodec.RunInflater();
        var decoded = new MemoryStream();
        for (int offset = 0; offset < payload.Length; offset += 100)
        {
            inflater.Feed(payload.AsSpan(offset, Math.Min(100, payload.Length - offset)));
            Drain(inflater, decoded); // mid-run: usually yields nothing, must never lose state
        }
        inflater.EndRun();
        Drain(inflater, decoded);

        Assert.Equal(run, decoded.ToArray());
    }

    /// <summary>Builds the on-the-wire DEFLATED_DATA payloads for <paramref name="runs"/> the way
    /// rsync's zlibx sender does: ONE deflater for the whole file, a sync flush per literal run, and
    /// the trailing <c>00 00 ff ff</c> marker stripped from each run.</summary>
    private static IReadOnlyList<byte[]> DeflateContinuously(params byte[][] runs)
    {
        var payloads = new List<byte[]>(runs.Length);
        using var sink = new MemoryStream();
        var deflate = new System.IO.Compression.DeflateStream(
            sink, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true);
        int consumed = 0;
        foreach (byte[] run in runs)
        {
            deflate.Write(run);
            deflate.Flush();
            int flushed = (int)sink.Length;
            payloads.Add(sink.GetBuffer().AsSpan(consumed, flushed - consumed - 4).ToArray());
            consumed = flushed;
        }
        deflate.Dispose();
        return payloads;
    }

    /// <summary>Inflates one payload in isolation, or null if that throws — the expected outcome for a
    /// run that back-references an earlier one.</summary>
    private static byte[]? InflatedStandalone(byte[] payload)
    {
        try
        {
            return ZlibxTokenCodec.InflateRun(payload);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static byte[] DecodeRun(ZlibxTokenCodec.RunInflater inflater, byte[] payload)
    {
        var decoded = new MemoryStream();
        inflater.Feed(payload);
        inflater.EndRun();
        Drain(inflater, decoded);
        return decoded.ToArray();
    }

    private static void Drain(ZlibxTokenCodec.RunInflater inflater, Stream destination)
    {
        byte[] buffer = new byte[8192];
        while (true)
        {
            int read = inflater.Read(buffer);
            if (read == 0)
                return;
            destination.Write(buffer, 0, read);
        }
    }

    /// <summary>Encodes <paramref name="source"/> against a signature as a zlibx reply (sum head +
    /// compressed tokens + whole-file trailer), then decodes it back through <see cref="FileReceiver"/>
    /// with the same basis and returns the reconstructed bytes.</summary>
    private static async Task<byte[]> RoundTripAsync(
        byte[] source, byte[]? basis, SumHeader header, IReadOnlyList<BlockSignature> blockSums,
        int seed = 0, bool checksumSeedFix = false)
    {
        const ChecksumAlgorithm algorithm = ChecksumAlgorithm.XxHash128;
        const int protocol = 31;

        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        var writer = new MultiplexWriter(pipe.Writer);

        Span<byte> headBytes = stackalloc byte[SumHeader.Size];
        header.Write(headBytes);
        writer.Write(headBytes);

        MatchSearcher.Search(writer, source, header, blockSums, algorithm, seed, checksumSeedFix, CompressionMethod.Zlibx);

        WholeFileChecksum hasher = StrongChecksum.CreateFileSum(algorithm, seed, protocol);
        hasher.Append(source);
        Span<byte> trailer = stackalloc byte[16];
        int trailerLength = hasher.Finish(trailer);
        writer.Write(trailer[..trailerLength]);

        await writer.FlushAsync();
        await pipe.Writer.CompleteAsync();

        var reader = new MultiplexReader(pipe.Reader);
        using var destination = new MemoryStream();
        FileReceiveResult result = await FileReceiver.ReceiveAsync(
            reader, destination, algorithm, seed, protocol, trailerLength,
            basis: basis is null ? null : new MemoryStream(basis, writable: false),
            compression: CompressionMethod.Zlibx);

        Assert.True(result.ChecksumMatches, "whole-file trailer must verify after a clean round-trip");
        return destination.ToArray();
    }

    private static List<BlockSignature> ParseEntries(SignatureResult signature)
    {
        var entries = new List<BlockSignature>(signature.Header.Count);
        int entrySize = 4 + signature.Header.StrongSumLength;
        int offset = SumHeader.Size;
        for (int i = 0; i < signature.Header.Count; i++)
        {
            uint weak = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(signature.Wire.AsSpan(offset, 4));
            byte[] strong = signature.Wire.AsSpan(offset + 4, signature.Header.StrongSumLength).ToArray();
            entries.Add(new BlockSignature(weak, strong));
            offset += entrySize;
        }
        return entries;
    }
}
