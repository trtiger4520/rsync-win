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
