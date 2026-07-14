using System.Text;
using RsyncWin.Protocol.FileList;

namespace RsyncWin.Protocol.Tests.FileList;

/// <summary>
/// Both discriminating rules were pinned against a live rsync 3.4.3 <c>--list-only</c> run
/// (alpine:3.21) whose output order is reproduced here verbatim. These orderings CANNOT be
/// derived from plain byte comparison — each test fails under the naive rule.
/// </summary>
public class FileNameComparerTests
{
    private static FileEntry Make(string name, bool dir = false) => new()
    {
        NameBytes = Encoding.UTF8.GetBytes(name),
        Mode = dir ? FileEntry.Directory | 0x1ED : FileEntry.RegularFile | 0x1A4,
        Size = 0,
        ModifiedUnixSeconds = 0,
    };

    private static string[] Sorted(params FileEntry[] entries)
    {
        var list = entries.ToList();
        list.Sort(FileNameComparer.Instance);
        return [.. list.Select(e => e.Name)];
    }

    [Fact]
    public void BandRule_AllFilesBeforeAllDirs_WithinAParent()
    {
        // Measured: aaa/adir/ccc are dirs, yet bbb.txt and a-dash sort before every one of them.
        string[] sorted = Sorted(
            Make("aaa", dir: true), Make("aaa/inner.txt"), Make("bbb.txt"), Make("ccc", dir: true),
            Make("a-dash"), Make("adir", dir: true), Make("adir/deep.txt"), Make(".", dir: true));

        Assert.Equal(
            [".", "a-dash", "bbb.txt", "aaa", "aaa/inner.txt", "adir", "adir/deep.txt", "ccc"],
            sorted);
    }

    [Fact]
    public void TrailingSlashRule_FooBarSubtree_BeforeFoo()
    {
        // Measured: dirs compare as if suffixed with '/', so '-' (0x2D) < '/' (0x2F) puts foo-bar
        // and its whole subtree before foo. Naive strcmp says foo < foo-bar.
        string[] sorted = Sorted(
            Make("foo", dir: true), Make("foo/f.txt"), Make("foo-bar", dir: true),
            Make("foo-bar/g.txt"), Make("qux"), Make("qux-1"), Make(".", dir: true));

        Assert.Equal(
            [".", "qux", "qux-1", "foo-bar", "foo-bar/g.txt", "foo", "foo/f.txt"],
            sorted);
    }

    [Fact]
    public void OrdinalBytes_NoCaseFolding()
    {
        // 'B' (0x42) < 'a' (0x61): a culture-aware or case-insensitive compare breaks this.
        Assert.Equal(["Zebra.txt", "apple.txt"], Sorted(Make("apple.txt"), Make("Zebra.txt")));
    }

    [Fact]
    public void HighBitBytes_CompareUnsigned()
    {
        // 0xE4 (the UTF-8 lead byte of '中') must sort AFTER ASCII 'z' (0x7A). A signed-byte
        // comparison flips this — and every fixture tree's deciding bytes are ASCII, so only
        // this test pins the unsigned invariant, in both the file band and the dir band.
        Assert.Equal(["z.txt", "中文.txt"], Sorted(Make("中文.txt"), Make("z.txt")));
        Assert.Equal(["z", "中"], Sorted(Make("中", dir: true), Make("z", dir: true)));
    }

    [Fact]
    public void DirSortsImmediatelyBeforeItsOwnContents()
    {
        string[] sorted = Sorted(
            Make("sub/deep/x"), Make("sub", dir: true), Make("sub/deep", dir: true), Make("sub/a.txt"));

        Assert.Equal(["sub", "sub/a.txt", "sub/deep", "sub/deep/x"], sorted);
    }
}
