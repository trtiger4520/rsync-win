using System.Text;

namespace RsyncWin.Protocol.Daemon;

/// <summary>
/// Frames the server argv words (produced by <see cref="Session.ServerArgvBuilder"/>, minus its
/// leading <c>"rsync"</c> program-name word) onto the raw daemon socket, right after
/// <c>@RSYNCD: OK\n</c> and before any multiplexed byte (docs/daemon-spec.md §3).
/// </summary>
/// <remarks>
/// MEASURED (daemon31-pull-rt): <c>--server\0--sender\0-tre.LsfxCIvu\0.\0tree/\0\0</c> — each word
/// NUL-terminated, the whole list closed by one more NUL (an empty string). Protocol 29 uses
/// <c>\n</c> in place of NUL throughout, closed by an empty line (daemon29-auth-pull).
/// </remarks>
public static class DaemonArgWriter
{
    /// <summary>Frames <paramref name="words"/> for <paramref name="protocol"/> and returns the wire bytes.</summary>
    public static byte[] Write(IReadOnlyList<string> words, int protocol)
    {
        byte separator = protocol >= 30 ? (byte)0 : (byte)'\n';

        using var buffer = new MemoryStream();
        foreach (string word in words)
        {
            buffer.Write(Encoding.UTF8.GetBytes(word));
            buffer.WriteByte(separator);
        }
        // The list terminator is one more separator on its own: a lone NUL (>=30, an empty
        // string) or a lone newline (29, an empty line) with no word bytes before it.
        buffer.WriteByte(separator);

        return buffer.ToArray();
    }
}
