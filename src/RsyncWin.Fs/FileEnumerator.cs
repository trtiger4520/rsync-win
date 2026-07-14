using RsyncWin.Protocol.FileList;

namespace RsyncWin.Fs;

/// <summary>
/// One push-side source-tree entry: the wire-shaped <see cref="FileEntry"/> (name, mode/kind,
/// size, mtime) paired with the local absolute path a later sender-loop task needs to actually
/// read the file's content. Pure data — no protocol encoding lives here.
/// </summary>
public sealed record EnumeratedEntry(FileEntry Wire, string AbsolutePath);

/// <summary>
/// Walks a local source directory recursively and returns entries pre-sorted into rsync's flist
/// order (<see cref="FileNameComparer"/>) — the push-side mirror of <c>FileListReader</c>. Pure
/// enumeration: turns filesystem reality into <see cref="FileEntry"/> records; wire encoding is
/// FileListWriter's job (a separate task).
/// </summary>
/// <remarks>
/// Mode synthesis: Windows/NTFS has no POSIX permission bits, so <see cref="FileEntry.Mode"/> is
/// synthesized from the read-only attribute — 0755/0644 (writable) or 0555/0444 (read-only) for
/// dirs/files respectively. This is a minimal, deterministic policy, not a real permission model.
/// <para>
/// Sort-order ambiguity note (flist-spec.md §7 rule 6 — a dir and a non-dir with the same joined
/// name): cannot arise from walking a single real directory tree, since a filesystem cannot hold
/// both a file "a" and a directory "a" as siblings. The rule only matters when multiple source
/// roots get merged into one flist (e.g. multiple CLI source args). TODO: revisit — and capture a
/// discriminating vector to confirm behavior — if/when multi-source push is implemented; for now
/// this enumerator only ever walks one root, so the case is unreachable, not decided.
/// </para>
/// </remarks>
public static class FileEnumerator
{
    private const int DirectoryWritablePermissions = 0x1ED; // 0755
    private const int DirectoryReadOnlyPermissions = 0x16D;  // 0555
    private const int FileWritablePermissions = 0x1A4;       // 0644
    private const int FileReadOnlyPermissions = 0x124;       // 0444

    /// <summary>
    /// Enumerates <paramref name="rootPath"/> and its full subtree, sorted into flist order. The
    /// root itself is returned first as the literal wire name "." (mirroring rsync's transfer-root
    /// convention, flist-spec.md §3), followed by every descendant with a '/'-separated relative
    /// name. Empty directories are included (they simply have no descendants). Symlinks and any
    /// other reparse points are included with <see cref="FileEntry.Symlink"/> kind bits set — never
    /// recursed into — so the caller can warn-and-skip them, mirroring the pull-side policy (see
    /// <c>PullSession.WritePhase0RequestsAsync</c>: "symlinks/devices land in a later phase").
    /// </summary>
    public static IReadOnlyList<EnumeratedEntry> Enumerate(string rootPath)
    {
        // .NET resolves long paths natively on modern Windows (no MAX_PATH manifest, no manual
        // "\\?\" prefixing) — the same assumption RsyncWin.Engine's PullSession already relies on
        // via plain Path.GetFullPath calls; there is no existing "\\?\" handling in this project.
        string root = Path.GetFullPath(rootPath);

        var entries = new List<EnumeratedEntry> { BuildEntry(root, ".") };
        Walk(root, relativePrefix: "", entries);

        entries.Sort((a, b) => FileNameComparer.Instance.Compare(a.Wire, b.Wire));
        return entries;
    }

    private static void Walk(string directoryPath, string relativePrefix, List<EnumeratedEntry> entries)
    {
        foreach (string childPath in Directory.EnumerateFileSystemEntries(directoryPath))
        {
            string childName = Path.GetFileName(childPath);
            string relativeName = relativePrefix.Length == 0 ? childName : $"{relativePrefix}/{childName}";
            EnumeratedEntry entry = BuildEntry(childPath, relativeName);
            entries.Add(entry);

            // Reparse points (symlinks, junctions, mount points) report FileAttributes.Directory
            // too when they target a directory — BuildEntry classifies them as Symlink kind first,
            // so IsDirectory is false here and we never follow them (avoids symlink-loop recursion).
            if (entry.Wire.IsDirectory)
                Walk(childPath, relativeName, entries);
        }
    }

    private static EnumeratedEntry BuildEntry(string path, string relativeName)
    {
        FileAttributes attributes = File.GetAttributes(path);
        bool isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
        bool isDirectory = !isReparsePoint && (attributes & FileAttributes.Directory) != 0;
        bool readOnly = (attributes & FileAttributes.ReadOnly) != 0;

        DateTime modifiedUtc;
        long size;
        if (isDirectory)
        {
            var info = new DirectoryInfo(path);
            modifiedUtc = info.LastWriteTimeUtc;
            size = 0; // NTFS has no meaningful directory byte size; never compared for dirs anyway
        }
        else
        {
            var info = new FileInfo(path);
            modifiedUtc = info.LastWriteTimeUtc;
            size = isReparsePoint ? 0 : info.Length; // symlinks are skipped downstream, size unused
        }

        int typeBits = isReparsePoint ? FileEntry.Symlink : isDirectory ? FileEntry.Directory : FileEntry.RegularFile;
        int permissions = isDirectory
            ? (readOnly ? DirectoryReadOnlyPermissions : DirectoryWritablePermissions)
            : (readOnly ? FileReadOnlyPermissions : FileWritablePermissions);

        var mtimeOffset = new DateTimeOffset(modifiedUtc, TimeSpan.Zero);
        long ticksIntoSecond = modifiedUtc.Ticks % TimeSpan.TicksPerSecond;

        var wire = new FileEntry
        {
            NameBytes = System.Text.Encoding.UTF8.GetBytes(relativeName),
            Mode = typeBits | permissions,
            Size = size,
            ModifiedUnixSeconds = mtimeOffset.ToUnixTimeSeconds(),
            ModifiedNanoseconds = (int)(ticksIntoSecond * 100), // 100ns ticks -> ns; policy decided later
        };
        return new EnumeratedEntry(wire, path);
    }
}
