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
    /// deletion candidate) and deletes every entry whose destination-relative path is not in
    /// <paramref name="keepRelativePaths"/>. Path separators and comparison semantics follow the
    /// current platform policy.
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
        => Prune(destRoot, keepRelativePaths, recurse, LocalPathPolicy.Current);

    /// <summary>
    /// Applies the prune using an explicit local path policy. This overload is primarily useful for
    /// deterministic platform-policy tests; production callers normally use the runtime overload.
    /// </summary>
    internal static PruneResult Prune(
        string destRoot,
        IReadOnlySet<string> keepRelativePaths,
        bool recurse,
        LocalPathPolicy policy)
    {
        // Path.GetFullPath preserves a trailing separator, but the containment guard (see
        // IsWithinRoot) needs root WITHOUT one so a child is root + separator + name. Trim it —
        // TrimEndingDirectorySeparator leaves volume-root paths ("C:\", "/") intact.
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destRoot));
        var keep = new HashSet<string>(keepRelativePaths, policy.PathComparer);
        var paths = new List<string>();
        var counters = new Counters();

        Walk(root, root, relativePrefix: "", keep, recurse, forceDelete: false, paths, counters, policy);

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
        Counters counters,
        LocalPathPolicy policy)
    {
        foreach (string childPath in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            string childName = Path.GetFileName(childPath);
            string relativeName = relativePrefix.Length == 0
                ? childName
                : $"{relativePrefix}{policy.DirectorySeparator}{childName}";

            FileAttributes attributes = File.GetAttributes(childPath);
            bool isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
            bool isDirectory = !isReparsePoint && (attributes & FileAttributes.Directory) != 0;
            bool isKept = !forceDelete && keepRelativePaths.Contains(relativeName);

            if (isKept)
            {
                // Only descend into a kept directory's children when recursing; a kept file or
                // reparse point needs no further action either way.
                if (isDirectory && recurse)
                    Walk(root, childPath, relativeName, keepRelativePaths, recurse, forceDelete: false, paths, counters, policy);
                continue;
            }

            // Extraneous: delete post-order (contents before the directory itself) so a directory
            // is empty by the time it is removed. Never recurse through a reparse point — an
            // extraneous junction/dir-symlink is deleted as a single symlink entry.
            if (isDirectory)
            {
                Walk(root, childPath, relativeName, keepRelativePaths, recurse, forceDelete: true, paths, counters, policy);
                DeleteEntry(root, childPath, attributes, isDirectory: true, policy);
                paths.Add(childPath);
                counters.Dirs++;
            }
            else if (isReparsePoint)
            {
                DeleteEntry(root, childPath, attributes, isDirectory: (attributes & FileAttributes.Directory) != 0, policy);
                paths.Add(childPath);
                counters.Symlinks++;
            }
            else
            {
                DeleteEntry(root, childPath, attributes, isDirectory: false, policy);
                paths.Add(childPath);
                counters.Files++;
            }
        }
    }

    private static void DeleteEntry(
        string root,
        string path,
        FileAttributes attributes,
        bool isDirectory,
        LocalPathPolicy policy)
    {
        // Containment defense-in-depth: enumeration already guarantees this, but a mapper/walk bug
        // must never be the last line of defense (WindowsPathMapper's doc note applies here too).
        string fullPath = Path.GetFullPath(path);
        if (!IsWithinRoot(root, fullPath, policy))
            throw new InvalidOperationException($"Refusing to delete '{fullPath}': outside destination root '{root}'");

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

    /// <summary>
    /// True when <paramref name="fullPath"/> is the root itself or a genuine descendant of it.
    /// <paramref name="root"/> is a <see cref="Path.GetFullPath(string)"/> result with any trailing
    /// separator already stripped by <see cref="Prune(string, IReadOnlySet{string}, bool, LocalPathPolicy)"/>,
    /// so a real child is <c>root + separator + name</c>: the char at <c>root.Length</c> must be the
    /// separator. That second test is what rejects a same-prefix sibling — root <c>…\dest</c> vs
    /// <c>…\destEVIL\x</c> — which shares the prefix but is a different directory.
    /// </summary>
    internal static bool IsWithinRoot(string root, string fullPath, LocalPathPolicy policy)
        => fullPath.StartsWith(root, policy.PathComparison)
            && (fullPath.Length == root.Length || fullPath[root.Length] == Path.DirectorySeparatorChar);
}
