using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipelines;
using RsyncWin.Protocol.Checksums;
using RsyncWin.Protocol.Delta;
using RsyncWin.Protocol.Mux;

namespace RsyncWin.Perf;

internal static class MicrobenchmarkRunner
{
    public static async Task<IReadOnlyList<MicrobenchmarkResult>> RunAsync(string profile, CancellationToken cancellationToken)
    {
        int size = profile.Equals("full", StringComparison.OrdinalIgnoreCase) ? 64 * 1024 * 1024 : 4 * 1024 * 1024;
        int operations = profile.Equals("full", StringComparison.OrdinalIgnoreCase) ? 20 : 3;
        byte[] data = CreateData(size);
        var results = new List<MicrobenchmarkResult>
        {
            Measure("rolling-checksum", operations, () => Rolling(data)),
            Measure("strong-checksum", operations, () => Strong(data)),
        };

        results.Add(await MeasureAsync("signature", operations, async () =>
        {
            using var stream = new MemoryStream(data, writable: false);
            SignatureResult signature = await SignatureGenerator.GenerateAsync(
                stream,
                ChecksumAlgorithm.XxHash128,
                unchecked((int)PerfConstants.Seed),
                checksumSeedFix: true,
                cancellationToken: cancellationToken);
            return (ulong)signature.Wire.Length;
        }));

        IReadOnlyList<BlockSignature> signatures = BuildSignatures(data, 4096);
        var header = new SumHeader(signatures.Count, 4096, 16, data.Length % 4096);
        results.Add(Measure("matching", operations, () => Matching(data, header, signatures)));
        results.Add(Measure("zlibx", operations, () => Zlibx(data)));
        return results;
    }

    private static MicrobenchmarkResult Measure(string name, int operations, Func<ulong> action)
    {
        action();
        return MeasureCore(name, operations, () => ValueTask.FromResult(action())).GetAwaiter().GetResult();
    }

    private static Task<MicrobenchmarkResult> MeasureAsync(string name, int operations, Func<ValueTask<ulong>> action) =>
        MeasureCore(name, operations, action);

    private static async Task<MicrobenchmarkResult> MeasureCore(string name, int operations, Func<ValueTask<ulong>> action)
    {
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long allocated = GC.GetTotalAllocatedBytes(precise: true);
        var stopwatch = Stopwatch.StartNew();
        ulong check = 0;
        for (int i = 0; i < operations; i++)
            check ^= await action();
        stopwatch.Stop();
        return new MicrobenchmarkResult(
            name,
            operations,
            stopwatch.Elapsed.TotalMilliseconds,
            GC.GetTotalAllocatedBytes(precise: true) - allocated,
            GC.CollectionCount(0) - g0,
            GC.CollectionCount(1) - g1,
            GC.CollectionCount(2) - g2,
            check);
    }

    private static ulong Rolling(byte[] data)
    {
        const int window = 4096;
        uint sum = RollingChecksum.Compute(data.AsSpan(0, window));
        for (int i = 0; i + window < data.Length; i++)
            sum = RollingChecksum.Roll(sum, data[i], data[i + window], window);
        return sum;
    }

    private static ulong Strong(byte[] data)
    {
        Span<byte> digest = stackalloc byte[16];
        for (int offset = 0; offset < data.Length; offset += 4096)
            StrongChecksum.ComputeBlockSum(ChecksumAlgorithm.XxHash128, 12345, true, data.AsSpan(offset, Math.Min(4096, data.Length - offset)), digest);
        return BinaryPrimitives.ReadUInt64LittleEndian(digest);
    }

    private static ulong Matching(byte[] data, SumHeader header, IReadOnlyList<BlockSignature> signatures)
    {
        var pipe = new Pipe();
        var writer = new MultiplexWriter(pipe.Writer);
        MatchResult result = MatchSearcher.Search(writer, data, header, signatures, ChecksumAlgorithm.XxHash128, 12345, true);
        pipe.Writer.Complete();
        pipe.Reader.Complete();
        return (ulong)(result.LiteralBytes ^ result.MatchedBytes);
    }

    private static ulong Zlibx(byte[] data)
    {
        byte[] compressed = ZlibxTokenCodec.DeflateRun(data);
        byte[] inflated = ZlibxTokenCodec.InflateRun(compressed);
        if (!inflated.AsSpan().SequenceEqual(data))
            throw new InvalidDataException("zlibx microbenchmark round-trip mismatch");
        return (ulong)compressed.Length;
    }

    private static IReadOnlyList<BlockSignature> BuildSignatures(byte[] data, int blockSize)
    {
        var result = new List<BlockSignature>();
        Span<byte> digest = stackalloc byte[16];
        for (int offset = 0; offset < data.Length; offset += blockSize)
        {
            ReadOnlySpan<byte> block = data.AsSpan(offset, Math.Min(blockSize, data.Length - offset));
            uint weak = RollingChecksum.Compute(block);
            StrongChecksum.ComputeBlockSum(ChecksumAlgorithm.XxHash128, 12345, true, block, digest);
            result.Add(new BlockSignature(weak, digest.ToArray()));
        }
        return result;
    }

    private static byte[] CreateData(int size)
    {
        byte[] data = new byte[size];
        ulong state = PerfConstants.Seed;
        for (int i = 0; i < data.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            data[i] = (byte)state;
        }
        return data;
    }
}
