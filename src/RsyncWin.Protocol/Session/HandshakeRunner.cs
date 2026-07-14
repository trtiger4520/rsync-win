using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Protocol.Session;

/// <summary>Client-side knobs for a handshake. The defaults are what production sessions use.</summary>
public sealed record HandshakeOptions
{
    /// <summary>The version we advertise; the session runs <c>min(this, peer)</c>.</summary>
    public int AdvertisedProtocol { get; init; } = RsyncConstants.ProtocolVersion;

    /// <summary>Space-separated checksum-choice list. Must contain only implemented names.</summary>
    public string ChecksumOffer { get; init; } = ChecksumNegotiator.DefaultOffer;

    /// <summary>
    /// Set for a daemon (<c>rsync://</c>) session: the protocol version already agreed via the
    /// textual <c>@RSYNCD: &lt;ver&gt;.&lt;sub&gt;</c> greeting. When set, the binary version-int
    /// exchange is skipped entirely (<c>setup_protocol()</c> does the same once
    /// <c>remote_protocol</c> is already known) and this value is used as the negotiated protocol.
    /// Everything downstream — compat flags, checksum negotiation, the seed — is unchanged.
    /// </summary>
    public int? PreNegotiatedProtocolVersion { get; init; }
}

/// <summary>
/// Runs the client side of the handshake prologue and yields the immutable
/// <see cref="SessionContext"/>. Consumes exactly the prologue — the reader is left positioned on
/// the first multiplexed byte.
/// </summary>
/// <remarks>
/// The order is measured (<c>docs/wire-notes.md</c>) and source-verified against
/// <c>compat.c setup_protocol()</c>:
/// <list type="number">
/// <item>both sides write their version as a 4-byte LE int <em>eagerly</em>, then read the peer's;</item>
/// <item>protocol ≥ 30: the <em>server</em> writes a compat_flags varint, we read it;</item>
/// <item>checksum vstrings both ways — but only if the server's compat_flags has
/// <c>CF_VARINT_FLIST_FLAGS</c>, which does double duty as the "peer can negotiate strings" bit.
/// The client must therefore read compat_flags <em>before</em> sending its offer;</item>
/// <item>checksum_seed LAST, a 4-byte LE int, at every protocol version.</item>
/// </list>
/// Reading the seed any earlier consumes the compat varint and desyncs every later read.
///
/// <para>
/// Daemon sessions (<see cref="HandshakeOptions.PreNegotiatedProtocolVersion"/>) skip step 1
/// entirely — the version was already agreed via the textual <c>@RSYNCD:</c> greeting — and join
/// the sequence at step 2. Everything else, including gating on compat flags, is identical.
/// </para>
/// </remarks>
public static class HandshakeRunner
{
    public static async ValueTask<SessionContext> RunClientAsync(
        PipeReader input,
        PipeWriter output,
        HandshakeOptions options,
        CancellationToken cancellationToken = default)
    {
        foreach (string name in options.ChecksumOffer.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            ChecksumNegotiator.Map(name); // reject unimplemented names before they can win a negotiation

        int protocol;
        if (options.PreNegotiatedProtocolVersion is int preNegotiated)
        {
            // Daemon mode: setup_protocol() skips the binary version-int exchange once
            // remote_protocol is already known from the textual greeting. Nothing is written or
            // read here; we join the sequence directly at compat_flags below.
            ArgumentOutOfRangeException.ThrowIfLessThan(preNegotiated, RsyncConstants.MinProtocolVersion);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(preNegotiated, RsyncConstants.MaxProtocolVersion);
            protocol = preNegotiated;
        }
        else
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(options.AdvertisedProtocol, RsyncConstants.MinProtocolVersion);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(options.AdvertisedProtocol, RsyncConstants.ProtocolVersion);

            WriteInt32(output, options.AdvertisedProtocol);
            await output.FlushAsync(cancellationToken);

            int peerVersion = BinaryPrimitives.ReadInt32LittleEndian(await ReadExactAsync(input, 4, cancellationToken));
            if (peerVersion < RsyncConstants.MinProtocolVersion || peerVersion > RsyncConstants.MaxProtocolVersion)
            {
                // Matches rsync's "is your shell clean?" failure mode: a version outside the sane range
                // usually means a login script polluted the stream, not an actual old/new rsync.
                throw new ProtocolException(
                    RsyncExitCode.ProtocolIncompatibility,
                    $"peer advertised protocol {peerVersion}, outside [{RsyncConstants.MinProtocolVersion}, {RsyncConstants.MaxProtocolVersion}] — protocol version mismatch, or the stream is polluted");
            }
            protocol = Math.Min(options.AdvertisedProtocol, peerVersion);
        }

        int compatFlags = 0;
        var checksum = ChecksumNegotiator.Default(protocol);
        if (protocol >= 30)
        {
            compatFlags = await ReadVarintAsync(input, cancellationToken);
            if ((compatFlags & RsyncConstants.CompatIncRecurse) != 0)
            {
                // We never send the 'i' capability letter, so a server that sets this anyway is
                // not honoring client_info — the flist exchange that follows would be undecodable.
                throw new ProtocolException(
                    RsyncExitCode.ProtocolIncompatibility,
                    "server forced incremental recursion, which we neither request nor support");
            }

            if ((compatFlags & RsyncConstants.CompatVarintFlistFlags) != 0)
            {
                WriteVstring(output, options.ChecksumOffer);
                await output.FlushAsync(cancellationToken);

                string serverList = Encoding.ASCII.GetString(await ReadVstringAsync(input, cancellationToken));
                checksum = ChecksumNegotiator.Map(ChecksumNegotiator.NegotiateName(options.ChecksumOffer, serverList));
            }
        }

        int seed = BinaryPrimitives.ReadInt32LittleEndian(await ReadExactAsync(input, 4, cancellationToken));

        return new SessionContext
        {
            Protocol = protocol,
            CompatFlags = compatFlags,
            ChecksumSeed = seed,
            TransferChecksum = checksum,
            FileChecksum = checksum,
        };
    }

