using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using RsyncWin.Protocol.Session;

namespace RsyncWin.Protocol.Mux;

/// <summary>
/// Demultiplexes the peer's framed stream into a continuous data channel plus out-of-band
/// messages. Frame boundaries carry <em>no</em> semantics — a logical value routinely spans
/// frames (pinned by the captured generator stream in the ndx tests), so the only correct
/// consumption model is "read N data bytes across however many frames that takes".
/// </summary>
/// <remarks>
/// Out-of-band handling: zero-length <see cref="MessageTag.Data"/> frames and
/// <see cref="MessageTag.Noop"/> are keep-alives and are skipped; <see cref="MessageTag.ErrorExit"/>
/// raises a <see cref="ProtocolException"/> carrying the peer's exit code; every other tag in
/// <paramref name="allowedTags"/> is handed to <see cref="MessageReceived"/>. A tag outside the
/// set — undefined, or defined but impossible for our role (rsync-internal plumbing like
/// <c>MSG_REDO</c>) — means the stream is desynced and throws (exit-12 semantics), because a
/// garbage header that happens to parse must not be silently skipped.
/// </remarks>
public sealed class MultiplexReader(PipeReader input, IReadOnlySet<MessageTag>? allowedTags = null)
{
    /// <summary>Tags a pull client (receiver/generator) may legitimately receive.</summary>
    public static readonly IReadOnlySet<MessageTag> PullClientTags = new HashSet<MessageTag>
    {
        MessageTag.Data,
        MessageTag.ErrorXfer,
        MessageTag.Info,
        MessageTag.Error,
        MessageTag.Warning,
        MessageTag.IoError,
        MessageTag.IoTimeout,
        MessageTag.Noop,
        MessageTag.ErrorExit,
        // A pull client receives this too: the sender substitutes it for a reply when it cannot
        // open a requested file (vanished mid-session).
        MessageTag.NoSend,
    };

    private readonly IReadOnlySet<MessageTag> _allowedTags = allowedTags ?? PullClientTags;
    private int _remainingInFrame;

    /// <summary>Out-of-band frames (Info/Error/Stats/…) with their raw payloads.</summary>
    public Action<MessageTag, byte[]>? MessageReceived { get; set; }

    /// <summary>Reads exactly <paramref name="count"/> data-channel bytes, spanning frames as needed.</summary>
    /// <exception cref="InvalidDataException">The stream ended or desynced mid-read.</exception>
    /// <exception cref="ProtocolException">The peer signalled <see cref="MessageTag.ErrorExit"/>.</exception>
    public async ValueTask<byte[]> ReadDataExactlyAsync(int count, CancellationToken cancellationToken = default)
    {
        byte[] result = new byte[count];
        int filled = 0;
        while (filled < count)
        {
            if (_remainingInFrame == 0)
            {
                await AdvanceToNextDataFrameAsync(cancellationToken);
                continue;
            }

            ReadResult read = await input.ReadAsync(cancellationToken);
            if (read.Buffer.IsEmpty)
            {
                if (read.IsCompleted)
                    throw new InvalidDataException(
                        $"multiplex: stream ended with {_remainingInFrame} bytes missing from the current frame");
                input.AdvanceTo(read.Buffer.Start, read.Buffer.End);
                continue;
            }

            int take = (int)Math.Min(read.Buffer.Length, Math.Min(_remainingInFrame, count - filled));
            read.Buffer.Slice(0, take).CopyTo(result.AsSpan(filled, take));
            input.AdvanceTo(read.Buffer.GetPosition(take));
            filled += take;
            _remainingInFrame -= take;
        }
        return result;
    }

    /// <summary>Reads a single data-channel byte.</summary>
    public async ValueTask<byte> ReadDataByteAsync(CancellationToken cancellationToken = default) =>
        (await ReadDataExactlyAsync(1, cancellationToken))[0];

    private async ValueTask AdvanceToNextDataFrameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            MuxHeader header = MuxHeader.Read(await ReadRawAsync(MuxHeader.Size, cancellationToken));
            if (header.IsKeepAlive)
                continue;

            if (header.Tag == MessageTag.Data)
            {
                _remainingInFrame = header.PayloadLength;
                return;
            }

            if (!_allowedTags.Contains(header.Tag))
                throw new InvalidDataException(
                    $"multiplex: message tag {(int)header.Tag} is invalid on this stream — desynced");

            byte[] payload = header.PayloadLength == 0
                ? []
                : await ReadRawAsync(header.PayloadLength, cancellationToken);

            if (header.Tag == MessageTag.ErrorExit)
            {
                // The peer is tearing the session down; its exit code takes precedence over the
                // transport's and the session exits with it verbatim. Legal payloads are exactly
                // 4 bytes (LE code) or empty (the goodbye/ack leg — code 0). The echo obligation
                // of the proto-31 goodbye handshake belongs to the role sessions' error paths.
                if (payload.Length is not (0 or 4))
                    throw new InvalidDataException(
                        $"multiplex: MSG_ERROR_EXIT with illegal payload length {payload.Length}");
                int exitCode = payload.Length == 4 ? BinaryPrimitives.ReadInt32LittleEndian(payload) : 0;
                throw new ProtocolException((RsyncExitCode)exitCode,
                    payload.Length == 0
                        ? "peer signalled MSG_ERROR_EXIT (goodbye leg, code 0)"
                        : $"peer signalled MSG_ERROR_EXIT with code {exitCode}");
            }

            if (header.Tag == MessageTag.Noop)
                continue; // protocol-30-style keep-alive: prove-liveness only, never dispatched

            MessageReceived?.Invoke(header.Tag, payload);
        }
    }

    /// <summary>Reads exactly <paramref name="count"/> raw (pre-demux) bytes.</summary>
    private async ValueTask<byte[]> ReadRawAsync(int count, CancellationToken cancellationToken)
    {
        ReadResult read = await input.ReadAtLeastAsync(count, cancellationToken);
        if (read.Buffer.Length < count)
        {
            input.AdvanceTo(read.Buffer.Start, read.Buffer.End);
            throw new InvalidDataException(
                $"multiplex: stream ended after {read.Buffer.Length} of {count} expected bytes");
        }

        byte[] bytes = read.Buffer.Slice(0, count).ToArray();
        input.AdvanceTo(read.Buffer.GetPosition(count));
        return bytes;
    }
}
