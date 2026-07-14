namespace RsyncWin.Fs;

/// <summary>
/// Counts and paths produced by <see cref="LocalTreePruner.Prune"/>. The four category counters
/// mirror the wire's NDX_DEL_STATS field order (deleted regular files, dirs, symlinks, devices,
/// specials — transfer-spec.md §5) so a caller can forward them verbatim.
/// </summary>
public readonly record struct PruneResult(
    IReadOnlyList<string> DeletedPaths,
    long DeletedRegularFiles,
    long DeletedDirectories,
    long DeletedSymlinks,
    long DeletedDevices,
    long DeletedSpecials);

/// <summary>
/// Deletes local destination entries that are extraneous relative to the file list the sender is
/// about to (or just did) transfer — the receiver side of rsync's <c>--delete</c>. Pure filesystem
/// walk-and-delete: no wire knowledge, no protocol decisions about WHEN to delete, just WHICH local
/// entries are not in the keep set.
/// </summary>
public static class LocalTreePruner
{
    private sealed class Counters
    {
        // Devices/Specials never get incremented on Windows (no real device/special files) but stay
        // as fields for wire completeness — auto-properties (not bare fields) so the compiler does
        // not flag them CS0649 "never assigned".
        public long Files { get; set; }
        public long Dirs { get; set; }
        public long Symlinks { get; set; }
        public long Devices { get; set; }
        public long Specials { get; set; }
    }

    /// <summary>
    /// Enumerates the children of <paramref name="destRoot"/> (the transfer root itself is never a
    /// deletion candidate) and deletes every entry whose dest-relative, '\'-joined path is not in
    /// <paramref name="keepRelativePaths"/>. Comparison is <see cref="StringComparer.OrdinalIgnoreCase"/>
    /// because NTFS is case-insensitive: an entry that only case-differs from a kept path is the same
    /// on-disk file and must be kept, since an over-eager delete is the dangerous failure mode.
    /// <para>
    /// A directory reparse point (junction or directory symlink) is never walked into — deleting the
    /// contents of a junction target could destroy data outside the tree — so an extraneous one is
    /// removed as a single symlink entry. A kept directory's children are only visited when
    /// <paramref name="recurse"/> is true; an extraneous directory (top-level or nested) is always
    /// deleted wholesale with its full subtree, since every entry under it is extraneous too (rsync's
    /// flist always includes a kept entry's ancestor directories, so nothing kept can live under an
    /// extraneous one).
    /// </para>
    /// </summary>
    public static PruneResult Prune(string destRoot, IReadOnlySet<string> keepRelativePaths, bool recurse)
    {
        string root = Path.GetFullPath(destRoot);
        var paths = new List<string>();
        var counters = new Counters();

        Walk(root, root, relativePrefix: "", keepRelativePaths, recurse, forceDelete: false, paths, counters);

        return new PruneResult(paths, counters.Files, counters.Dirs, counters.Symlinks, counters.Devices, counters.Specials);
    }

    /// <param name="forceDelete">
    /// True once inside an extraneous subtree: every entry here is deleted unconditionally,
    /// ignoring the keep set and <paramref name="recurse"/> (an extraneous directory always takes
    /// its whole subtree with it).
    /// </param>
    private static void Walk(
        string root,
        string directoryPath,
        string relativePrefix,
        IReadOnlySet<string> keepRelativePaths,
        bool recurse,
        bool forceDelete,
        List<string> paths,
        Counters counters)
    {
        foreach (string childPath in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            string childName = Path.GetFileName(childPath);
            string relativeName = relativePrefix.Length == 0 ? childName : $"{relativePrefix}\\{childName}";

            FileAttributes attributes = File.GetAttributes(childPath);
            bool isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
            bool isDirectory = !isReparsePoint && (attributes & FileAttributes.Directory) != 0;
            bool isKept = !forceDelete && keepRelativePaths.Contains(relativeName);

            if (isKept)
            {
                // Only descend into a kept directory's children when recursing; a kept file or
                // reparse point needs no further action either way.
                if (isDirectory && recurse)
                    Walk(root, childPath, relativeName, keepRelativePaths, recurse, forceDelete: false, paths, counters);
                continue;
            }

            // Extraneous: delete post-order (contents before the directory itself) so a directory
            // is empty by the time it is removed. Never recurse through a reparse point — an
            // extraneous junction/dir-symlink is deleted as a single symlink entry.
            if (isDirectory)
            {
                Walk(root, childPath, relativeName, keepRelativePaths, recurse, forceDelete: true, paths, counters);
                DeleteEntry(root, childPath, attributes, isDirectory: true);
                paths.Add(childPath);
                counters.Dirs++;
            }
            else if (isReparsePoint)
            {
                DeleteEntry(root, childPath, attributes, isDirectory: (attributes & FileAttributes.Directory) != 0);
                paths.Add(childPath);
                counters.Symlinks++;
            }
            else
            {
                DeleteEntry(root, childPath, attributes, isDirectory: false);
                paths.Add(childPath);
                counters.Files++;
            }
        }
    }

    private static void DeleteEntry(string root, string path, FileAttributes attributes, bool isDirectory)
    {
        // Containment defense-in-depth: enumeration already guarantees this, but a mapper/walk bug
        // must never be the last line of defense (WindowsPathMapper's doc note applies here too).
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || (fullPath.Length > root.Length && fullPath[root.Length] != Path.DirectorySeparatorChar))
        {
            throw new InvalidOperationException($"Refusing to delete '{fullPath}': outside destination root '{root}'");
        }

        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);

        // A directory reparse point (junction/dir symlink) is removed via the directory API, not
        // File.Delete — it deletes the link itself, never the target, because we never recursed
        // into it above.
        if (isDirectory)
            Directory.Delete(path, recursive: false);
        else
            File.Delete(path);
    }
}
