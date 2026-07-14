namespace RsyncWin.Fs;

/// <summary>
/// Maps a single rsync wire name (already validated Unix-side by <c>FileListReader</c>: no leading
/// '/', no '..' components split on '/') into a Windows-safe relative path. Pure string mapping,
/// no filesystem access — the caller is responsible for joining the result under a destination and
/// re-checking containment, since a mapper bug must never be the last line of defense.
/// </summary>
public static class WindowsPathMapper
{
    private static readonly char[] InvalidChars = ['\\', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Splits <paramref name="name"/> on '/', sanitizes each segment for Windows/NTFS, and joins
    /// with '\'. <paramref name="name"/>Changed reports whether any segment differed from the raw
    /// input, so callers can surface a warning without redoing the mapping.
    /// </summary>
    public static (string Mapped, bool Changed) Map(string name)
    {
        string[] segments = name.Split('/');
        bool changed = false;
        for (int i = 0; i < segments.Length; i++)
        {
            string mapped = MapSegment(segments[i]);
            if (mapped != segments[i])
            {
                changed = true;
                segments[i] = mapped;
            }
        }
        return (string.Join('\\', segments), changed);
    }

    private static string MapSegment(string segment)
    {
        Span<char> buffer = segment.Length <= 256 ? stackalloc char[segment.Length] : new char[segment.Length];
        for (int i = 0; i < segment.Length; i++)
        {
            char c = segment[i];
            buffer[i] = c < 0x20 || Array.IndexOf(InvalidChars, c) >= 0 ? '_' : c;
        }
        string sanitized = new(buffer);

        // '.' and '..' are navigation tokens, not names to sanitize — leave them intact so a
        // caller's containment check (the real backstop) still sees a genuine traversal attempt.
        if (sanitized is "." or "..")
            return sanitized;

        if (sanitized.Length == 0)
            return "_";

        int dot = sanitized.IndexOf('.');
        string baseName = dot >= 0 ? sanitized[..dot] : sanitized;
        if (Array.Exists(ReservedNames, n => string.Equals(n, baseName, StringComparison.OrdinalIgnoreCase)))
            sanitized = dot >= 0 ? string.Concat(sanitized.AsSpan(0, dot), "_", sanitized.AsSpan(dot)) : sanitized + "_";

        if (sanitized.EndsWith('.') || sanitized.EndsWith(' '))
            sanitized += "_";

        return sanitized;
    }
}
