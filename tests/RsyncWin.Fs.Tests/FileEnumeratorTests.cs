using RsyncWin.Fs;
using RsyncWin.Protocol.FileList;

namespace RsyncWin.Fs.Tests;

/// <summary>
/// Hermetic tests for <see cref="FileEnumerator"/>: the push-side source-tree walk. Every ordering
/// assertion is checked against a hand-computed UTF-8-byte sort, never against .NET's default
/// (UTF-16, culture-aware) string ordering — that divergence is exactly what would silently break
/// the wire's positional ndx if this walker ever stopped reusing
/// <see cref="RsyncWin.Protocol.FileList.FileNameComparer"/>.
/// </summary>
[Trait("Category", "WindowsFs")]
public class FileEnumeratorTests
{
    private static string CreateTempDir() => WindowsFsTestSupport.CreateTempDirectory("rsyncwin-flistwalk");

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
    public void SingleFileSource_ReturnsOneBasenameEntry_NoDotRoot()
    {
        // "rsync file host::mod/" is a single-FILE source: the flist is one entry named by the file's
        // basename with NO "." transfer-root (pinned byte-exact by ssh31-push-delta/redo). Contrast
        // RootEntry_IsFirstAndNamedDot above, which pins the directory-source "." root.
        string root = CreateTempDir();
        try
        {
            string filePath = Path.Combine(root, "clip.mp4");
            File.WriteAllText(filePath, "hello"); // 5 bytes

            IReadOnlyList<EnumeratedEntry> entries = FileEnumerator.Enumerate(filePath);

            EnumeratedEntry only = Assert.Single(entries);
            Assert.Equal("clip.mp4", only.Wire.Name); // basename, never "."
            Assert.False(only.Wire.IsDirectory);
            Assert.True(only.Wire.IsRegularFile);
            Assert.Equal(FileEntry.RegularFile | 0x1A4, only.Wire.Mode); // writable regular file, 0644
            Assert.Equal(5, only.Wire.Size);
            Assert.Equal(filePath, only.AbsolutePath);
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                throw Xunit.Sdk.SkipException.ForSkip($"File symbolic links are unavailable on this host: {ex.Message}");
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

    [Fact]
    public void ReadOnlyAttributes_SynthesizeTheExpectedWindowsModes()
    {
        string root = CreateTempDir();
        try
        {
            string writable = Path.Combine(root, "writable.txt");
            string readOnly = Path.Combine(root, "readonly.txt");
            string readOnlyDirectory = Path.Combine(root, "readonly-dir");
            File.WriteAllText(writable, "writable");
            File.WriteAllText(readOnly, "readonly");
            Directory.CreateDirectory(readOnlyDirectory);
            File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);
            File.SetAttributes(readOnlyDirectory, File.GetAttributes(readOnlyDirectory) | FileAttributes.ReadOnly);

            IReadOnlyList<EnumeratedEntry> entries = FileEnumerator.Enumerate(root);

            Assert.Equal(FileEntry.RegularFile | 0x1A4, entries.Single(e => e.Wire.Name == "writable.txt").Wire.Mode);
            Assert.Equal(FileEntry.RegularFile | 0x124, entries.Single(e => e.Wire.Name == "readonly.txt").Wire.Mode);
            Assert.Equal(FileEntry.Directory | 0x16D, entries.Single(e => e.Wire.Name == "readonly-dir").Wire.Mode);
        }
        finally
        {
            string readOnly = Path.Combine(root, "readonly.txt");
            string readOnlyDirectory = Path.Combine(root, "readonly-dir");
            if (File.Exists(readOnly))
                File.SetAttributes(readOnly, FileAttributes.Normal);
            if (Directory.Exists(readOnlyDirectory))
                File.SetAttributes(readOnlyDirectory, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeepTreeBeyondMaxPath_IsEnumeratedAndKeepsAbsolutePaths()
    {
        string root = CreateTempDir();
        try
        {
            string current = root;
            for (int i = 0; i < 10; i++)
            {
                current = Path.Combine(current, $"segment-{i:D2}-{new string('x', 24)}");
                Directory.CreateDirectory(current);
            }

            string filePath = Path.Combine(current, "payload.txt");
            File.WriteAllText(filePath, "long path");
            Assert.True(filePath.Length > 260, $"test path must exceed MAX_PATH, got {filePath.Length}");

            IReadOnlyList<EnumeratedEntry> entries = FileEnumerator.Enumerate(root);
            EnumeratedEntry payload = Assert.Single(entries, e => e.Wire.Name.EndsWith("/payload.txt", StringComparison.Ordinal));

            Assert.Equal(Path.GetFullPath(filePath), payload.AbsolutePath);
            Assert.Equal(10, payload.Wire.Name.Count(c => c == '/'));
            Assert.Equal("long path", File.ReadAllText(payload.AbsolutePath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DirectorySymlink_IsMarkedAndItsTargetIsNotEnumerated()
    {
        string root = CreateTempDir();
        string target = CreateTempDir();
        try
        {
            string targetFile = Path.Combine(target, "outside.txt");
            File.WriteAllText(targetFile, "outside");
            string link = Path.Combine(root, "linked-dir");

            WindowsFsTestSupport.CreateDirectorySymlinkOrSkip(link, target);

            IReadOnlyList<EnumeratedEntry> entries = FileEnumerator.Enumerate(root);
            EnumeratedEntry linked = Assert.Single(entries, e => e.Wire.Name == "linked-dir");

            Assert.True(linked.Wire.IsSymlink);
            Assert.DoesNotContain(entries, e => e.Wire.Name == "linked-dir/outside.txt");
            Assert.Equal("outside", File.ReadAllText(targetFile));
        }
        finally
        {
            WindowsFsTestSupport.DeleteDirectory(root);
            WindowsFsTestSupport.DeleteDirectory(target);
        }
    }

    [Fact]
    public void DirectoryJunction_IsMarkedAndItsTargetIsNotEnumerated()
    {
        string root = CreateTempDir();
        string target = CreateTempDir();
        try
        {
            string targetFile = Path.Combine(target, "outside.txt");
            File.WriteAllText(targetFile, "outside");
            string link = Path.Combine(root, "junction-dir");

            WindowsFsTestSupport.CreateDirectoryJunctionOrSkip(link, target);

            IReadOnlyList<EnumeratedEntry> entries = FileEnumerator.Enumerate(root);
            EnumeratedEntry junction = Assert.Single(entries, e => e.Wire.Name == "junction-dir");

            Assert.True(junction.Wire.IsSymlink);
            Assert.DoesNotContain(entries, e => e.Wire.Name == "junction-dir/outside.txt");
            Assert.Equal("outside", File.ReadAllText(targetFile));
        }
        finally
        {
            WindowsFsTestSupport.DeleteDirectory(root);
            WindowsFsTestSupport.DeleteDirectory(target);
        }
    }
}
