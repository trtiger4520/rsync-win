using RsyncWin.Protocol;
using RsyncWin.Protocol.FileList;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Session;
using RsyncWin.Protocol.Wire;
using RsyncWin.Transport;

namespace RsyncWin.Engine;

/// <summary>
/// A complete <c>--list-only</c> pull: handshake, empty filter list, file-list receive, and the
/// measured end-of-session choreography, ending with the remote rsync exiting 0.
/// </summary>
/// <remarks>
/// The tail sequence mirrors the captured protocol-31 list-only session byte-for-byte
/// (<c>vectors/ssh31-sizes-list</c>): client sends NDX_DONE, then three more NDX_DONEs; server
/// answers with three NDX_DONEs, the five varlong(3) stats, and a goodbye NDX_DONE; client sends
/// the final goodbye NDX_DONE. Protocol 31 only — the DONE counts differ at 30.
/// </remarks>
public static class ListOnlySession
{
    public sealed record Result(
        SessionContext Session,
        FileListResult FileList,
        SessionStats Stats,
        IReadOnlyList<(MessageTag Tag, string Text)> ServerMessages);

    /// <summary>The sender's end-of-session statistics block — five varlong(3) values.</summary>
    public sealed record SessionStats(
        long TotalRead, long TotalWritten, long TotalSize, long FlistBuildTime, long FlistTransferTime);

    public static async Task<Result> RunAsync(
        IRsyncTransport transport, ServerArgvBuilder serverArgs, CancellationToken cancellationToken = default)
    {
        SessionContext session = await HandshakeRunner.RunClientAsync(
            transport.Input, transport.Output, new HandshakeOptions(), cancellationToken);
        if (session.Protocol < 31)
            throw new ProtocolException(RsyncExitCode.ProtocolIncompatibility,
                $"list-only choreography is implemented for protocol 31, peer negotiated {session.Protocol}");
        if (!session.VarintFlistFlags)
        {
            // Protocol 31 predates CF_VARINT_FLIST_FLAGS (rsync 3.1.0 vs 3.2.4): a 3.1.x–3.2.3
            // server passes the version check yet sends byte-mode xflags and no end-of-list
            // io_error varint — FileListReader would desync or mutually hang. Fail loudly instead.
            throw new ProtocolException(RsyncExitCode.ProtocolIncompatibility,
                "peer did not negotiate varint file-list flags (rsync older than 3.2.4); byte-mode file lists are not implemented");
        }

        var reader = new MultiplexReader(transport.Input);
        var writer = new MultiplexWriter(transport.Output);
        var serverMessages = new List<(MessageTag Tag, string Text)>();
        reader.MessageReceived = (tag, payload) =>
            serverMessages.Add((tag, System.Text.Encoding.UTF8.GetString(payload)));

        // Empty filter list: a single int32 0. Framed — client→server is multiplexed at 30+.
        writer.Write([0, 0, 0, 0]);
        await writer.FlushAsync(cancellationToken);

        var options = new FileListOptions
        {
            Protocol = session.Protocol,
            PreserveUid = serverArgs.PreserveOwner,
            PreserveGid = serverArgs.PreserveGroup,
            PreserveLinks = serverArgs.PreserveLinks,
            PreserveDevices = serverArgs.PreserveDevices,
            PreserveSpecials = serverArgs.PreserveDevices,
            Id0Names = (session.CompatFlags & RsyncConstants.CompatId0Names) != 0,
        };
        FileListResult fileList = await FileListReader.ReadAsync(reader, options, cancellationToken);

        // Nothing to request: end phase 0, then phases 1/2 + the pre-stats DONE in one flush.
        writer.Write([0x00]);
        await writer.FlushAsync(cancellationToken);
        writer.Write([0x00, 0x00, 0x00]);
        await writer.FlushAsync(cancellationToken);

        for (int i = 0; i < 3; i++)
            await ExpectNdxDoneAsync(reader, cancellationToken);

        var stats = new SessionStats(
            await reader.ReadVarlongAsync(3, cancellationToken),
            await reader.ReadVarlongAsync(3, cancellationToken),
            await reader.ReadVarlongAsync(3, cancellationToken),
            await reader.ReadVarlongAsync(3, cancellationToken),
            await reader.ReadVarlongAsync(3, cancellationToken));

        await ExpectNdxDoneAsync(reader, cancellationToken); // server's goodbye

        writer.Write([0x00]); // our goodbye — the last byte on the wire
        await writer.FlushAsync(cancellationToken);

        return new Result(session, fileList, stats, serverMessages);
    }

    private static async ValueTask ExpectNdxDoneAsync(MultiplexReader reader, CancellationToken cancellationToken)
    {
        // NDX_DONE is the single byte 0x00 (ndx-codec form, state untouched).
        byte b = await reader.ReadDataByteAsync(cancellationToken);
        if (b != 0x00)
            throw new InvalidDataException($"expected NDX_DONE, got 0x{b:x2} — session choreography desynced");
    }
}