    private static void WriteInt32(PipeWriter output, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(output.GetSpan(4), value);
        output.Advance(4);
    }

    private static void WriteVstring(PipeWriter output, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        int written = VstringCodec.Write(output.GetSpan(2 + bytes.Length), bytes);
        output.Advance(written);
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes, consuming no more.</summary>
    /// <exception cref="InvalidDataException">Stream ended mid-prologue — the peer died or ssh
    /// failed before rsync ever started (its stderr says which).</exception>
    private static async ValueTask<byte[]> ReadExactAsync(PipeReader input, int count, CancellationToken cancellationToken)
    {
        ReadResult result = await input.ReadAtLeastAsync(count, cancellationToken);
        if (result.Buffer.Length < count)
        {
            input.AdvanceTo(result.Buffer.Start, result.Buffer.End);
            throw new InvalidDataException(
                $"handshake: stream ended after {result.Buffer.Length} of {count} expected bytes");
        }

        byte[] bytes = result.Buffer.Slice(0, count).ToArray();
        input.AdvanceTo(result.Buffer.GetPosition(count));
        return bytes;
    }

    private static async ValueTask<int> ReadVarintAsync(PipeReader input, CancellationToken cancellationToken)
    {
        byte header = (await ReadExactAsync(input, 1, cancellationToken))[0];
        int total = VarintCodec.WireLength(header);

        byte[] wire = new byte[total];
        wire[0] = header;
        if (total > 1)
            (await ReadExactAsync(input, total - 1, cancellationToken)).CopyTo(wire, 1);

        return VarintCodec.ReadVarint(wire).Value;
    }

    private static async ValueTask<byte[]> ReadVstringAsync(PipeReader input, CancellationToken cancellationToken)
    {
        // Wire form per VstringCodec: 1 length byte, or 2 big-endian-with-flag bytes when bit 7 set.
        int length = (await ReadExactAsync(input, 1, cancellationToken))[0];
        if ((length & 0x80) != 0)
            length = (length & 0x7F) << 8 | (await ReadExactAsync(input, 1, cancellationToken))[0];

        return length == 0 ? [] : await ReadExactAsync(input, length, cancellationToken);
    }
}
