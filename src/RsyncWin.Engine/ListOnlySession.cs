using RsyncWin.Protocol.FileList;
using RsyncWin.Protocol.Session;
using RsyncWin.Transport;

namespace RsyncWin.Engine;

/// <summary>
/// A complete <c>--list-only</c> pull: handshake, empty filter list, file-list receive, and the
/// measured end-of-session choreography, ending with the remote rsync exiting 0.
/// </summary>
/// <remarks>
/// The tail sequence mirrors the captured protocol-31 list-only session byte-for-byte
/// (<c>vectors/ssh31-sizes-list</c>): client sends NDX_DONE #1, then the #2/#3/#4 burst; server
/// answers with three NDX_DONEs, the stats block, and a goodbye NDX_DONE; client sends the final
/// goodbye. Protocol 31 only — the DONE counts differ at 30 (guarded in <see cref="SessionSetup"/>).
/// </remarks>
public static class ListOnlySession
{
    public sealed record Result(
        SessionContext Session,
        FileListResult FileList,
        SessionStats Stats,
        IReadOnlyList<ServerMessage> ServerMessages);

    public static async Task<Result> RunAsync(
        IRsyncTransport transport, ServerArgvBuilder serverArgs, CancellationToken cancellationToken = default)
    {
        SessionChannel channel = await SessionSetup.OpenAsync(transport, serverArgs, cancellationToken);

        // Nothing to request: end phase 0, then the phase-1/2 + delete-phase burst in one flush.
        channel.Writer.Write([0x00]);
        await channel.Writer.FlushAsync(cancellationToken);
        channel.Writer.Write([0x00, 0x00, 0x00]);
        await channel.Writer.FlushAsync(cancellationToken);

        for (int i = 0; i < 3; i++)
            await SessionSetup.ExpectNdxDoneAsync(channel.Reader, cancellationToken);

        SessionStats stats = await SessionSetup.ReadStatsAsync(channel.Reader, cancellationToken);

        await SessionSetup.ExpectNdxDoneAsync(channel.Reader, cancellationToken); // server's goodbye

        channel.Writer.Write([0x00]); // our goodbye — the last byte on the wire
        await channel.Writer.FlushAsync(cancellationToken);

        return new Result(channel.Session, channel.FileList, stats, channel.ServerMessages);
    }
}
