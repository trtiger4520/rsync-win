using RsyncWin.Fs;

namespace RsyncWin.Fs.Tests;

/// <summary>Hermetic tests for <see cref="LocalTreePruner"/>: the pull-side <c>--delete</c> walk.</summary>
public class LocalTreePrunerTests
{
    private static string CreateTempDir()
    {
        string path = Path.Combine(Path.GetTempPath(), $"rsyncwin-prune-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

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
    public void DirectoryJunction_IsNotWalkedInto_TargetContentsSurvive()
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Host lacks the privilege (or Developer Mode) required to create directory
                // symlinks unelevated. TODO: exercise junctions/dir-symlinks at the interop tier
                // where an elevated or dev-mode host is guaranteed.
                return;
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
            Directory.Delete(root, recursive: true);
            Directory.Delete(targetDir, recursive: true);
        }
    }
}
