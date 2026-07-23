using System.Threading.Channels;
using RsyncWin.Fs;
using RsyncWin.Protocol;
using RsyncWin.Protocol.Checksums;
using RsyncWin.Protocol.Delta;
using RsyncWin.Protocol.FileList;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Session;
using RsyncWin.Protocol.Wire;
using RsyncWin.Transport;

namespace RsyncWin.Engine;

/// <summary>
/// A full push: handshake, flist (no filter list, no terminating int32), then the server-generator's
/// request/reply loop until the measured protocol-31 goodbye. We are the sender — the pure core
/// (<see cref="MatchSearcher"/>, <see cref="SumRequestReader"/>) does the wire-shape work; this class
/// only wires it to the local filesystem and the request/reply choreography.
/// </summary>
/// <remarks>
/// Phase choreography pinned by capture (<c>docs/wire-notes.md</c> "Push direction" section, and
/// <c>docs/transfer-spec.md</c> "P7 additions"): DONE maps mirror <see cref="PullSession"/> with
/// roles swapped and no stats block. The server-generator writes phase-0 requests + DONE#1, then
/// phase-1 redo requests + a DONE#2/#3/#4 burst, then (after reading our goodbye) its own final
/// goodbye DONE#5. We (the client-sender) echo DONE after #1, echo after #2, write our own final
/// DONE after #3 (ends our reading loop), write our own goodbye DONE after #4, then read the
/// server's #5 and the stream ends there — nothing more is read or written.
/// </remarks>
/// <remarks>
/// Concurrency mirrors <see cref="PullSession"/>'s P5 fix: the server generator can write many
/// requests before consuming any of our replies, and a reply can require a slow local file read —
/// so per phase, a request-reading loop and a reply-writing loop run concurrently over a bounded
/// <see cref="Channel{T}"/> of <see cref="SumRequest"/> values, instead of read-everything-then-
/// reply-everything, which risks the same mutual-fill deadlock P5 fixed for the pull direction.
/// </remarks>
public static class PushSession
{
    public sealed record Result(
        SessionContext Session,
        int FilesSent,
        int AttributeOnlyReplies,
        long LiteralBytes,
        long MatchedBytes,
        IReadOnlyList<string> FailedFiles,
        IReadOnlyList<string> OversizeFiles,
        IReadOnlyList<ServerMessage> ServerMessages,
        IReadOnlyList<string> DeletedPaths);

    /// <summary>Tags a push client (sender) may legitimately receive. Unlike a pull client, a push
    /// client never receives <see cref="MessageTag.NoSend"/> or <see cref="MessageTag.IoError"/> —
    /// those are things the SENDER emits, and here we are the sender. Under <c>--delete</c> the
    /// remote receiver reports each deletion via <see cref="MessageTag.Deleted"/>
    /// (<c>ssh31-push-delete</c>: MSG_DELETED frames right after the seed; newer peers may also
    /// send an NDX_DEL_STATS block before the final DONE).</summary>
    private static readonly IReadOnlySet<MessageTag> PushClientTags = new HashSet<MessageTag>
    {
        MessageTag.Data,
        MessageTag.ErrorXfer,
        MessageTag.Info,
        MessageTag.Error,
        MessageTag.Warning,
        MessageTag.IoTimeout,
        MessageTag.Noop,
        MessageTag.ErrorExit,
        MessageTag.Deleted,
    };

    /// <param name="entries">
    /// The source tree in whatever order the caller supplies — the flist wire format carries no
    /// ordering requirement (<see cref="FileListWriter"/>). Production wiring passes
    /// <see cref="FileEnumerator"/>'s (already sorted) output; a replay test passes the captured
    /// readdir order instead, so the encoded flist bytes match the capture exactly.
    /// </param>
    public static async Task<Result> RunAsync(
        IRsyncTransport transport,
        ServerArgvBuilder serverArgs,
        IReadOnlyList<EnumeratedEntry> entries,
        CancellationToken cancellationToken = default,
        HandshakeOptions? handshake = null,
        ITransferProgressSink? progress = null)
    {
        progress ??= NullProgressSink.Instance;
        SessionContext session = await HandshakeRunner.RunClientAsync(
            transport.Input, transport.Output, handshake ?? new HandshakeOptions(), cancellationToken);
        if (session.Protocol < 31)
            throw new ProtocolException(RsyncExitCode.ProtocolIncompatibility,
                $"session choreography is implemented for protocol 31, peer negotiated {session.Protocol}");
        if (!session.VarintFlistFlags)
            throw new ProtocolException(RsyncExitCode.ProtocolIncompatibility,
                "peer did not negotiate varint file-list flags (rsync older than 3.2.4); byte-mode file lists are not implemented");

        var reader = new MultiplexReader(transport.Input, PushClientTags);
        var writer = new MultiplexWriter(transport.Output);
        var serverMessages = new List<ServerMessage>();
        reader.MessageReceived = (tag, payload) => serverMessages.Add(new ServerMessage(tag, payload));

        // Filter list: plain push sends NONE (docs/wire-notes.md "Push direction"), but --delete adds
        // the empty filter list int32 0 to the c2s (ssh31-push-delete: "00 00 00 00" precedes the
        // flist). Framed — c2s is multiplexed at protocol >= 30.
        if (serverArgs.Delete)
            writer.Write([0, 0, 0, 0]);

        int digestLength = StrongChecksum.DigestLength(session.TransferChecksum);
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
            ChecksumLength = serverArgs.Checksum ? digestLength : 0,
        };

