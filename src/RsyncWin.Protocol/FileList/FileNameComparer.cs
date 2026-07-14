namespace RsyncWin.Protocol.FileList;

/// <summary>
/// rsync's <c>f_name_cmp</c> ordering (protocol ≥ 29). Both sides sort their file list with THIS
/// comparator after the exchange, and every subsequent <c>ndx</c> on the wire is a position in the
/// sorted array — an ordering bug here silently misaligns every transfer that follows.
/// </summary>
/// <remarks>
/// Rules, each pinned against a live rsync 3.4.3 <c>--list-only</c> run on a discriminating tree
/// (see <c>docs/wire-notes.md</c>):
/// <list type="number">
/// <item>the root entry <c>.</c> sorts first;</item>
/// <item>within a directory, ALL non-directories sort before ALL directories, regardless of
/// name bytes (measured: <c>bbb.txt</c> before dir <c>aaa</c>);</item>
/// <item>directory names compare as if suffixed with <c>/</c> (0x2F), so among dirs
/// <c>foo-bar</c> and its whole subtree sort BEFORE <c>foo</c> (measured);</item>
/// <item>a directory sorts immediately before its own contents;</item>
/// <item>everything else is plain unsigned byte order — no locale, no case folding.</item>
/// </list>
/// </remarks>
public sealed class FileNameComparer : IComparer<FileEntry>
{
    public static FileNameComparer Instance { get; } = new();

    public int Compare(FileEntry? x, FileEntry? y)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);

        bool xRoot = x.NameBytes is [(byte)'.'];
        bool yRoot = y.NameBytes is [(byte)'.'];
        if (xRoot || yRoot)
            return xRoot == yRoot ? 0 : xRoot ? -1 : 1;

        ReadOnlySpan<byte> a = x.NameBytes;
        ReadOnlySpan<byte> b = y.NameBytes;
        int i = 0, j = 0;
        while (true)
        {
            int aEnd = NextSlash(a, i);
            int bEnd = NextSlash(b, j);
            bool aLeaf = aEnd == a.Length;
            bool bLeaf = bEnd == b.Length;

            // The band rule: a leaf non-dir belongs to band 0, everything directory-ish
            // (a dir leaf, or a path continuing into a subdirectory) to band 1.
            bool aDirish = !aLeaf || x.IsDirectory;
            bool bDirish = !bLeaf || y.IsDirectory;
            if (aDirish != bDirish)
                return aDirish ? 1 : -1;

            if (!aDirish)
                // Two files in the same parent: plain byte order, prefix first.
                return a[i..aEnd].SequenceCompareTo(b[j..bEnd]);

            // Two directory-ish components: compare with the virtual trailing '/'.
            int aLen = aEnd - i, bLen = bEnd - j;
            for (int k = 0; k < aLen || k < bLen; k++)
            {
                byte ca = k < aLen ? a[i + k] : (byte)'/';
                byte cb = k < bLen ? b[j + k] : (byte)'/';
                if (ca != cb)
                    return ca - cb;
            }

            // Identical components: the directory itself precedes its contents.
            if (aLeaf || bLeaf)
                return aLeaf == bLeaf ? 0 : aLeaf ? -1 : 1;
            i = aEnd + 1;
            j = bEnd + 1;
        }
    }

    private static int NextSlash(ReadOnlySpan<byte> name, int start)
    {
        int relative = name[start..].IndexOf((byte)'/');
        return relative < 0 ? name.Length : start + relative;
    }
}
