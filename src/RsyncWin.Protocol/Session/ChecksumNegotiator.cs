using RsyncWin.Protocol.Checksums;

namespace RsyncWin.Protocol.Session;

/// <summary>
/// Picks the transfer checksum from the two space-separated name lists exchanged during
/// <c>negotiate_the_strings()</c>.
/// </summary>
/// <remarks>
/// <para>
/// The winner is the first name in the <em>client's</em> list that the server also lists — both
/// sides compute the same answer because both know which side is the client. With our
/// single-algorithm offer the rule is unobservable (any common-entry rule yields the same result);
/// it becomes load-bearing the moment the offer grows, so pin it with <c>--debug=nstr</c> against a
/// live rsync before advertising xxh64 (P4).
/// </para>
/// <para>
/// Measured (rsync 3.4.3): stock clients offer <c>xxh128 xxh3 xxh64 md5 md4</c>; servers reply with
/// the same plus <c> none</c>. We offer only what we implement — <c>md5</c> for now — so the
/// negotiated result is always something we can actually compute. Never offer <c>md4</c> at
/// protocol 30/31: stock builds disagree among themselves on its seed placement there.
/// </para>
/// </remarks>
public static class ChecksumNegotiator
{
    /// <summary>Our checksum-choice offer. xxh64 joins once golden-vectored against a live delta (P4).</summary>
    public const string DefaultOffer = "md5";

    /// <summary>The algorithm used when no negotiation happens (peer too old or below protocol 30).</summary>
    public static ChecksumAlgorithm Default(int protocol) =>
        protocol >= 30 ? ChecksumAlgorithm.Md5 : ChecksumAlgorithm.Md4;

    /// <summary>First name in <paramref name="clientOffer"/> that <paramref name="serverList"/> contains.</summary>
    /// <exception cref="ProtocolException">No common name — the session cannot proceed.</exception>
    public static string NegotiateName(string clientOffer, string serverList)
    {
        string[] server = serverList.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (string name in clientOffer.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Array.IndexOf(server, name) >= 0)
                return name;
        }
        // rsync exits RERR_UNSUPPORTED (4) here, not RERR_PROTOCOL — verified against compat.c.
        throw new ProtocolException(
            RsyncExitCode.UnsupportedAction,
            $"no common checksum algorithm: we offered \"{clientOffer}\", server accepts \"{serverList}\"");
    }

    /// <summary>Maps a negotiated wire name to an implemented algorithm.</summary>
    /// <exception cref="ArgumentException">The name is not implemented — a caller bug, since the
    /// negotiated result is always drawn from our own offer.</exception>
    public static ChecksumAlgorithm Map(string name) => name switch
    {
        "md4" => ChecksumAlgorithm.Md4,
        "md5" => ChecksumAlgorithm.Md5,
        "xxh64" => ChecksumAlgorithm.XxHash64,
        // Whole-file sums only until the block seed rules are pinned (P6) — fine for pulls,
        // which never compute xxh128 block sums; keep it out of DefaultOffer until then.
        "xxh128" => ChecksumAlgorithm.XxHash128,
        _ => throw new ArgumentException($"checksum \"{name}\" is not implemented; it must never be offered", nameof(name)),
    };
}
