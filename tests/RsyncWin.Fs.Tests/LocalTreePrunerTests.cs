using RsyncWin.Fs;

namespace RsyncWin.Fs.Tests;

/// <summary>Hermetic tests for <see cref="LocalTreePruner"/>: the pull-side <c>--delete</c> walk.</summary>
[Trait("Category", "WindowsFs")]
public class LocalTreePrunerTests
{
    private static string CreateTempDir() => WindowsFsTestSupport.CreateTempDirectory("rsyncwin-prune");

    [Fact]
    public void ExtraneousFileAndDirectory_AreDeleted_KeptFileSurvives()
    {
        string root = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, "keep.txt"), "keep");
            File.WriteAllText(Path.Combine(root, "extra.txt"), "extra");
            Directory.CreateDirectory(Path.Combine(root, "extraDir"));
            File.WriteAllText(Path.Combine(root, "extraDir", "inner.txt"), "inner");

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "keep.txt" };
            PruneResult result = LocalTreePruner.Prune(root, keep, recurse: true);

            Assert.True(File.Exists(Path.Combine(root, "keep.txt")));
            Assert.False(File.Exists(Path.Combine(root, "extra.txt")));
            Assert.False(Directory.Exists(Path.Combine(root, "extraDir")));

            // extra.txt + extraDir/inner.txt = 2 regular files; extraDir itself = 1 directory.
            Assert.Equal(2, result.DeletedRegularFiles);
            Assert.Equal(1, result.DeletedDirectories);
            Assert.Equal(0, result.DeletedSymlinks);
            Assert.Equal(0, result.DeletedDevices);
            Assert.Equal(0, result.DeletedSpecials);
            Assert.Equal(3, result.DeletedPaths.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void KeepSetMatch_IsCaseInsensitive()
    {
        string root = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, "foo.txt"), "hi");

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Foo.txt" };
            PruneResult result = LocalTreePruner.Prune(root, keep, recurse: true);

            Assert.True(File.Exists(Path.Combine(root, "foo.txt")));
            Assert.Empty(result.DeletedPaths);
            Assert.Equal(0, result.DeletedRegularFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExplicitUnixPolicy_UsesCaseSensitiveKeepComparison()
    {
        string root = CreateTempDir();
        try
        {
            string lowerCasePath = Path.Combine(root, "foo.txt");
            File.WriteAllText(lowerCasePath, "hi");

            IReadOnlySet<string> keep = new HashSet<string> { "Foo.txt" };
            PruneResult result = LocalTreePruner.Prune(root, keep, recurse: true, LocalPathPolicy.Unix);

            Assert.False(File.Exists(lowerCasePath));
            Assert.Equal(1, result.DeletedRegularFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExplicitUnixPolicy_UsesSlashForNestedKeepPaths()
    {
        string root = CreateTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "dir"));
            string nestedPath = Path.Combine(root, "dir", "nested.txt");
            File.WriteAllText(nestedPath, "hi");

            IReadOnlySet<string> keep = new HashSet<string> { "dir", "dir/nested.txt" };
            PruneResult result = LocalTreePruner.Prune(root, keep, recurse: true, LocalPathPolicy.Unix);

            Assert.True(File.Exists(nestedPath));
            Assert.Empty(result.DeletedPaths);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadOnlyExtraneousFile_IsDeleted()
    {
        string root = CreateTempDir();
        try
        {
            string path = Path.Combine(root, "readonly.txt");
            File.WriteAllText(path, "ro");
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            PruneResult result = LocalTreePruner.Prune(root, keep, recurse: true);

            Assert.False(File.Exists(path));
            Assert.Equal(1, result.DeletedRegularFiles);
        }
        finally
        {
            // Belt-and-suspenders in case the assertion above ever fails mid-run.
            if (File.Exists(Path.Combine(root, "readonly.txt")))
                File.SetAttributes(Path.Combine(root, "readonly.txt"), FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DeleteOrder_IsPostOrder_InnerFileBeforeParentDirectory()
    {
        string root = CreateTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "extraDir"));
            File.WriteAllText(Path.Combine(root, "extraDir", "inner.txt"), "inner");

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            PruneResult result = LocalTreePruner.Prune(root, keep, recurse: true);

            string innerPath = Path.Combine(root, "extraDir", "inner.txt");
            string dirPath = Path.Combine(root, "extraDir");
            int innerIndex = result.DeletedPaths.ToList().IndexOf(innerPath);
            int dirIndex = result.DeletedPaths.ToList().IndexOf(dirPath);

            Assert.True(innerIndex >= 0);
            Assert.True(dirIndex >= 0);
            Assert.True(innerIndex < dirIndex);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RecurseFalse_DeletesTopLevelExtraneousFile_ButDoesNotPruneInsideKeptDirectory()
    {
        string root = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, "extra.txt"), "extra");
            Directory.CreateDirectory(Path.Combine(root, "keptDir"));
            File.WriteAllText(Path.Combine(root, "keptDir", "shouldSurvive.txt"), "survive");

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "keptDir" };
            PruneResult result = LocalTreePruner.Prune(root, keep, recurse: false);

            Assert.False(File.Exists(Path.Combine(root, "extra.txt")));
            Assert.True(Directory.Exists(Path.Combine(root, "keptDir")));
            Assert.True(File.Exists(Path.Combine(root, "keptDir", "shouldSurvive.txt")));
            Assert.Equal(1, result.DeletedRegularFiles);
            Assert.Equal(0, result.DeletedDirectories);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RecurseFalse_ExtraneousTopLevelDirectory_IsRemovedWholesale()
    {
        string root = CreateTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "extraDir"));
            File.WriteAllText(Path.Combine(root, "extraDir", "inner.txt"), "inner");

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            PruneResult result = LocalTreePruner.Prune(root, keep, recurse: false);

            Assert.False(Directory.Exists(Path.Combine(root, "extraDir")));
            Assert.Equal(1, result.DeletedRegularFiles);
            Assert.Equal(1, result.DeletedDirectories);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NothingExtraneous_ProducesEmptyResultWithAllCountsZero()
    {
        string root = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(root, "keep.txt"), "keep");
            Directory.CreateDirectory(Path.Combine(root, "keptDir"));
            File.WriteAllText(Path.Combine(root, "keptDir", "nested.txt"), "nested");

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "keep.txt",
                "keptDir",
                "keptDir\\nested.txt",
            };
            PruneResult result = LocalTreePruner.Prune(root, keep, recurse: true);

            Assert.Empty(result.DeletedPaths);
            Assert.Equal(0, result.DeletedRegularFiles);
            Assert.Equal(0, result.DeletedDirectories);
            Assert.Equal(0, result.DeletedSymlinks);
            Assert.Equal(0, result.DeletedDevices);
            Assert.Equal(0, result.DeletedSpecials);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DirectorySymlink_IsNotWalkedInto_TargetContentsSurvive()
    {
        string root = CreateTempDir();
        string targetDir = CreateTempDir(); // separate root, standing in for "outside the tree"
        try
        {
            string targetFile = Path.Combine(targetDir, "outside.txt");
            File.WriteAllText(targetFile, "must survive");

            string linkPath = Path.Combine(root, "link");
            try
            {
                Directory.CreateSymbolicLink(linkPath, targetDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                throw Xunit.Sdk.SkipException.ForSkip($"Directory symbolic links are unavailable on this host: {ex.Message}");
            }

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            PruneResult result = LocalTreePruner.Prune(root, keep, recurse: true);

            // The link itself is extraneous and gets removed as a single symlink entry...
            Assert.False(Directory.Exists(linkPath) && new DirectoryInfo(linkPath).LinkTarget is not null);
            Assert.Equal(1, result.DeletedSymlinks);
            Assert.Equal(0, result.DeletedRegularFiles);
            // ...but the target directory's own contents were never touched.
            Assert.True(File.Exists(targetFile));
        }
        finally
        {
            WindowsFsTestSupport.DeleteDirectory(root);
            WindowsFsTestSupport.DeleteDirectory(targetDir);
        }
    }

    [Fact]
    public void DirectoryJunction_IsNotWalkedInto_TargetContentsSurvive()
    {
        string root = CreateTempDir();
        string targetDir = CreateTempDir();
        try
        {
            string targetFile = Path.Combine(targetDir, "outside.txt");
            File.WriteAllText(targetFile, "must survive");

            string linkPath = Path.Combine(root, "junction");
            WindowsFsTestSupport.CreateDirectoryJunctionOrSkip(linkPath, targetDir);

            PruneResult result = LocalTreePruner.Prune(
                root, new HashSet<string>(StringComparer.OrdinalIgnoreCase), recurse: true);

            Assert.False(Directory.Exists(linkPath));
            Assert.Equal(1, result.DeletedSymlinks);
            Assert.Equal(0, result.DeletedRegularFiles);
            Assert.True(File.Exists(targetFile));
            Assert.Equal("must survive", File.ReadAllText(targetFile));
        }
        finally
        {
            WindowsFsTestSupport.DeleteDirectory(root);
            WindowsFsTestSupport.DeleteDirectory(targetDir);
        }
    }

    [Fact]
    public void ReadOnlyExtraneousDirectory_IsDeletedWithItsReadOnlyChildren()
    {
        string root = CreateTempDir();
        try
        {
            string directory = Path.Combine(root, "readonly-dir");
            string file = Path.Combine(directory, "readonly.txt");
            Directory.CreateDirectory(directory);
            File.WriteAllText(file, "remove me");
            File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.ReadOnly);
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);

            PruneResult result = LocalTreePruner.Prune(
                root, new HashSet<string>(StringComparer.OrdinalIgnoreCase), recurse: true);

            Assert.False(Directory.Exists(directory));
            Assert.Equal(1, result.DeletedDirectories);
            Assert.Equal(1, result.DeletedRegularFiles);
        }
        finally
        {
            string directory = Path.Combine(root, "readonly-dir");
            string file = Path.Combine(directory, "readonly.txt");
            if (File.Exists(file))
                File.SetAttributes(file, FileAttributes.Normal);
            if (Directory.Exists(directory))
                File.SetAttributes(directory, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ExtraneousFileSymlink_IsDeletedWithoutTouchingItsTarget()
    {
        string root = CreateTempDir();
        string target = CreateTempDir();
        try
        {
            string targetFile = Path.Combine(target, "outside.txt");
            string link = Path.Combine(root, "linked-file");
            File.WriteAllText(targetFile, "must survive");
            WindowsFsTestSupport.CreateFileSymlinkOrSkip(link, targetFile);

            PruneResult result = LocalTreePruner.Prune(
                root, new HashSet<string>(StringComparer.OrdinalIgnoreCase), recurse: true);

            Assert.False(File.Exists(link));
            Assert.Equal(1, result.DeletedSymlinks);
            Assert.Equal("must survive", File.ReadAllText(targetFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void KeptDirectorySymlink_IsNotWalkedOrDeleted()
    {
        string root = CreateTempDir();
        string target = CreateTempDir();
        try
        {
            string targetFile = Path.Combine(target, "outside.txt");
            string link = Path.Combine(root, "linked-dir");
            File.WriteAllText(targetFile, "must survive");
            WindowsFsTestSupport.CreateDirectorySymlinkOrSkip(link, target);

            PruneResult result = LocalTreePruner.Prune(
                root,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "linked-dir" },
                recurse: true);

            Assert.Empty(result.DeletedPaths);
            Assert.True(Directory.Exists(link));
            Assert.Equal("must survive", File.ReadAllText(targetFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void DeepLongPath_IsPrunedWithoutLeavingTheTreeBehind()
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

            string file = Path.Combine(current, "payload.txt");
            File.WriteAllText(file, "remove me");
            Assert.True(file.Length > 260, $"test path must exceed MAX_PATH, got {file.Length}");

            PruneResult result = LocalTreePruner.Prune(
                root, new HashSet<string>(StringComparer.OrdinalIgnoreCase), recurse: true);

            Assert.False(Directory.Exists(root) && File.Exists(file));
            Assert.Equal(1, result.DeletedRegularFiles);
            Assert.Equal(10, result.DeletedDirectories);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
