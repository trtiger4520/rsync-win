using System.Text;
using RsyncWin.Protocol;
using RsyncWin.Protocol.Checksums;
using RsyncWin.Protocol.FileList;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Session;
using RsyncWin.Protocol.Wire;
using RsyncWin.Transport;

namespace RsyncWin.Engine;

/// <summary>
/// The sender's end-of-session statistics block — five varlong(3) values, wire order pinned by
/// capture: the sender's total_read (bytes WE sent) comes FIRST, then its total_written (bytes we
/// received), then the total regular-file size, then flist build/transfer times.
/// </summary>
public sealed record SessionStats(
    long SenderRead, long SenderWritten, long TotalSize, long FlistBuildTime, long FlistTransferTime);

/// <summary>An out-of-band message the server sent during the session.</summary>
public sealed record ServerMessage(MessageTag Tag, byte[] Payload)
{
    /// <summary>The payload as text — meaningful for the Info/Error/Warning family only.</summary>
    public string Text => Encoding.UTF8.GetString(Payload);
}

/// <summary>
/// The shared session prologue every pull-side role runs: handshake with guards, mux channels,
/// message collection, the empty filter list, and the file-list receive.
/// </summary>
internal sealed record SessionChannel(
    SessionContext Session,
    MultiplexReader Reader,
    MultiplexWriter Writer,
    FileListResult FileList,
    List<ServerMessage> ServerMessages);

internal static class SessionSetup
{
    internal static async Task<SessionChannel> OpenAsync(
        IRsyncTransport transport, ServerArgvBuilder serverArgs, CancellationToken cancellationToken,
        HandshakeOptions? handshake = null)
    {
        SessionContext session = await HandshakeRunner.RunClientAsync(
            transport.Input, transport.Output, handshake ?? new HandshakeOptions(), cancellationToken);
        if (session.Protocol < 31)
            throw new ProtocolException(RsyncExitCode.ProtocolIncompatibility,
                $"session choreography is implemented for protocol 31, peer negotiated {session.Protocol}");
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
        var serverMessages = new List<ServerMessage>();
        reader.MessageReceived = (tag, payload) => serverMessages.Add(new ServerMessage(tag, payload));

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
            Checksum = serverArgs.Checksum,
            ChecksumLength = serverArgs.Checksum ? StrongChecksum.DigestLength(session.TransferChecksum) : 0,
        };
        FileListResult fileList = await FileListReader.ReadAsync(reader, options, cancellationToken);

        return new SessionChannel(session, reader, writer, fileList, serverMessages);
    }

    internal static async ValueTask<SessionStats> ReadStatsAsync(
        MultiplexReader reader, CancellationToken cancellationToken) =>
        new(await reader.ReadVarlongAsync(3, cancellationToken),
            await reader.ReadVarlongAsync(3, cancellationToken),
            await reader.ReadVarlongAsync(3, cancellationToken),
            await reader.ReadVarlongAsync(3, cancellationToken),
            await reader.ReadVarlongAsync(3, cancellationToken));

    internal static async ValueTask ExpectNdxDoneAsync(
        MultiplexReader reader, CancellationToken cancellationToken)
    {
        // NDX_DONE is the single byte 0x00 (ndx-codec form, state untouched).
        byte b = await reader.ReadDataByteAsync(cancellationToken);
        if (b != 0x00)
            throw new InvalidDataException($"expected NDX_DONE, got 0x{b:x2} — session choreography desynced");
    }
}