        // Under --checksum the flist carries a whole-file F_SUM on every regular-file entry
        // (docs/flist-spec.md §14; ssh31-push-checksum). The pure core does no file I/O, so we
        // precompute each sum here (unseeded xxh128, byte-identical to the §3 transfer trailer) and
        // attach it to the entry before FileListWriter emits it.
        IReadOnlyList<FileEntry> wireEntries = serverArgs.Checksum
            ? await ComputeFlistChecksumsAsync(entries, session, cancellationToken)
            : [.. entries.Select(e => e.Wire)];

        FileListWriter.Write(writer, wireEntries, options);
        await writer.FlushAsync(cancellationToken);

        // Both ends sort after receipt; the ndx space is over the SORTED list, not send order
        // (docs/transfer-spec.md / wire-notes.md "Push direction").
        List<EnumeratedEntry> sortedEntries = [.. entries];
        sortedEntries.Sort((a, b) => FileNameComparer.Instance.Compare(a.Wire, b.Wire));

        // Progress denominators: the regular files we may send (an attribute-only or up-to-date reply
        // just means those bytes never move — same short-of-100% quirk as pull, docs/progress-spec.md).
        long progressTotalBytes = 0;
        int progressTotalFiles = 0;
        foreach (EnumeratedEntry entry in sortedEntries)
        {
            if (!entry.Wire.IsRegularFile)
                continue;
            progressTotalBytes += entry.Wire.Size;
            progressTotalFiles++;
        }
        // Scoped so End() runs on every exit (return or throw, including the re-thrown
        // ProtocolException below) — an aborted push must still terminate its in-place progress line.
        using IDisposable progressScope = TransferProgressScope.Started(progress, progressTotalBytes, progressTotalFiles);

        var outbound = new NdxCodec(); // our own reply ndx encoder
        var inbound = new NdxCodec();  // the server-generator's request ndx decoder
        var sender = new ReplySender(reader, writer, outbound, inbound, sortedEntries, session, progress);

        try
        {
            // ---- phase 0: serve every request until the server's DONE#1 ---------------------
            await RunPhaseAsync(sender, cancellationToken);
            WriteNdxDone(writer); // echo#1
            await writer.FlushAsync(cancellationToken);

            // ---- phase 1 (redo): serve any re-requests until the server's DONE#2 -------------
            await RunPhaseAsync(sender, cancellationToken);
            WriteNdxDone(writer); // echo#2

            await SessionSetup.ExpectNdxDoneAsync(reader, cancellationToken); // server's DONE#3 (final)
            WriteNdxDone(writer); // our final#3 — ends the reply side
            await writer.FlushAsync(cancellationToken);

            // A protocol-31 receiver may put NDX_DEL_STATS between DONE#3 and DONE#4 when
            // --delete is active. The 3.4.4 peer emits this marker with non-zero counts; the
            // original 3.4.3 capture did not expose the marker, so a byte-only reader remained
            // untested until this version-matrix gate.
            await ReadDoneAndDeleteStatsAsync(reader, inbound, cancellationToken); // server's DONE#4
            WriteNdxDone(writer); // goodbye#4
            await writer.FlushAsync(cancellationToken);

            // Server's own final goodbye (its DONE#5) — the stream ends exactly here, nothing more
            // is read or written (no stats block on a push, docs/wire-notes.md "Push direction").
            await SessionSetup.ExpectNdxDoneAsync(reader, cancellationToken);
        }
        catch (ProtocolException ex) when (serverMessages.Count > 0)
        {
            // MultiplexReader raises MSG_ERROR_EXIT as a bare ProtocolException carrying only the
            // exit code — no message text (docs/daemon-spec.md §5: a read-only-module push sends
            // MSG_ERROR text frames, then MSG_ERROR_EXIT with the real exit code). Fold whatever
            // server text already landed in serverMessages into the exception so the CLI's
            // "rsyncwin: {ex.Message}" actually shows the server's own words instead of a generic
            // "peer signalled MSG_ERROR_EXIT" line, while keeping the same exit code.
            string serverText = string.Join('\n', serverMessages
                .Where(m => m.Tag is MessageTag.Error or MessageTag.ErrorXfer or MessageTag.Warning)
                .Select(m => m.Text.TrimEnd('\n')));
            throw new ProtocolException(ex.ExitCode, string.IsNullOrEmpty(serverText) ? ex.Message : serverText);
        }

