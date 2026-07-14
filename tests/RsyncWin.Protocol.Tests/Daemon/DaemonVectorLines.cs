using System.Text;

namespace RsyncWin.Protocol.Tests.Daemon;

/// <summary>
/// The daemon preamble is line-based text glued directly onto a binary stream (docs/daemon-spec.md
/// §1) — captured vectors cannot be read with <c>File.ReadAllLines</c>. This walks a captured
/// <c>c2s.bin</c>/<c>s2c.bin</c> one <c>\n</c>-terminated line at a time, tracking the byte offset so
/// callers can locate exactly where the text preamble ends and the binary phase begins.
/// </summary>
internal static class DaemonVectorLines
{
    /// <summary>Reads the line starting at <paramref name="offset"/>; returns it (no trailing '\n')
    /// plus the offset of the byte right after that '\n'.</summary>
    public static (string Line, int NextOffset) ReadLine(byte[] raw, int offset)
    {
        int newline = Array.IndexOf(raw, (byte)'\n', offset);
        if (newline < 0)
            throw new InvalidOperationException($"no newline found at/after offset {offset}");
        string line = Encoding.UTF8.GetString(raw, offset, newline - offset);
        return (line, newline + 1);
    }
}
