using System.Buffers;
using RsyncWin.Protocol.FileList;

namespace RsyncWin.Fs;

/// <summary>Options for a local-to-local sync: the subset of CLI flags that means anything without
/// a wire (-z/-s are accepted upstream and never reach here).</summary>
public sealed record LocalSyncOptions(bool Recurse, bool Checksum, bool Delete);

/// <summary>Outcome of a local-to-local sync, mirroring the shape of the wire sessions' Result
/// records so Program.cs can report it through the same stderr conventions.</summary>
public sealed record LocalSyncResult(
    int TransferredFiles,
    long TransferredBytes,
    IReadOnlyList<string> SkippedDirectories,
    IReadOnlyList<(string Name, string Reason)> SkippedNonRegular,
    IReadOnlyList<(string Path, string Reason)> FailedFiles,
    PruneResult Prune,
    bool PruneSkipped);

/// <summary>
/// Direct local-to-local copy — no wire protocol, no transport. Real rsync runs its full protocol
/// over a local socketpair but defaults to <c>--whole-file</c> there (delta transfer never pays for
/// itself when both sides are local disks), so a direct copy with rsync's skip/replace semantics is
/// behaviorally equivalent. Reuses the same building blocks as the wire paths: enumeration order
/// from <see cref="FileEnumerator"/>, name mapping from <see cref="LocalPathPolicy"/>, atomic
/// replace from <see cref="DestinationReplacer"/>, and <c>--delete</c> via
/// <see cref="LocalTreePruner"/>.
/// </summary>
/// <remarks>
/// Source trailing-slash semantics follow real rsync: <c>src</c> creates <c>dest\src\...</c>,
/// <c>src\</c> (or <c>src/</c>) copies the contents directly into <c>dest\</c>. This intentionally
/// diverges from the ssh/daemon push convention (always-contents, see the TODO(P9) in Program.cs) —
/// local mode is new surface, so it starts out correct.
/// </remarks>
public static class LocalSyncEngine
{
    /// <summary>
    /// Runs the sync. Throws <see cref="ArgumentException"/> for shape errors the caller should
    /// surface as a syntax-style failure (self-copy, dest-inside-source, contents-into-a-file);
    /// <see cref="IOException"/>/<see cref="UnauthorizedAccessException"/> for a fatal source/dest
    /// root failure (missing source, unreadable tree — the caller's exit-11 path). Per-file
    /// failures never throw; they land in <see cref="LocalSyncResult.FailedFiles"/>.
    /// </summary>
    public static LocalSyncResult Run(
        string sourceSpec, string destSpec, LocalSyncOptions options, ITransferProgressSink? progress = null)
    {
        progress ??= NullProgressSink.Instance;
        LocalPathPolicy policy = LocalPathPolicy.Current;

        // Trailing-slash intent must be read off the raw specs — Path.GetFullPath normalization is
        // allowed to drop the trailing separator. A drive root ("C:\") is always "contents": there
        // is no leaf directory to re-create on the destination side.
        bool sourceEndsWithSeparator = EndsWithSeparator(sourceSpec);
        bool destEndsWithSeparator = EndsWithSeparator(destSpec);
        string fullSource = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceSpec));
        string fullDest = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destSpec));

        FileAttributes sourceAttributes = File.GetAttributes(fullSource); // missing source throws → exit 11
        bool sourceIsReparse = (sourceAttributes & FileAttributes.ReparsePoint) != 0;
        bool sourceIsDirectory = !sourceIsReparse && (sourceAttributes & FileAttributes.Directory) != 0;

        var state = new SyncState();

        if (sourceIsReparse)
        {
            // Mirrors the wire paths' warn-and-skip policy for symlinks/junctions as transfer roots.
            state.SkippedNonRegular.Add((Path.GetFileName(fullSource), "symlink"));
            return state.ToResult();
        }

        if (!sourceIsDirectory)
        {
            // Scoped like the wire paths so End() runs even if the copy throws — otherwise an
            // animated in-place progress line would be left dangling on the terminal.
            using (TransferProgressScope.Started(progress, RegularFileLengthOrZero(fullSource), 1))
                SyncSingleFileSource(fullSource, fullDest, destEndsWithSeparator, options.Checksum, state, progress);
            return state.ToResult();
        }

        if (!options.Recurse)
        {
            // rsync without -r (or -d, unimplemented) skips a directory source entirely.
            state.SkippedDirectories.Add(Path.GetFileName(fullSource));
            return state.ToResult();
        }

        bool copyContents = sourceEndsWithSeparator || IsDriveRoot(fullSource);
        string destRoot = copyContents ? fullDest : Path.Combine(fullDest, Path.GetFileName(fullSource));

        GuardSelfCopy(fullSource, destRoot, policy);

        if (copyContents && File.Exists(destRoot))
            throw new ArgumentException($"destination \"{destSpec}\" must be a directory to receive the contents of \"{sourceSpec}\"");

        IReadOnlyList<EnumeratedEntry> entries = FileEnumerator.Enumerate(fullSource); // fatal → exit 11

        // Progress denominators over the regular files in flist order (an up-to-date skip later just
        // means those bytes never get copied — same short-of-100% quirk as the wire paths).
        long progressTotalBytes = 0;
        int progressTotalFiles = 0;
        foreach (EnumeratedEntry entry in entries)
        {
            if (!entry.Wire.IsRegularFile)
                continue;
            progressTotalBytes += entry.Wire.Size;
            progressTotalFiles++;
        }
        // Scoped so End() runs on every exit (return or throw) — an aborted copy pass must still
        // terminate its in-place progress line instead of leaving the terminal mid-line.
        using IDisposable progressScope = TransferProgressScope.Started(progress, progressTotalBytes, progressTotalFiles);

        // Copy pass in flist order (parents sort before their children, so directories exist by the
        // time their contents arrive). Directory mtimes are restored afterwards, deepest first — the
        // file writes below keep bumping them.
        var directoryMtimes = new List<(string DestPath, DateTime MtimeUtc, int Depth)>();
        foreach (EnumeratedEntry entry in entries)
        {
            string name = entry.Wire.Name;
            string destPath = entry.Wire.NameBytes is [(byte)'.']
                ? destRoot
                : Path.Combine(destRoot, policy.Map(name).Mapped);

            if (entry.Wire.IsDirectory)
            {
                try
                {
                    // The transfer root is the one directory the user (or the leaf-name rule) named
                    // directly: an existing junction/mount point there is written through, never
                    // unlinked — same as the single-file path. Nested reparse points ARE transfer
                    // entries and get replaced.
                    if (entry.Wire.NameBytes is [(byte)'.'])
                        EnsureDestinationRoot(destPath);
                    else
                        EnsureDirectory(destPath);
                    directoryMtimes.Add((
                        destPath,
                        WireMtimeUtc(entry.Wire),
                        entry.Wire.NameBytes.Count(b => b == (byte)'/')));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    state.FailedFiles.Add((name, ex.Message));
                }
            }
            else if (entry.Wire.IsRegularFile)
            {
                SyncRegularFile(name, entry.AbsolutePath, destPath, options.Checksum, state, progress);
            }
            else
            {
                state.SkippedNonRegular.Add((name, entry.Wire.IsSymlink ? "symlink" : "not a regular file"));
            }
        }

        foreach ((string destPath, DateTime mtimeUtc, _) in directoryMtimes.OrderByDescending(d => d.Depth))
        {
            try
            {
                Directory.SetLastWriteTimeUtc(destPath, mtimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best-effort, like a file's per-file failure — but a directory already received its
                // contents, so a failed mtime restore alone is not worth an exit-23.
            }
        }

        if (options.Delete)
        {
            // rsync suppresses deletion when the source listing/reads hit IO errors, so a partial
            // view of the source never deletes destination data (same rule as the wire pull).
            if (state.SourceReadError)
            {
                state.PruneSkipped = true;
            }
            else
            {
                var keep = new HashSet<string>(policy.PathComparer);
                foreach (EnumeratedEntry entry in entries)
                {
                    if (entry.Wire.NameBytes is [(byte)'.'])
                        continue;
                    keep.Add(policy.Map(entry.Wire.Name).Mapped);
                }

                try
                {
                    state.Prune = LocalTreePruner.Prune(destRoot, keep, recurse: true, policy);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A locked/undeletable extraneous entry aborts the pruner mid-walk. rsync
                    // reports deletion failures per-file and exits 23 — surfacing this as a failed
                    // item (not a fatal exit-11 throw) is the closest this walk can get without
                    // teaching LocalTreePruner per-entry tolerance.
                    state.FailedFiles.Add(("--delete", ex.Message));
                }
            }
        }

        return state.ToResult();
    }

    /// <summary>Mutable accumulator threaded through the copy pass; <see cref="ToResult"/> freezes it.</summary>
    private sealed class SyncState
    {
        public int TransferredFiles;
        public long TransferredBytes;
        public List<string> SkippedDirectories { get; } = [];
        public List<(string Name, string Reason)> SkippedNonRegular { get; } = [];
        public List<(string Path, string Reason)> FailedFiles { get; } = [];
        public PruneResult Prune = new([], 0, 0, 0, 0, 0);
        public bool PruneSkipped;
        public bool SourceReadError;

        public LocalSyncResult ToResult() => new(
            TransferredFiles, TransferredBytes, SkippedDirectories, SkippedNonRegular, FailedFiles,
            Prune, PruneSkipped);
    }

    private static void SyncSingleFileSource(
        string fullSource, string fullDest, bool destEndsWithSeparator, bool checksum, SyncState state,
        ITransferProgressSink progress)
    {
        // "rsyncwin file dst\" (or dst being an existing directory) drops the file inside dst,
        // creating dst if needed; otherwise dst names the file itself. A source==target invocation
        // needs no guard: the skip fast path sees an identical file and transfers nothing.
        string target;
        if (destEndsWithSeparator || Directory.Exists(fullDest))
        {
            Directory.CreateDirectory(fullDest);
            target = Path.Combine(fullDest, Path.GetFileName(fullSource));
        }
        else
        {
            target = fullDest;
        }

        SyncRegularFile(Path.GetFileName(fullSource), fullSource, target, checksum, state, progress);
    }

    /// <summary>Brings one regular file up to date at <paramref name="destPath"/>: skip fast path
    /// (size + exact mtime), or with --checksum a streamed byte compare (attribute-only mtime touch
    /// when only the mtime differs), else copy-to-temp + atomic replace. All failures are per-file.</summary>
    private static void SyncRegularFile(
        string name, string sourcePath, string destPath, bool checksum, SyncState state,
        ITransferProgressSink progress)
    {
        FileStream source;
        try
        {
            // Open first so size/mtime and content come from one handle — and so a source-side
            // failure is distinguishable: it must suppress --delete (partial source view), while a
            // destination-side failure must not.
            source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.SourceReadError = true;
            state.FailedFiles.Add((name, ex.Message));
            return;
        }

        try
        {
            long sourceLength = source.Length;
            DateTime sourceMtimeUtc = File.GetLastWriteTimeUtc(sourcePath);

            if (File.Exists(destPath))
            {
                var dest = new FileInfo(destPath);
                if (!checksum)
                {
                    // Local fast path keeps full 100ns precision — no wire truncation to seconds.
                    if (dest.Length == sourceLength && dest.LastWriteTimeUtc == sourceMtimeUtc)
                        return;
                }
                else if (dest.Length == sourceLength && ContentsEqual(source, destPath))
                {
                    // --checksum, contents identical: rsync's attribute-only update.
                    if (dest.LastWriteTimeUtc != sourceMtimeUtc)
                        File.SetLastWriteTimeUtc(destPath, sourceMtimeUtc);
                    return;
                }
            }
            else if (Directory.Exists(destPath))
            {
                // A directory (or junction) sits where the file must go. An empty directory or a
                // reparse point is replaced (never walked into); a non-empty one is rsync's
                // "--force" territory — per-file failure.
                FileAttributes attributes = File.GetAttributes(destPath);
                if ((attributes & FileAttributes.ReparsePoint) == 0
                    && Directory.EnumerateFileSystemEntries(destPath).Any())
                {
                    state.FailedFiles.Add((name, "destination is a non-empty directory"));
                    return;
                }

                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(destPath, attributes & ~FileAttributes.ReadOnly);
                Directory.Delete(destPath, recursive: false);
            }

            string tempPath = DestinationReplacer.TempPathFor(destPath);
            progress.BeginFile(name, sourceLength);
            try
            {
                source.Position = 0; // ContentsEqual above may have consumed the stream
                using (var temp = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                    CopyReporting(source, temp, progress);
                DestinationReplacer.FinalizeReplace(tempPath, destPath, sourceMtimeUtc);
            }
            finally
            {
                progress.EndFile();
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            state.TransferredFiles++;
            state.TransferredBytes += sourceLength;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            state.FailedFiles.Add((name, ex.Message));
        }
        finally
        {
            source.Dispose();
        }
    }

    /// <summary>Makes the transfer root usable as a directory. Unlike <see cref="EnsureDirectory"/>
    /// this never unlinks an existing directory — including a junction or volume mount point, which
    /// the user named (directly or via the leaf-name rule) and expects to be written through, not
    /// destroyed. Only a plain file in the way is replaced (the contents-into-a-file case already
    /// threw upstream).</summary>
    private static void EnsureDestinationRoot(string path)
    {
        if (Directory.Exists(path))
            return;

        if (File.Exists(path))
        {
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            File.Delete(path); // a file symlink in the way deletes as the link itself
        }

        Directory.CreateDirectory(path);
    }

    /// <summary>Makes a real directory exist at <paramref name="path"/>, replacing a file or a
    /// reparse point (a junction is deleted as a link, never written through) already there.</summary>
    private static void EnsureDirectory(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            FileAttributes attributes = File.GetAttributes(path);
            bool isReparse = (attributes & FileAttributes.ReparsePoint) != 0;
            if (!isReparse && (attributes & FileAttributes.Directory) != 0)
                return; // already a real directory

            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            if ((attributes & FileAttributes.Directory) != 0)
                Directory.Delete(path, recursive: false); // directory reparse point: unlink only
            else
                File.Delete(path);
        }

        Directory.CreateDirectory(path);
    }

    /// <summary>Streamed byte-for-byte compare of the (position-0) open source stream against the
    /// destination file — cheaper than hashing when both sides are local, and strictly equivalent.
    /// Lengths are already known equal.</summary>
    private static bool ContentsEqual(FileStream source, string destPath)
    {
        source.Position = 0;
        using var dest = new FileStream(destPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        const int BufferSize = 128 * 1024;
        byte[] sourceBuffer = new byte[BufferSize];
        byte[] destBuffer = new byte[BufferSize];
        while (true)
        {
            int sourceRead = source.ReadAtLeast(sourceBuffer, BufferSize, throwOnEndOfStream: false);
            int destRead = dest.ReadAtLeast(destBuffer, BufferSize, throwOnEndOfStream: false);
            if (sourceRead != destRead)
                return false; // a side truncated mid-compare — treat as different
            if (sourceRead == 0)
                return true;
            if (!sourceBuffer.AsSpan(0, sourceRead).SequenceEqual(destBuffer.AsSpan(0, destRead)))
                return false;
        }
    }

    /// <summary>Copies <paramref name="source"/> (already positioned at 0) to
    /// <paramref name="destination"/>, reporting each written chunk to <paramref name="progress"/> so
    /// a large local file animates a progress bar. Equivalent to <see cref="Stream.CopyTo(Stream)"/>
    /// otherwise.</summary>
    private static void CopyReporting(Stream source, Stream destination, ITransferProgressSink progress)
    {
        // Pooled rather than `new byte[128*1024]`: a 128 KB buffer exceeds the 85 KB LOH threshold, so
        // a fresh one per copied file would churn the Large Object Heap across a many-file sync.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                destination.Write(buffer, 0, read);
                progress.Advance(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Best-effort byte length of a regular file for the progress denominator — 0 if it
    /// cannot be stat'd (the copy itself will then record the real per-file failure).</summary>
    private static long RegularFileLengthOrZero(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Rejects the two shapes that would recurse a tree into itself. Known limitation: this is a
    /// lexical check on <see cref="Path.GetFullPath(string)"/> output — an 8.3 short-name alias
    /// ("C:\LONGNA~1") or a junction pointing back inside the source evades it (real rsync uses
    /// st_dev/st_ino; the Windows equivalent needs GetFinalPathNameByHandle P/Invoke). The
    /// consequence is bounded: enumeration snapshots up front, so the worst case is one nested
    /// self-copy, never unbounded recursion or deletion. Recorded in docs/roadmap.md P12.
    /// </summary>
    private static void GuardSelfCopy(string fullSource, string destRoot, LocalPathPolicy policy)
    {
        string source = EnsureTrailingSeparator(fullSource);
        string dest = EnsureTrailingSeparator(Path.GetFullPath(destRoot));

        if (dest.Equals(source, policy.PathComparison))
            throw new ArgumentException($"cannot copy \"{fullSource}\" onto itself");
        if (dest.StartsWith(source, policy.PathComparison))
            throw new ArgumentException($"cannot copy \"{fullSource}\" into itself (\"{destRoot}\")");
    }

    private static string EnsureTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private static bool EndsWithSeparator(string spec) =>
        spec.EndsWith('/') || spec.EndsWith('\\');

    private static bool IsDriveRoot(string fullPath) =>
        string.Equals(fullPath, Path.GetPathRoot(fullPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>The enumeration-time mtime snapshot, reconstructed losslessly from the wire entry
    /// (FileEnumerator preserved the 100ns ticks in <see cref="FileEntry.ModifiedNanoseconds"/>).</summary>
    private static DateTime WireMtimeUtc(FileEntry entry) =>
        DateTimeOffset.FromUnixTimeSeconds(entry.ModifiedUnixSeconds).UtcDateTime
            .AddTicks(entry.ModifiedNanoseconds / 100);
}
