namespace RsyncWin.Fs;

/// <summary>
/// Opens an existing local file for read-only use as a delta-transfer basis (the generator's
/// signature pass, and later the receiver's reconstruction pass, both read the same on-disk bytes
/// while the destination path may be replaced out from under them). Pure filesystem seam — no
/// signature or reconstruction logic lives here.
/// </summary>
public static class BasisFileStore
{
    /// <summary>
    /// Opens <paramref name="path"/> read-only for use as a basis file. Returns <c>null</c> instead
    /// of throwing when the file is missing, locked, or otherwise unreadable — a failed basis open
    /// just means "no basis, fall back to a full transfer", never a reason to fail the pull session.
    /// <para>
    /// Uses <see cref="FileShare.Read"/> | <see cref="FileShare.Delete"/> so the receiver can later
    /// replace this file while the basis handle stays open. Measured on Windows: an overwriting
    /// <c>File.Move(temp, original, overwrite: true)</c> fails with access-denied whenever *any*
    /// handle is open on <paramref name="path"/>, no matter which <see cref="FileShare"/> flags that
    /// handle used — but an explicit <c>File.Delete(original)</c> followed by a plain (non-overwrite)
    /// <c>File.Move(temp, original)</c> succeeds, provided the open handle grants
    /// <see cref="FileShare.Delete"/>. Granting it here does not make the basis handle's reads
    /// unsafe: NTFS keeps the original file's data reachable through an already-open handle until
    /// that handle is closed, even after its directory entry has been replaced. A future receiver
    /// must use delete-then-rename, not the overwrite overload, to replace a basis file in place.
    /// </para>
    /// </summary>
    public static FileStream? Open(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
