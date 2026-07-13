using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RsyncWin.Perf;

internal static class DatasetGenerator
{
    private const int BufferSize = 1024 * 1024;

    public static async Task<DatasetManifest> GenerateAsync(
        ScenarioDefinition scenario,
        string profile,
        string root,
        CancellationToken cancellationToken)
    {
        string scenarioRoot = Path.Combine(root, scenario.Name);
        RecreateDirectory(scenarioRoot);

        switch (scenario.Kind)
        {
            case DatasetKind.SmallFiles:
                await GenerateEqualFilesAsync(scenarioRoot, scenario.FileCount, 4096, compressible: false, cancellationToken);
                break;
            case DatasetKind.LargeFiles:
                await GenerateEqualFilesAsync(scenarioRoot, scenario.FileCount, scenario.LogicalBytes / scenario.FileCount, compressible: false, cancellationToken);
                break;
            case DatasetKind.MixedFiles:
                await GenerateMixedAsync(scenarioRoot, scenario.FileCount, scenario.LogicalBytes, cancellationToken);
                break;
            case DatasetKind.Delta:
                await GenerateDeltaAsync(scenarioRoot, scenario.LogicalBytes / 2, cancellationToken);
                break;
            case DatasetKind.Compressible:
                await GenerateEqualFilesAsync(scenarioRoot, scenario.FileCount, scenario.LogicalBytes / scenario.FileCount, compressible: true, cancellationToken);
                break;
            case DatasetKind.Incompressible:
                await GenerateEqualFilesAsync(scenarioRoot, scenario.FileCount, scenario.LogicalBytes / scenario.FileCount, compressible: false, cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        DatasetManifest manifest = await ManifestBuilder.BuildAsync(scenario.Name, profile, scenarioRoot, cancellationToken);
        string manifestPath = Path.Combine(root, $"{scenario.Name}.dataset-manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, PerfJsonContext.Default.DatasetManifest),
            cancellationToken);
        return manifest;
    }

    public static void Cleanup(string root, string scenario)
    {
        DeleteDirectoryIfExists(Path.Combine(root, scenario));
        string manifest = Path.Combine(root, $"{scenario}.dataset-manifest.json");
        if (File.Exists(manifest))
            File.Delete(manifest);
    }

    private static async Task GenerateEqualFilesAsync(
        string root,
        int count,
        long fileSize,
        bool compressible,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < count; i++)
        {
            string directory = Path.Combine(root, $"d{i % 256:D3}", $"s{i % 17:D2}");
            Directory.CreateDirectory(directory);
            await WriteFileAsync(Path.Combine(directory, $"file-{i:D6}.bin"), fileSize, i, compressible, cancellationToken);
        }
    }

    private static async Task GenerateMixedAsync(string root, int count, long totalBytes, CancellationToken cancellationToken)
    {
        long remaining = totalBytes;
        for (int i = 0; i < count; i++)
        {
            int remainingFiles = count - i;
            long average = remaining / remainingFiles;
            ulong mixed = Mix(PerfConstants.Seed + (ulong)i);
            long variation = (long)(mixed % 2001) - 1000;
            long size = i == count - 1 ? remaining : Math.Max(1, average + average * variation / 4000);
            size = Math.Min(size, remaining - (remainingFiles - 1));
            string directory = Path.Combine(root, $"d{i % 128:D3}", $"s{(i / 128) % 32:D2}");
            Directory.CreateDirectory(directory);
            await WriteFileAsync(Path.Combine(directory, $"mixed-{i:D6}.bin"), size, i, false, cancellationToken);
            remaining -= size;
        }
    }

    private static async Task GenerateDeltaAsync(string root, long size, CancellationToken cancellationToken)
    {
        string basis = Path.Combine(root, "basis.bin");
        string source = Path.Combine(root, "source.bin");
        await WriteFileAsync(basis, size, 0, false, cancellationToken);
        File.Copy(basis, source, overwrite: true);

        const int blockSize = 64 * 1024;
        long blockCount = (size + blockSize - 1) / blockSize;
        long edits = Math.Max(1, blockCount / 100);
        byte[] patch = new byte[blockSize];
        await using var stream = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
        var editedBlocks = new HashSet<long>();
        for (long i = 0; editedBlocks.Count < edits; i++)
        {
            long block = (long)(Mix(PerfConstants.Seed + (ulong)i) % (ulong)blockCount);
            if (!editedBlocks.Add(block))
                continue;
            FillDeterministic(patch, Mix(PerfConstants.Seed ^ (ulong)i));
            stream.Position = block * blockSize;
            int length = (int)Math.Min(blockSize, size - stream.Position);
            await stream.WriteAsync(patch.AsMemory(0, length), cancellationToken);
        }
    }

    private static async Task WriteFileAsync(
        string path,
        long size,
        int fileIndex,
        bool compressible,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous);
            long written = 0;
            ulong state = Mix(PerfConstants.Seed + (ulong)fileIndex);
            while (written < size)
            {
                int length = (int)Math.Min(BufferSize, size - written);
                if (compressible)
                {
                    for (int i = 0; i < length; i++)
                        buffer[i] = (byte)("RSYNCWIN-PERF\n"[(int)((written + i) % 14)]);
                }
                else
                {
                    FillDeterministic(buffer.AsSpan(0, length), state);
                    state = Mix(state + (ulong)length);
                }
                await stream.WriteAsync(buffer.AsMemory(0, length), cancellationToken);
                written += length;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void FillDeterministic(Span<byte> destination, ulong state)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            destination[i] = (byte)state;
        }
    }

    private static ulong Mix(ulong value)
    {
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9;
        value ^= value >> 27;
        value *= 0x94D049BB133111EB;
        return value ^ (value >> 31);
    }

    private static void RecreateDirectory(string path)
    {
        DeleteDirectoryIfExists(path);
        Directory.CreateDirectory(path);
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
            return;
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}

internal static class ManifestBuilder
{
    public static async Task<DatasetManifest> BuildAsync(
        string scenario,
        string profile,
        string root,
        CancellationToken cancellationToken)
    {
        var entries = new List<ManifestEntry>();
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(file);
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
            entries.Add(new ManifestEntry(relative, info.Length, Convert.ToHexString(hash).ToLowerInvariant(), info.LastWriteTimeUtc));
        }

        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (ManifestEntry entry in entries)
            aggregate.AppendData(Encoding.UTF8.GetBytes($"{entry.Path}\0{entry.Size}\0{entry.Sha256}\n"));

        return new DatasetManifest(
            scenario,
            profile,
            PerfConstants.Seed,
            entries.Count,
            entries.Sum(x => x.Size),
            Convert.ToHexString(aggregate.GetHashAndReset()).ToLowerInvariant(),
            entries);
    }
}
