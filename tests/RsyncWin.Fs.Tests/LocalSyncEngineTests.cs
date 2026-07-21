using RsyncWin.Fs;

namespace RsyncWin.Fs.Tests;

/// <summary>Hermetic temp-dir tests for <see cref="LocalSyncEngine"/>: trailing-slash semantics,
/// the skip fast path, --checksum, replace hazards, --delete, and the self-copy guards.</summary>
[Trait("Category", "WindowsFs")]
public class LocalSyncEngineTests
{
    private static readonly LocalSyncOptions Recursive = new(Recurse: true, Checksum: false, Delete: false);

    private static string CreateTempDir() => WindowsFsTestSupport.CreateTempDirectory("rsyncwin-local");

    private static void Cleanup(string path) => WindowsFsTestSupport.DeleteDirectory(path);

    [Fact]
    public void NoTrailingSlash_CreatesSourceLeafDirectoryUnderDest()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(src, "a.txt"), "hello");

            LocalSyncResult result = LocalSyncEngine.Run(src, dst, Recursive);

            Assert.Equal("hello", File.ReadAllText(Path.Combine(dst, "src", "a.txt")));
            Assert.Equal(1, result.TransferredFiles);
            Assert.Equal(5, result.TransferredBytes);
            Assert.Empty(result.FailedFiles);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData("\\")]
    [InlineData("/")]
    public void TrailingSlash_CopiesContentsDirectlyIntoDest(string separator)
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(src, "sub", "a.txt"), "hi");

            LocalSyncResult result = LocalSyncEngine.Run(src + separator, dst, Recursive);

            Assert.Equal("hi", File.ReadAllText(Path.Combine(dst, "sub", "a.txt")));
            Assert.False(Directory.Exists(Path.Combine(dst, "src")));
            Assert.Equal(1, result.TransferredFiles);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void MissingDestination_IsCreated()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "a.txt"), "x");

            LocalSyncResult result = LocalSyncEngine.Run(src, Path.Combine(root, "made", "dst"), Recursive);

            Assert.Equal("x", File.ReadAllText(Path.Combine(root, "made", "dst", "src", "a.txt")));
            Assert.Equal(1, result.TransferredFiles);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Rerun_TransfersNothing()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "a.txt"), "one");
            File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "two");

            LocalSyncResult first = LocalSyncEngine.Run(src + "\\", dst, Recursive);
            LocalSyncResult second = LocalSyncEngine.Run(src + "\\", dst, Recursive);

            Assert.Equal(2, first.TransferredFiles);
            Assert.Equal(0, second.TransferredFiles);
            Assert.Equal(0, second.TransferredBytes);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FileAndDirectoryMtimes_ArePreserved()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            File.WriteAllText(Path.Combine(src, "sub", "a.txt"), "x");
            var fileMtime = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc).AddTicks(1_234_500);
            var dirMtime = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(src, "sub", "a.txt"), fileMtime);
            Directory.SetLastWriteTimeUtc(Path.Combine(src, "sub"), dirMtime);

            LocalSyncEngine.Run(src + "\\", dst, Recursive);

            Assert.Equal(fileMtime, File.GetLastWriteTimeUtc(Path.Combine(dst, "sub", "a.txt")));
            Assert.Equal(dirMtime, Directory.GetLastWriteTimeUtc(Path.Combine(dst, "sub")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FastPath_SameSizeAndMtimeDifferentContent_IsSkipped()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(src, "a.txt"), "AAAA");
            File.WriteAllText(Path.Combine(dst, "a.txt"), "BBBB");
            var mtime = new DateTime(2022, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(src, "a.txt"), mtime);
            File.SetLastWriteTimeUtc(Path.Combine(dst, "a.txt"), mtime);

            LocalSyncResult result = LocalSyncEngine.Run(src + "\\", dst, Recursive);

            // Documents the quiet-check limitation --checksum exists to close.
            Assert.Equal(0, result.TransferredFiles);
            Assert.Equal("BBBB", File.ReadAllText(Path.Combine(dst, "a.txt")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Checksum_SameSizeAndMtimeDifferentContent_IsRecopied()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(src, "a.txt"), "AAAA");
            File.WriteAllText(Path.Combine(dst, "a.txt"), "BBBB");
            var mtime = new DateTime(2022, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(src, "a.txt"), mtime);
            File.SetLastWriteTimeUtc(Path.Combine(dst, "a.txt"), mtime);

            LocalSyncResult result = LocalSyncEngine.Run(src + "\\", dst, Recursive with { Checksum = true });

            Assert.Equal(1, result.TransferredFiles);
            Assert.Equal("AAAA", File.ReadAllText(Path.Combine(dst, "a.txt")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Checksum_SameContentDifferentMtime_TouchesMtimeWithoutTransferring()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(src, "a.txt"), "same");
            File.WriteAllText(Path.Combine(dst, "a.txt"), "same");
            var srcMtime = new DateTime(2022, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(Path.Combine(src, "a.txt"), srcMtime);
            File.SetLastWriteTimeUtc(Path.Combine(dst, "a.txt"), srcMtime.AddHours(1));

            LocalSyncResult result = LocalSyncEngine.Run(src + "\\", dst, Recursive with { Checksum = true });

            Assert.Equal(0, result.TransferredFiles);
            Assert.Equal(srcMtime, File.GetLastWriteTimeUtc(Path.Combine(dst, "a.txt")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void NonRecursive_DirectorySource_IsSkippedEntirely()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "a.txt"), "x");

            LocalSyncResult result = LocalSyncEngine.Run(src, dst, Recursive with { Recurse = false });

            Assert.Equal(["src"], result.SkippedDirectories);
            Assert.Equal(0, result.TransferredFiles);
            Assert.False(Directory.Exists(dst));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void SingleFileSource_CopiesWithoutRecurse()
    {
        string root = CreateTempDir();
        try
        {
            string srcFile = Path.Combine(root, "a.txt");
            File.WriteAllText(srcFile, "solo");

            LocalSyncResult toName = LocalSyncEngine.Run(srcFile, Path.Combine(root, "copy.txt"),
                new LocalSyncOptions(Recurse: false, Checksum: false, Delete: false));
            LocalSyncResult intoDir = LocalSyncEngine.Run(srcFile, Path.Combine(root, "dir") + "\\",
                new LocalSyncOptions(Recurse: false, Checksum: false, Delete: false));

            Assert.Equal("solo", File.ReadAllText(Path.Combine(root, "copy.txt")));
            Assert.Equal("solo", File.ReadAllText(Path.Combine(root, "dir", "a.txt")));
            Assert.Equal(1, toName.TransferredFiles);
            Assert.Equal(1, intoDir.TransferredFiles);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void EmptyDirectories_AreCreated()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(Path.Combine(src, "empty", "nested"));

            LocalSyncResult result = LocalSyncEngine.Run(src + "\\", dst, Recursive);

            Assert.True(Directory.Exists(Path.Combine(dst, "empty", "nested")));
            Assert.Equal(0, result.TransferredFiles);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void DirectorySymlinkInSource_IsSkippedAndNeverFollowed()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string dst = Path.Combine(root, "dst");
            string target = Path.Combine(root, "target");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "secret.txt"), "outside");
            WindowsFsTestSupport.CreateDirectoryJunctionOrSkip(Path.Combine(src, "link"), target);

            LocalSyncResult result = LocalSyncEngine.Run(src + "\\", dst, Recursive);

            Assert.Contains(result.SkippedNonRegular, s => s.Name == "link" && s.Reason == "symlink");
            Assert.False(Directory.Exists(Path.Combine(dst, "link")));
            Assert.Equal(0, result.TransferredFiles);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ReadOnlyDestinationFile_IsReplaced()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(src, "a.txt"), "new content");
            File.WriteAllText(Path.Combine(dst, "a.txt"), "old");
            File.SetAttributes(Path.Combine(dst, "a.txt"), FileAttributes.ReadOnly);

            LocalSyncResult result = LocalSyncEngine.Run(src + "\\", dst, Recursive);

            Assert.Equal("new content", File.ReadAllText(Path.Combine(dst, "a.txt")));
            Assert.Empty(result.FailedFiles);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void LockedDestinationFile_IsAPerFileFailureNotAThrow()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(src, "locked.txt"), "new content");
            File.WriteAllText(Path.Combine(src, "ok.txt"), "fine");
            File.WriteAllText(Path.Combine(dst, "locked.txt"), "old");
            File.WriteAllText(Path.Combine(dst, "extraneous.txt"), "should still be pruned");

            using (new FileStream(Path.Combine(dst, "locked.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                LocalSyncResult result = LocalSyncEngine.Run(src + "\\", dst, Recursive with { Delete = true });

                Assert.Contains(result.FailedFiles, f => f.Path == "locked.txt");
                Assert.Equal(1, result.TransferredFiles); // ok.txt still made it

                // A destination-side failure never suppresses --delete — only source reads do.
                Assert.False(result.PruneSkipped);
                Assert.False(File.Exists(Path.Combine(dst, "extraneous.txt")));
            }

            Assert.Equal("fine", File.ReadAllText(Path.Combine(dst, "ok.txt")));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void FileWhereDirectoryGoes_IsReplacedByDirectory()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(src, "sub", "a.txt"), "x");
            File.WriteAllText(Path.Combine(dst, "sub"), "I am a file where a directory goes");

            LocalSyncResult result = LocalSyncEngine.Run(src + "\\", dst, Recursive);

            Assert.True(Directory.Exists(Path.Combine(dst, "sub")));
            Assert.Equal("x", File.ReadAllText(Path.Combine(dst, "sub", "a.txt")));
            Assert.Empty(result.FailedFiles);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void EmptyDirectoryWhereFileGoes_IsReplaced_NonEmptyFails()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(src, "was-empty"), "now a file");
            File.WriteAllText(Path.Combine(src, "occupied"), "wants to be a file");
            Directory.CreateDirectory(Path.Combine(dst, "was-empty"));
            Directory.CreateDirectory(Path.Combine(dst, "occupied"));
            File.WriteAllText(Path.Combine(dst, "occupied", "child.txt"), "in the way");

            LocalSyncResult result = LocalSyncEngine.Run(src + "\\", dst, Recursive);

            Assert.Equal("now a file", File.ReadAllText(Path.Combine(dst, "was-empty")));
            Assert.True(Directory.Exists(Path.Combine(dst, "occupied")));
            Assert.Contains(result.FailedFiles, f => f.Path == "occupied");
            Assert.Equal(1, result.TransferredFiles);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void Delete_PrunesExtraneousDestEntries()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(Path.Combine(src, "keepdir"));
            File.WriteAllText(Path.Combine(src, "keep.txt"), "k");
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(dst, "extra.txt"), "e");
            Directory.CreateDirectory(Path.Combine(dst, "extradir"));
            File.WriteAllText(Path.Combine(dst, "extradir", "inner.txt"), "i");

            LocalSyncResult result = LocalSyncEngine.Run(src + "\\", dst, Recursive with { Delete = true });

            Assert.True(File.Exists(Path.Combine(dst, "keep.txt")));
            Assert.True(Directory.Exists(Path.Combine(dst, "keepdir")));
            Assert.False(File.Exists(Path.Combine(dst, "extra.txt")));
            Assert.False(Directory.Exists(Path.Combine(dst, "extradir")));
            Assert.Equal(2, result.Prune.DeletedRegularFiles);
            Assert.Equal(1, result.Prune.DeletedDirectories);
            Assert.False(result.PruneSkipped);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void LockedSourceFile_SuppressesDelete()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(dst);
            File.WriteAllText(Path.Combine(src, "unreadable.txt"), "cannot open me");
            File.WriteAllText(Path.Combine(dst, "extraneous.txt"), "must survive a partial source view");

            using (new FileStream(Path.Combine(src, "unreadable.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                LocalSyncResult result = LocalSyncEngine.Run(src + "\\", dst, Recursive with { Delete = true });

                // rsync's io_error rule: a source read failure means the source view is partial, so
                // nothing on the destination may be deleted based on it.
                Assert.Contains(result.FailedFiles, f => f.Path == "unreadable.txt");
                Assert.True(result.PruneSkipped);
                Assert.True(File.Exists(Path.Combine(dst, "extraneous.txt")));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void JunctionAsDestinationRoot_IsWrittenThroughNotUnlinked()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string target = Path.Combine(root, "real-dest");
            string junction = Path.Combine(root, "junction-dest");
            Directory.CreateDirectory(src);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(src, "a.txt"), "through the looking glass");
            WindowsFsTestSupport.CreateDirectoryJunctionOrSkip(junction, target);

            LocalSyncResult result = LocalSyncEngine.Run(src + "\\", junction, Recursive);

            // The user-named destination root must stay a junction and receive the content.
            Assert.NotEqual((FileAttributes)0, File.GetAttributes(junction) & FileAttributes.ReparsePoint);
            Assert.Equal("through the looking glass", File.ReadAllText(Path.Combine(target, "a.txt")));
            Assert.Equal(1, result.TransferredFiles);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void SelfCopy_IsRejected()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(src);

            // "src\ -> src" and "src -> parent" both resolve to copying the tree onto itself.
            Assert.Throws<ArgumentException>(() => LocalSyncEngine.Run(src + "\\", src, Recursive));
            Assert.Throws<ArgumentException>(() => LocalSyncEngine.Run(src, root, Recursive));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void DestinationInsideSource_IsRejected()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            Directory.CreateDirectory(Path.Combine(src, "inner"));

            Assert.Throws<ArgumentException>(
                () => LocalSyncEngine.Run(src, Path.Combine(src, "inner"), Recursive));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void ContentsIntoExistingFile_IsRejected()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string dstFile = Path.Combine(root, "dstfile");
            Directory.CreateDirectory(src);
            File.WriteAllText(dstFile, "not a directory");

            Assert.Throws<ArgumentException>(() => LocalSyncEngine.Run(src + "\\", dstFile, Recursive));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void MissingSource_ThrowsIoException()
    {
        string root = CreateTempDir();
        try
        {
            Assert.ThrowsAny<IOException>(
                () => LocalSyncEngine.Run(Path.Combine(root, "nope"), Path.Combine(root, "dst"), Recursive));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void LongPaths_Over260Chars_AreCopied()
    {
        string root = CreateTempDir();
        try
        {
            string src = Path.Combine(root, "src");
            string deep = Path.Combine(src, string.Join('\\', Enumerable.Repeat(new string('d', 60), 5)));
            Directory.CreateDirectory(deep);
            File.WriteAllText(Path.Combine(deep, "deep.txt"), "far down");

            string dst = Path.Combine(root, "dst");
            LocalSyncResult result = LocalSyncEngine.Run(src + "\\", dst, Recursive);

            string deepDest = Path.Combine(dst, string.Join('\\', Enumerable.Repeat(new string('d', 60), 5)), "deep.txt");
            Assert.True(deepDest.Length > 260);
            Assert.Equal("far down", File.ReadAllText(deepDest));
            Assert.Equal(1, result.TransferredFiles);
        }
        finally
        {
            Cleanup(root);
        }
    }
}