        // MSG_DELETED payloads are the deleted path bytes, with a trailing NUL for directories
        // (ssh31-push-delete). Surface them for the CLI's "deleting <path>" report.
        List<string> deletedPaths =
        [
            .. serverMessages
                .Where(m => m.Tag == MessageTag.Deleted)
                .Select(m => System.Text.Encoding.UTF8.GetString(m.Payload).TrimEnd('\0')),
        ];

        return new Result(
            session,
            sender.FilesSent,
            sender.AttributeOnlyReplies,
            sender.LiteralBytes,
            sender.MatchedBytes,
            sender.FailedFiles,
            sender.OversizeFiles,
            serverMessages,
            deletedPaths);
    }

    /// <summary>
    /// Precomputes the whole-file F_SUM for every regular-file entry under <c>--checksum</c> and
    /// returns the entries with <see cref="FileEntry.FlistChecksum"/> populated (directories and any
    /// non-regular entries pass through untouched — they carry no F_SUM). The sum is the negotiated
    /// transfer checksum computed unseeded over the whole file, byte-identical to the transfer
    /// trailer (docs/transfer-spec.md §3), streamed in chunks so a large source is never fully
    /// buffered just to hash it.
    /// </summary>
    private static async Task<IReadOnlyList<FileEntry>> ComputeFlistChecksumsAsync(
        IReadOnlyList<EnumeratedEntry> entries, SessionContext session, CancellationToken cancellationToken)
    {
        int digestLength = StrongChecksum.DigestLength(session.TransferChecksum);
        var result = new List<FileEntry>(entries.Count);
        byte[] buffer = new byte[RsyncConstants.ChunkSize];
        foreach (EnumeratedEntry entry in entries)
        {
            if (entry.Wire.FileType != FileEntry.RegularFile)
            {
                result.Add(entry.Wire);
                continue;
            }

            byte[] flistSum;
            try
            {
                WholeFileChecksum hasher = StrongChecksum.CreateFileSum(
                    session.TransferChecksum, session.ChecksumSeed, session.Protocol);
                await using FileStream stream = File.OpenRead(entry.AbsolutePath);
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                    hasher.Append(buffer.AsSpan(0, read));

                byte[] sum = new byte[16];
                int length = hasher.Finish(sum);
                flistSum = sum[..length];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A source file locked or vanished between enumeration and this precompute (routine
                // on Windows: open PST/DB/log files). rsync emits a zero F_SUM and carries on — it
                // will mismatch the server's basis, so the file is requested, and the reply path's
                // own read failure (ReplyAsync's catch) records it in FailedFiles → exit 23. Never
                // abort the whole flist over one unreadable file.
                flistSum = new byte[digestLength];
            }

            result.Add(entry.Wire with { FlistChecksum = flistSum });
        }
        return result;
    }

    /// <summary>
    /// True when a source file's length cannot be read as a full-file literal by this build: the
    /// reply path loads the whole file via <see cref="File.ReadAllBytesAsync(string, CancellationToken)"/>,
    /// which is bounded by <see cref="int.MaxValue"/> (streaming a &gt;2 GiB source is deferred, not
    /// implemented here). Extracted as a pure predicate so the size decision is unit-testable without
    /// a real 2 GiB fixture.
    /// </summary>
    internal static bool ExceedsSupportedSize(long fileLength) => fileLength > int.MaxValue;

    private static void WriteNdxDone(MultiplexWriter writer) => writer.Write([0x00]);

    private static async ValueTask ReadDoneAndDeleteStatsAsync(
        MultiplexReader reader, NdxCodec inbound, CancellationToken cancellationToken)
    {
        while (true)
        {
            int ndx = await reader.ReadNdxAsync(inbound, cancellationToken);
            if (ndx == RsyncConstants.NdxDone)
                return;

            if (ndx != RsyncConstants.NdxDelStats)
                throw new InvalidDataException(
                    $"expected NDX_DONE or NDX_DEL_STATS, got {ndx} — session choreography desynced");

            // NDX_DEL_STATS is followed by five rsync varint counters in wire order.  The push
            // result currently surfaces deletion paths via MSG_DELETED; consume the counters here
            // so the subsequent DONE markers remain aligned for every peer version.
            for (int i = 0; i < 5; i++)
                await reader.ReadVarintAsync(cancellationToken);
        }
    }

    private static void WriteNdxAndFlags(MultiplexWriter writer, NdxCodec codec, int ndx, ItemFlags iflags)
    {
        Span<byte> buffer = stackalloc byte[NdxCodec.MaxLength + 2];
        int length = codec.Write(buffer, ndx);
        buffer[length++] = (byte)((ushort)iflags & 0xFF);
        buffer[length++] = (byte)((ushort)iflags >> 8);
        writer.Write(buffer[..length]);
    }

    private const int RequestChannelCapacity = 64;

    /// <summary>
    /// Runs one phase's request reader and reply writer concurrently over a bounded channel of
    /// <see cref="SumRequest"/> values. A fault in either loop cancels a linked token so the other
    /// side cannot hang forever — the request reader waiting on the wire for bytes the server will
    /// never send, or the reply writer waiting on a channel the reader will never post to again.
    /// </summary>
    private static async Task RunPhaseAsync(ReplySender sender, CancellationToken cancellationToken)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Channel<SumRequest> requests = Channel.CreateBounded<SumRequest>(RequestChannelCapacity);

        Task readerTask = sender.ReadRequestsAsync(requests.Writer, cts.Token);
        Task writerTask = sender.WriteRepliesAsync(requests.Reader, cts.Token);
        CancelOnFault(readerTask, cts);
        CancelOnFault(writerTask, cts);

        // Await both to settle before inspecting anything — a task cancelled by our own linked
        // token above (because the OTHER task faulted) must never be the exception that surfaces.
        try { await readerTask; } catch { /* inspected via Task state below */ }
        try { await writerTask; } catch { /* inspected via Task state below */ }

        if (readerTask.IsFaulted)
            throw readerTask.Exception!.GetBaseException();
        if (writerTask.IsFaulted)
            throw writerTask.Exception!.GetBaseException();
    }

    private static void CancelOnFault(Task task, CancellationTokenSource cts) =>
        task.ContinueWith(
            static (t, state) => { if (t.IsFaulted) ((CancellationTokenSource)state!).Cancel(); },
            cts, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>Reads the server-generator's requests for one phase and writes our replies, tracking
    /// running totals across the whole session (one instance lives for the life of the push).</summary>
    private sealed class ReplySender(
        MultiplexReader reader,
        MultiplexWriter writer,
        NdxCodec outbound,
        NdxCodec inbound,
        IReadOnlyList<EnumeratedEntry> sortedEntries,
        SessionContext session,
        ITransferProgressSink progress)
    {
        private readonly int _digestLength = StrongChecksum.DigestLength(session.TransferChecksum);
        private readonly List<string> _failedFiles = [];
        private readonly List<string> _oversizeFiles = [];

        public int FilesSent { get; private set; }
        public int AttributeOnlyReplies { get; private set; }
        public long LiteralBytes { get; private set; }
        public long MatchedBytes { get; private set; }
        public IReadOnlyList<string> FailedFiles => _failedFiles;
        public IReadOnlyList<string> OversizeFiles => _oversizeFiles;

        /// <summary>Reads requests off the wire for one phase, posting each to <paramref name="output"/>
        /// (including the terminating <see cref="SumRequest.Done"/> marker) so the concurrently
        /// running reply writer can start serving them without waiting for the whole phase to be
        /// read first.</summary>
        public async Task ReadRequestsAsync(ChannelWriter<SumRequest> output, CancellationToken cancellationToken)
        {
            while (true)
            {
                SumRequest request = await SumRequestReader.ReadAsync(reader, inbound, _digestLength, cancellationToken);
                if (!request.IsDone && (request.Ndx < 0 || request.Ndx >= sortedEntries.Count))
                    throw new InvalidDataException(
                        $"push: server requested out-of-range ndx {request.Ndx} (list has {sortedEntries.Count} entries)");

                await output.WriteAsync(request, cancellationToken);
                if (request.IsDone)
                {
                    output.Complete();
                    return;
                }
            }
        }

        /// <summary>Drains <paramref name="input"/> and writes one reply per request, stopping at
        /// the phase's <see cref="SumRequest.Done"/> marker (the caller writes the actual DONE echo
        /// byte itself, once per phase, after this method returns).</summary>
        public async Task WriteRepliesAsync(ChannelReader<SumRequest> input, CancellationToken cancellationToken)
        {
            await foreach (SumRequest request in input.ReadAllAsync(cancellationToken))
            {
                if (request.IsDone)
                    return;

                await ReplyAsync(request, cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }
        }

        private async Task ReplyAsync(SumRequest request, CancellationToken cancellationToken)
        {
            EnumeratedEntry entry = sortedEntries[request.Ndx];

            if (!request.Iflags.HasFlag(ItemFlags.Transfer))
            {
                // Attribute-only: ndx + iflags echo only, nothing follows (docs/transfer-spec.md §1).
                WriteNdxAndFlags(writer, outbound, request.Ndx, request.Iflags);
                AttributeOnlyReplies++;
                return;
            }

            long length;
            try
            {
                length = new FileInfo(entry.AbsolutePath).Length;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _failedFiles.Add(entry.Wire.Name);
                return;
            }

            if (ExceedsSupportedSize(length))
            {
                // Streaming reads are deferred (P7 scope); File.ReadAllBytesAsync below is bounded
                // by int.MaxValue, and its IOException for an oversize file is indistinguishable
                // from a vanished one — check up front so the reported reason stays honest instead
                // of misfiling a >2 GiB source as "vanished".
                _oversizeFiles.Add(entry.Wire.Name);
                _failedFiles.Add(entry.Wire.Name);
                return;
            }

            byte[] source;
            try
            {
                source = await File.ReadAllBytesAsync(entry.AbsolutePath, cancellationToken);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Capture-unpinned but interop-verified: no push vector exercises a vanished/
                // unreadable source file at reply time. Canonical rsync's real sender emits mux
                // MSG_NO_SEND here and continues (docs/transfer-spec.md §1), but that exact shape
                // has never been observed push-side in our own captures. Rather than guess at an
                // unverified wire message, we take the documented non-aborting fallback: send
                // nothing for this ndx and record a failure — a vanished source is not a reason to
                // abort the whole session. SshPushInteropTests.Push_SourceFileVanishesMidSession
                // confirms a real rsync generator completes cleanly under this behavior.
                _failedFiles.Add(entry.Wire.Name);
                return;
            }

            // Push reports per file (one 0→100% step): the whole source is read and matched before
            // any token goes out, so there is no intra-file byte cursor to animate (docs/progress-spec.md).
            progress.BeginFile(entry.Wire.Name, length);

            WriteNdxAndFlags(writer, outbound, request.Ndx, request.Iflags);

            SumHeader head = request.SumHead!.Value;
            Span<byte> headBytes = stackalloc byte[SumHeader.Size];
            head.Write(headBytes); // verbatim echo — never recomputed (docs/transfer-spec.md §1)
            writer.Write(headBytes);

            List<BlockSignature> blockSums = [.. request.BlockSums!.Select(b => new BlockSignature(b.WeakSum, b.StrongSum))];
            MatchResult match = MatchSearcher.Search(
                writer, source, head, blockSums, session.TransferChecksum, session.ChecksumSeed,
                session.ChecksumSeedFix, session.Compression);

            // Whole-file trailer: "the entire new file content in output order" (docs/transfer-spec.md
            // §3) is, for the sender, simply its own source bytes — a matched block's content is by
            // definition identical to the source at that position, or it would not have matched — so
            // hashing the source directly is equivalent to hashing the token stream's reconstruction,
            // without re-walking it.
            WholeFileChecksum hasher = StrongChecksum.CreateFileSum(session.TransferChecksum, session.ChecksumSeed, session.Protocol);
            hasher.Append(source);
            Span<byte> trailer = stackalloc byte[16];
            int trailerLength = hasher.Finish(trailer);
            writer.Write(trailer[..trailerLength]);

            FilesSent++;
            LiteralBytes += match.LiteralBytes;
            MatchedBytes += match.MatchedBytes;

            progress.Advance(length);
            progress.EndFile();
        }
    }
}
