using System.Security.Cryptography;
using System.Text;

namespace RsyncWin.Protocol.Daemon;

/// <summary>
/// The daemon challenge–response auth exchange (docs/daemon-spec.md §2). Unrelated to the in-band
/// transfer checksum: this is always plain, seed-free MD5, regardless of the negotiated transfer
/// checksum algorithm.
/// </summary>
/// <remarks>
/// MEASURED (daemon31-auth-pull, reproduced computationally): password "opensesame" + challenge
/// "NA08xsBNp7g58/f/MZdzCA" yields digest "u0qCkuq+uGjvPV1avQikyw" — exactly what 3.4.3 sent.
/// </remarks>
public static class DaemonAuth
{
    /// <summary>
    /// <c>base64(MD5(password + challenge))</c> with trailing <c>=</c> padding stripped — password
    /// bytes first, challenge bytes appended, no newline, standard base64 alphabet.
    /// </summary>
    public static string ComputeDigest(string password, string challenge)
    {
        byte[] input = Encoding.UTF8.GetBytes(password + challenge);
        byte[] hash = MD5.HashData(input);
        return Convert.ToBase64String(hash).TrimEnd('=');
    }

    /// <summary>Formats the client's auth reply line: <c>&lt;user&gt; &lt;digest&gt;\n</c>.</summary>
    public static string FormatReply(string user, string digest) => $"{user} {digest}\n";
}
