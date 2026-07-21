namespace RsyncWin.Fs;

/// <summary>
/// The shared "write to a temp file, then atomically replace the destination" pieces used by every
/// path that materializes a received/copied file on Windows: temp-file naming, the
/// clear-read-only + overwrite-move + mtime-restore finalize, and the FILETIME-range mtime clamp.
/// Extracted from <c>PullSession.ReplyReceiver</c> so the local-copy engine shares byte-for-byte
/// identical replace semantics with the wire receiver.
/// </summary>
internal static class DestinationReplacer
{
    /// <summary>A collision-safe sibling temp path for <paramref name="finalPath"/> —
    /// ".{name}.{guid8}.rsyncwin-tmp" in the same directory, so the final move never crosses
    /// volumes.</summary>
    public static string TempPathFor(string finalPath) =>
        Path.Combine(
            Path.GetDirectoryName(finalPath)!,
            $".{Path.GetFileName(finalPath)}.{Guid.NewGuid().ToString("N")[..8]}.rsyncwin-tmp");

    /// <summary>
    /// Moves the fully written <paramref name="tempPath"/> into place over
    /// <paramref name="finalPath"/> and restores the entry's mtime. rsync's contract replaces
    /// read-only destinations, but <see cref="File.Move(string, string, bool)"/> alone refuses —
    /// the read-only attribute is cleared first. Throws <see cref="IOException"/> /
    /// <see cref="UnauthorizedAccessException"/> on a locked or otherwise unreplaceable
    /// destination; callers decide whether that is per-file or fatal.
    /// </summary>
    public static void FinalizeReplace(string tempPath, string finalPath, DateTime? mtimeUtc)
    {
        if (File.Exists(finalPath))
        {
            FileAttributes attributes = File.GetAttributes(finalPath);
            if ((attributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(finalPath, attributes & ~FileAttributes.ReadOnly);
        }
        File.Move(tempPath, finalPath, overwrite: true);
        if (mtimeUtc is { } mtime)
            File.SetLastWriteTimeUtc(finalPath, mtime);
    }

    /// <summary>
    /// Win32 FileTime starts at 1601 and <see cref="DateTimeOffset"/> ends at year 9999; rsync's
    /// mtime varlong is wider on both ends. A peer's bogus timestamp clamps instead of throwing
    /// out of the session.
    /// </summary>
    public static DateTime ClampedMtimeUtc(long unixSeconds)
    {
        const long minSettable = -11_644_473_599; // 1601-01-01T00:00:01Z — FILETIME 0 means "don't change"
        const long maxSettable = 253_402_300_799; // 9999-12-31T23:59:59Z — DateTimeOffset's ceiling
        return DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(unixSeconds, minSettable, maxSettable)).UtcDateTime;
    }
}
