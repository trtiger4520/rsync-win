namespace RsyncWin.Perf;

internal sealed class PreparedOperation : IDisposable
{
    private readonly List<SourceEdit> _edits;

    internal PreparedOperation(DatasetManifest expectedManifest, List<SourceEdit> edits)
    {
        ExpectedManifest = expectedManifest;
        _edits = edits;
    }

    public DatasetManifest ExpectedManifest { get; }

    public void Dispose()
    {
        foreach (SourceEdit edit in _edits)
        {
            using var stream = new FileStream(edit.Path, FileMode.Open, FileAccess.Write, FileShare.None);
            stream.Position = edit.Offset;
            stream.WriteByte(edit.OriginalByte);
            File.SetLastWriteTimeUtc(edit.Path, edit.LastWriteUtc);
        }
    }
}

internal readonly record struct SourceEdit(string Path, long Offset, byte OriginalByte, DateTime LastWriteUtc);

internal static class OperationPreparer
{
    private const int DeltaBlockSize = 64 * 1024;

    public static async Task<PreparedOperation> PrepareAsync(
        string operation,
        string source,
        string destination,
        DatasetManifest originalManifest,
        CancellationToken cancellationToken)
    {
        var edits = new List<SourceEdit>();
        switch (operation)
        {
            case "full-copy" or "compression":
                RecreateDirectory(destination);
                break;
            case "up-to-date":
                SeedDestination(source, destination);
                break;
            case "delta":
                SeedDestination(source, destination);
                ApplyDeltaEdits(source, edits);
                break;
            case "checksum":
                SeedDestination(source, destination);
                ApplyChecksumEdit(source, edits);
                break;
            case "delete":
                SeedDestination(source, destination);
                await File.WriteAllTextAsync(Path.Combine(destination, "rsyncwin-perf-extra.tmp"), "delete-me", cancellationToken);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "unknown benchmark operation");
        }

        DatasetManifest expected = edits.Count == 0
            ? originalManifest
            : await ManifestBuilder.BuildAsync(originalManifest.Scenario, originalManifest.Profile, source, cancellationToken);
        return new PreparedOperation(expected, edits);
    }

    private static void ApplyDeltaEdits(string source, List<SourceEdit> edits)
    {
        string[] files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        long totalBlocks = files.Sum(path => Math.Max(1, (new FileInfo(path).Length + DeltaBlockSize - 1) / DeltaBlockSize));
        long target = Math.Max(1, totalBlocks / 100);
        long globalBlock = 0;
        long edited = 0;
        foreach (string path in files)
        {
            var info = new FileInfo(path);
            long blocks = Math.Max(1, (info.Length + DeltaBlockSize - 1) / DeltaBlockSize);
            for (long block = 0; block < blocks && edited < target; block++, globalBlock++)
            {
                if (globalBlock % 100 != 0 && totalBlocks >= 100)
                    continue;
                long offset = Math.Min(block * DeltaBlockSize, Math.Max(0, info.Length - 1));
                EditByte(path, offset, edits, preserveMtime: false);
                edited++;
            }
            if (edited == target)
                break;
        }

        // rsync's -r quick check compares size + mtime at 1-second granularity. The edits above
        // change content but leave mtime within the same second as the freshly generated basis
        // (and its seeded destination copy), so the receiver would skip them and the delta would
        // transfer nothing. Push each edited file clearly ahead of the destination copy — which
        // kept the original mtime — so the change is detected. Dispose() restores the original.
        foreach (SourceEdit edit in edits)
            File.SetLastWriteTimeUtc(edit.Path, edit.LastWriteUtc.AddHours(1));
    }

    private static void ApplyChecksumEdit(string source, List<SourceEdit> edits)
    {
        string path = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .First(x => new FileInfo(x).Length > 0);
        EditByte(path, 0, edits, preserveMtime: true);
    }

    private static void EditByte(string path, long offset, List<SourceEdit> edits, bool preserveMtime)
    {
        DateTime mtime = File.GetLastWriteTimeUtc(path);
        int original;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stream.Position = offset;
            original = stream.ReadByte();
            if (original < 0)
                throw new InvalidDataException($"cannot edit empty benchmark file: {path}");
            stream.Position = offset;
            stream.WriteByte((byte)(original ^ 0xA5));
        }
        edits.Add(new SourceEdit(path, offset, (byte)original, mtime));
        if (preserveMtime)
            File.SetLastWriteTimeUtc(path, mtime);
    }

    private static void SeedDestination(string source, string destination)
    {
        RecreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(file));
        }
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
