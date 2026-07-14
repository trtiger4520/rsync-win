using RsyncWin.Fs;

namespace RsyncWin.Fs.Tests;

/// <summary>
/// Hermetic tests for <see cref="FileEnumerator"/>: the push-side source-tree walk. Every ordering
/// assertion is checked against a hand-computed UTF-8-byte sort, never against .NET's default
/// (UTF-16, culture-aware) string ordering — that divergence is exactly what would silently break
/// the wire's positional ndx if this walker ever stopped reusing
/// <see cref="RsyncWin.Protocol.FileList.FileNameComparer"/>.
/// </summary>
public class FileEnumeratorTests
{
    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rsyncwin-flistwalk-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void RootEntry_IsFirstAndNamedDot()
    {
        string root = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "hi");

            IReadOnlyList<EnumeratedEntry> entries = FileEnumerator.Enumerate(root);

            Assert.Equal(".", entries[0].Wire.Name);
            Assert.True(entries[0].Wire.IsDirectory);
            Assert.Equal(root, entries[0].AbsolutePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NestedTree_MatchesHandComputedUtf8ByteSortOrder_IncludingNonAsciiAndAstralNames()
    {
        // Names chosen so UTF-16 ordinal compare and UTF-8 byte compare would disagree:
        // 'z' (0x7A) < '中' (UTF-8 E4 B8 AD, leading byte 0xE4) < astral U+1F600 (UTF-8 F0 9F 98 80,
        // leading byte 0xF0) in BOTH encodings for these three, but a UTF-16 string compare of the
        // astral char (surrogate pair D83D DE00) against '中' (single unit 4E2D) would put the
        // astral char BEFORE '中' (0xD83D < 0x4E2D as UTF-16 code units) — the opposite of the
        // correct UTF-8 byte order. This test pins the UTF-8-byte behavior.
        string root = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, "z.txt"), "z");
            File.WriteAllText(Path.Combine(root, "中.txt"), "zh");
            File.WriteAllText(Path.Combine(root, "\U0001F600.txt"), "emoji");
            Directory.CreateDirectory(Path.Combine(root, "empty_dir"));
            Directory.CreateDirectory(Path.Combine(root, "subdir"));
            File.WriteAllText(Path.Combine(root, "subdir", "nested.txt"), "nested");

            IReadOnlyList<EnumeratedEntry> entries = FileEnumerator.Enumerate(root);
            List<string> names = entries.Select(e => e.Wire.Name).ToList();

            // Hand-computed expected order (flist-spec.md §7): "." first; then the three
            // top-level non-dirs in raw UTF-8 byte order — 'z' = 0x7A, '中' = E4 B8 AD (leading
            // 0xE4), U+1F600 = F0 9F 98 80 (leading 0xF0), so 0x7A < 0xE4 < 0xF0; then the two
            // top-level dirs in byte order ('e' 0x65 < 's' 0x73), each followed by its subtree.
            var expected = new List<string>
            {
                ".",
                "z.txt",
                "中.txt",
                "\U0001F600.txt",
                "empty_dir",
                "subdir",
                "subdir/nested.txt",
            };

            Assert.Equal(expected, names);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EmptyDirectory_IsIncludedWithNoDescendants()
    {
        string root = CreateTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "empty"));

            IReadOnlyList<EnumeratedEntry> entries = FileEnumerator.Enumerate(root);

            EnumeratedEntry emptyDir = Assert.Single(entries, e => e.Wire.Name == "empty");
            Assert.True(emptyDir.Wire.IsDirectory);
            Assert.DoesNotContain(entries, e => e.Wire.Name.StartsWith("empty/", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Symlink_IsMarkedWithSymlinkKindAndNotRecursedInto()
    {
        string root = CreateTempDir();
        try
        {
            string targetFile = Path.Combine(root, "target.txt");
            File.WriteAllText(targetFile, "hi");
            string linkPath = Path.Combine(root, "link.txt");

            try
            {
                File.CreateSymbolicLink(linkPath, targetFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Host lacks the privilege (or Developer Mode) required to create symlinks
                // unelevated — nothing to assert against, skip gracefully rather than fail CI.
                return;
            }

            IReadOnlyList<EnumeratedEntry> entries = FileEnumerator.Enumerate(root);

            EnumeratedEntry link = Assert.Single(entries, e => e.Wire.Name == "link.txt");
            Assert.True(link.Wire.IsSymlink);
            Assert.False(link.Wire.IsDirectory);
            Assert.False(link.Wire.IsRegularFile);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RepeatedRuns_ProduceIdenticalOrder()
    {
        string root = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, "b.txt"), "b");
            File.WriteAllText(Path.Combine(root, "a.txt"), "a");
            Directory.CreateDirectory(Path.Combine(root, "dir"));
            File.WriteAllText(Path.Combine(root, "dir", "c.txt"), "c");

            List<string> first = FileEnumerator.Enumerate(root).Select(e => e.Wire.Name).ToList();
            List<string> second = FileEnumerator.Enumerate(root).Select(e => e.Wire.Name).ToList();

            Assert.Equal(first, second);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
