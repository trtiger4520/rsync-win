using System.Text;
using RsyncWin.Fs;

namespace RsyncWin.Fs.Tests;

/// <summary>Hermetic unit tests for <see cref="BasisFileStore"/>.</summary>
[Trait("Category", "WindowsFs")]
public class BasisFileStoreTests
{
    [Fact]
    public void MissingFile_ReturnsNull()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Assert.False(File.Exists(path));

        FileStream? stream = BasisFileStore.Open(path);

        Assert.Null(stream);
    }

    [Fact]
    public void ExistingFile_IsReadableSeekableAndHasCorrectContent()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        byte[] content = Encoding.UTF8.GetBytes("basis file contents");
        File.WriteAllBytes(path, content);
        try
        {
            using FileStream? stream = BasisFileStore.Open(path);

            Assert.NotNull(stream);
            Assert.True(stream!.CanRead);
            Assert.True(stream.CanSeek);
            Assert.False(stream.CanWrite);

            using MemoryStream buffer = new();
            stream.CopyTo(buffer);
            Assert.Equal(content, buffer.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ExclusivelyLockedFile_ReturnsNull()
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        File.WriteAllBytes(path, [1, 2, 3]);
        try
        {
            using FileStream exclusive = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            FileStream? stream = BasisFileStore.Open(path);

            Assert.Null(stream);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // The whole point of this test: the receiver replaces a stale destination via write-to-temp
    // then a rename onto the original path, while the generator's basis handle is still open, and
    // the basis handle must still read its original bytes to EOF afterwards (NTFS keeps the old
    // data reachable until the handle closes). This only works because BasisFileStore.Open grants
    // FileShare.Delete alongside FileShare.Read.
    //
    // Measured on Windows: File.Move(temp, original, overwrite: true) FAILS with
    // UnauthorizedAccessException whenever any handle is open on `original`, regardless of which
    // FileShare flags that handle was opened with (Read, Read|Delete, ReadWrite|Delete all fail) —
    // .NET's overwrite path uses MOVEFILE_REPLACE_EXISTING, which Windows refuses if the destination
    // has any open handle at all. The combination that actually works while a FileShare.Delete
    // handle is open is an explicit File.Delete(original) followed by a plain File.Move(temp,
    // original) (rename, not overwrite). That is the sequence exercised below; a future receiver
    // (T7) must use delete-then-rename, not the overwrite overload, to replace a stale basis file.
    [Fact]
    public void DeleteThenRenameOverOriginal_SucceedsWhileBasisHandleIsOpen_AndBasisStillReadsOldContent()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string original = Path.Combine(dir, "target.bin");
        string temp = Path.Combine(dir, "target.bin.tmp");
        byte[] oldContent = Encoding.UTF8.GetBytes("old basis content");
        byte[] newContent = Encoding.UTF8.GetBytes("brand new replacement content");
        File.WriteAllBytes(original, oldContent);
        try
        {
            using FileStream? basis = BasisFileStore.Open(original);
            Assert.NotNull(basis);

            File.WriteAllBytes(temp, newContent);

            // Must not throw even though `basis` still holds an open handle on `original`.
            File.Delete(original);
            File.Move(temp, original);

            using MemoryStream buffer = new();
            basis!.CopyTo(buffer);
            Assert.Equal(oldContent, buffer.ToArray());

            // The replace itself is visible to a fresh open of the (now new) path.
            Assert.Equal(newContent, File.ReadAllBytes(original));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Documents the negative finding above: the overwrite overload does not work even with
    // FileShare.Delete granted, so a future receiver must not rely on it for basis replacement.
    [Fact]
    public void FileMoveOverwriteOverload_FailsWhileBasisHandleIsOpen()
    {
        string dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        string original = Path.Combine(dir, "target.bin");
        string temp = Path.Combine(dir, "target.bin.tmp");
        File.WriteAllBytes(original, "old basis content"u8.ToArray());
        try
        {
            using FileStream? basis = BasisFileStore.Open(original);
            Assert.NotNull(basis);

            File.WriteAllBytes(temp, "brand new replacement content"u8.ToArray());

            Assert.Throws<UnauthorizedAccessException>(() => File.Move(temp, original, overwrite: true));
        }
        finally
        {
            File.Delete(temp);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DirectoryPath_ReturnsNullInsteadOfEscapingTheSession()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"rsyncwin-basis-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Null(BasisFileStore.Open(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
