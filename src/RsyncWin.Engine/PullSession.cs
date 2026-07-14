using System.Buffers.Binary;
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
/// A full pull: handshake, flist, generator requests, file reception with whole-file verification
/// and the one-retry redo pass, ending in the measured protocol-31 goodbye. Every transferred
/// file lands via a temp file + rename, so a failed verification never corrupts the destination.
/// </summary>
/// <remarks>
/// Phase choreography pinned by capture (<c>docs/wire-notes.md</c> P4 section): we send phase-0
/// requests + DONE#1, read replies until the sender's echo #1 (collecting checksum failures),
/// send redo requests + the DONE#2/#3/#4 burst, read redo replies until echo #2, then the
/// sender's final DONE, stats, its goodbye DONE, and answer with goodbye DONE#5.
/// </remarks>
/// <remarks>
/// P5: the request writer and the reply reader run as two concurrent loops per phase, joined with
/// <see cref="Task.WhenAll(Task, Task)"/> and handed off through a bounded <see cref="Channel{T}"/>
/// of requested ndx values — this removes the old write-everything-then-read-everything shape,
/// which could deadlock both pipe buffers on a very large flist (<c>docs/wire-notes.md</c> open
/// questions, P5). The demuxed logical byte stream is unchanged; only frame timing differs.
/// </remarks>
/// <remarks>
/// P5 mtime+size fast path: a regular file whose local size and mtime already match the flist
/// entry is never requested — zero ndx, zero iflags, zero sum head (<c>docs/wire-notes.md</c>,
/// vectors <c>ssh31-pull-uptodate</c> / <c>ssh31-pull-partial</c>). A file that exists but differs
/// gets <see cref="ItemFlags.Transfer"/> plus <see cref="ItemFlags.ReportSize"/> /
/// <see cref="ItemFlags.ReportTime"/> for whichever field(s) differ — no <see cref="ItemFlags.IsNew"/>,
/// which is reserved for files missing locally.
/// </remarks>
public static class PullSession
{
    public sealed record Result(
        SessionContext Session,
        IReadOnlyList<FileEntry> Entries,
        int TransferredFiles,
        long TransferredBytes,
        long MatchedBytes,
        IReadOnlyList<string> RedoneFiles,
        IReadOnlyList<string> FailedFiles,
        IReadOnlyList<(string Name, string Reason)> SkippedNonRegular,
        IReadOnlyList<string> MappedNames,
        IReadOnlyList<string> NotSentByServer,
        int IoErrorFlags,
        SessionStats Stats,
        IReadOnlyList<ServerMessage> ServerMessages);

    public static async Task<Result> RunAsync(
        IRsyncTransport transport,
        ServerArgvBuilder serverArgs,
        string destinationDirectory,
        CancellationToken cancellationToken = default,
        HandshakeOptions? handshake = null)
    {
        SessionChannel channel = await SessionSetup.OpenAsync(transport, serverArgs, cancellationToken, handshake);
        IReadOnlyList<FileEntry> entries = channel.FileList.Entries;

        // Map every name before touching the filesystem: one hostile entry anywhere in the list
        // must abort the session with nothing created. Also note which names the Windows mapper
        // actually rewrote, for a warn-and-continue report to the caller.
        //
        // WindowsPathMapper is many-to-one (e.g. "a:b" and "a_b" both map to "a_b"; case differs
        // on case-insensitive NTFS too): a later entry whose mapped path collides with an earlier
        // one must not clobber it. Flist order is authoritative — the first entry wins, the
        // colliding one is excluded from the transfer entirely (never requested downstream), and
        // any descendant of an excluded directory is excluded too, since its mapped path would
        // otherwise land under the wrong (winning) directory rather than a nonexistent one.
        var mappedNames = new List<string>();
        var skipped = new List<(string Name, string Reason)>();
        var excluded = new HashSet<int>();
        var pathOwners = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var excludedDirPrefixes = new List<string>();
        for (int ndx = 0; ndx < entries.Count; ndx++)
        {
            FileEntry entry = entries[ndx];
            string full = LocalPath(destinationDirectory, entry, out bool changed);
            if (changed)
                mappedNames.Add(entry.Name);

            if (excludedDirPrefixes.Exists(prefix => entry.Name.StartsWith(prefix, StringComparison.Ordinal)))
            {
                excluded.Add(ndx);
                skipped.Add((entry.Name, "parent directory excluded due to a name collision"));
                if (entry.IsDirectory)
                    excludedDirPrefixes.Add(entry.Name + "/");
                continue;
            }

            if (pathOwners.TryGetValue(full, out int ownerNdx))
            {
                excluded.Add(ndx);
                skipped.Add((entry.Name, $"name collision with \"{entries[ownerNdx].Name}\""));
                if (entry.IsDirectory)
                    excludedDirPrefixes.Add(entry.Name + "/");
            }
            else
            {
                pathOwners[full] = ndx;
            }
        }
        Directory.CreateDirectory(destinationDirectory);

        var outbound = new NdxCodec();   // one encoder and one decoder per direction, whole session
        var inbound = new NdxCodec();
        var receiver = new ReplyReceiver(channel, inbound, destinationDirectory);

        // ---- phase 0: walk the sorted list, create dirs, request every out-of-date file -------
        // Request-writing and reply-reading run as two concurrent loops, coordinated through a
        // bounded channel of requested ndx values so the reader's expectation set grows as
        // requests go out, instead of only after all of them have been written.
        List<int> redo = await RunPhaseAsync(
            (requestedNdx, ct) => WritePhase0RequestsAsync(
                channel.Writer, outbound, entries, destinationDirectory, channel.Session,
                skipped, excluded, requestedNdx, ct),
            (requestedNdx, ct) => receiver.ReceiveUntilDoneAsync(requestedNdx, ct),
            cancellationToken);

        // ---- phase 1 (redo) + the measured DONE#2/#3/#4 burst ---------------------------------
        List<int> failed = await RunPhaseAsync(
            (requestedNdx, ct) => WriteRedoRequestsAsync(
                channel.Writer, outbound, entries, destinationDirectory, channel.Session, redo, requestedNdx, ct),
            (requestedNdx, ct) => receiver.ReceiveUntilDoneAsync(requestedNdx, ct),
            cancellationToken);

        // ---- tail: sender's final DONE, stats, goodbye exchange -------------------------------
        await SessionSetup.ExpectNdxDoneAsync(channel.Reader, cancellationToken);
        SessionStats stats = await SessionSetup.ReadStatsAsync(channel.Reader, cancellationToken);
        await SessionSetup.ExpectNdxDoneAsync(channel.Reader, cancellationToken);
        WriteNdxDone(channel.Writer);
        await channel.Writer.FlushAsync(cancellationToken);

        // Directory mtimes last, deepest first — the file writes above kept bumping them.
        foreach (FileEntry dir in entries.Where((e, i) => e.IsDirectory && !excluded.Contains(i))
                     .OrderByDescending(e => e.NameBytes.Count(b => b == (byte)'/')))
        {
            Directory.SetLastWriteTimeUtc(
                LocalPath(destinationDirectory, dir), ClampedMtimeUtc(dir.ModifiedUnixSeconds));
        }

        int ioErrorFlags = channel.FileList.IoError;
        var notSent = new List<string>();
        foreach (ServerMessage message in channel.ServerMessages)
        {
            if (message.Tag == MessageTag.NoSend && message.Payload.Length == 4)
            {
                // The payload is server-controlled — an out-of-range ndx must not crash the report.
                int ndx = BinaryPrimitives.ReadInt32LittleEndian(message.Payload);
                notSent.Add(ndx >= 0 && ndx < entries.Count ? entries[ndx].Name : $"#{ndx}");
            }
            else if (message.Tag == MessageTag.IoError && message.Payload.Length == 4)
                ioErrorFlags |= BinaryPrimitives.ReadInt32LittleEndian(message.Payload);
        }

        return new Result(
            channel.Session,
            entries,
            receiver.TransferredFiles,
            receiver.TransferredBytes,
            receiver.MatchedBytes,
            [.. redo.Select(n => entries[n].Name)],
            [.. failed.Select(n => entries[n].Name)],
            skipped,
            mappedNames,
            notSent,
            ioErrorFlags,
            stats,
            channel.ServerMessages);
    }

    /// <summary>
    /// Maps a wire name under the destination. <see cref="FileListReader"/> enforced the Unix-side
    /// rules (no leading '/', no '..' components split on '/'), but '\' and ':' are path syntax
    /// only on Windows — <c>..\evil</c> or <c>C:evil</c> passes that validation and still escapes
    /// through <see cref="Path.Combine(string[])"/>. <see cref="WindowsPathMapper"/> sanitizes each
    /// segment; the containment check below is kept regardless, as the real backstop against a
    /// mapper bug (or a bare ".." that reached here despite the reader's rejection).
    /// </summary>
    internal static string LocalPath(string destination, FileEntry entry) =>
        LocalPath(destination, entry, out _);

    internal static string LocalPath(string destination, FileEntry entry, out bool changed)
    {
        if (entry.NameBytes is [(byte)'.'])
        {
            changed = false;
            return destination;
        }

        string name = entry.Name;
        (string mapped, changed) = WindowsPathMapper.Map(name);

        string full = Path.GetFullPath(Path.Combine(destination, mapped));
        string root = Path.GetFullPath(destination);
        if (!Path.EndsInDirectorySeparator(root))
            root += Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ProtocolException(RsyncExitCode.UnsupportedAction,
                $"refusing server-sent name that escapes the destination: \"{name}\"");
        return full;
    }

    /// <summary>
    /// Win32 FileTime starts at 1601 and <see cref="DateTimeOffset"/> ends at year 9999; rsync's
    /// mtime varlong is wider on both ends. A peer's bogus timestamp clamps instead of throwing
    /// out of the session.
    /// </summary>
    internal static DateTime ClampedMtimeUtc(long unixSeconds)
    {
        const long minSettable = -11_644_473_599; // 1601-01-01T00:00:01Z — FILETIME 0 means "don't change"
        const long maxSettable = 253_402_300_799; // 9999-12-31T23:59:59Z — DateTimeOffset's ceiling
        return DateTimeOffset.FromUnixTimeSeconds(Math.Clamp(unixSeconds, minSettable, maxSettable)).UtcDateTime;
    }

    /// <summary>
    /// The entry's mtime as it actually landed on disk: local files are written via
    /// <see cref="ClampedMtimeUtc"/>, so an out-of-range wire mtime must be compared against the
    /// clamped value, not the raw one, or the fast path never matches and the file re-transfers
    /// every run forever.
    /// </summary>
    private static long ClampedMtimeUnixSeconds(long unixSeconds) =>
        new DateTimeOffset(ClampedMtimeUtc(unixSeconds)).ToUnixTimeSeconds();

    private static void WriteNdxAndFlags(MultiplexWriter writer, NdxCodec codec, int ndx, ItemFlags iflags)
    {
        Span<byte> buffer = stackalloc byte[NdxCodec.MaxLength + 2];
        int length = codec.Write(buffer, ndx);
        buffer[length++] = (byte)((ushort)iflags & 0xFF);
        buffer[length++] = (byte)((ushort)iflags >> 8);
        writer.Write(buffer[..length]);
    }

    /// <summary>A full-transfer request (no basis, or one we couldn't open): iflags as given, plus
    /// the all-zero sum head ("send me everything"). For a missing file this is exactly the
    /// captured request shape 01 00 A0 + 16 zeros (ITEM_TRANSFER|ITEM_IS_NEW).</summary>
    private static void WriteTransferRequest(MultiplexWriter writer, NdxCodec codec, int ndx, ItemFlags iflags)
    {
        WriteNdxAndFlags(writer, codec, ndx, iflags);
        Span<byte> nullHead = stackalloc byte[SumHeader.Size];
        SumHeader.Null.Write(nullHead);
        writer.Write(nullHead);
    }

    private static void WriteNdxDone(MultiplexWriter writer) => writer.Write([0x00]);

    /// <summary>
    /// The mtime+size fast path (P5): decides whether a regular-file entry needs to be requested
    /// at all, and with which iflags, without touching the network. A local file that already
    /// matches the entry's size and mtime is skipped entirely — verified by capture
    /// (<c>ssh31-pull-uptodate</c>) to produce zero generator bytes for such files.
    /// </summary>
    /// <returns>False when the file is already up to date and must not be requested.</returns>
    internal static bool TryComputeRequestIflags(FileEntry entry, string destination, out ItemFlags iflags)
    {
        string path = LocalPath(destination, entry);
        if (!File.Exists(path))
        {
            // Missing locally: the existing P4 shape (new file, no basis).
            iflags = ItemFlags.Transfer | ItemFlags.IsNew;
            return true;
        }

        var info = new FileInfo(path);
        bool sizeDiffers = info.Length != entry.Size;
        // Our own writes are exact-second (FileSetLastWriteTimeUtc floors sub-second parts away),
        // so a plain unix-seconds comparison is clean.
        long localMtime = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeSeconds();
        bool mtimeDiffers = localMtime != ClampedMtimeUnixSeconds(entry.ModifiedUnixSeconds);

        if (!sizeDiffers && !mtimeDiffers)
        {
            iflags = ItemFlags.None;
            return false;
        }

        iflags = ItemFlags.Transfer;
        if (sizeDiffers)
            iflags |= ItemFlags.ReportSize;
        if (mtimeDiffers)
            iflags |= ItemFlags.ReportTime;
        return true;
    }

    /// <summary>
    /// The directory half of the mtime fast path (P5): creates the directory if it is missing,
    /// then decides whether it needs an itemize echo at all. A directory that already exists with
    /// the entry's exact mtime gets none — verified by capture (<c>ssh31-pull-uptodate</c>: the
    /// destination tree there was a preserved copy, and the whole session produced zero generator
    /// bytes, dirs included). One that is freshly created is <see cref="ItemFlags.IsNew"/>; one that
    /// existed but whose mtime differs is <see cref="ItemFlags.ReportTime"/> only — this is also
    /// what the transfer root itself gets in every other capture, since <see cref="RunAsync"/>
    /// always pre-creates the destination directory before this loop runs.
    /// </summary>
    /// <returns>False when the directory is already up to date and needs no itemize echo.</returns>
    private static bool TryComputeDirectoryIflags(FileEntry entry, string destination, out ItemFlags iflags)
    {
        string path = LocalPath(destination, entry);
        bool existed = Directory.Exists(path);
        if (!existed)
        {
            Directory.CreateDirectory(path);
            iflags = ItemFlags.IsNew | ItemFlags.LocalChange;
            return true;
        }

        long currentMtime = new DateTimeOffset(Directory.GetLastWriteTimeUtc(path), TimeSpan.Zero).ToUnixTimeSeconds();
        if (currentMtime == ClampedMtimeUnixSeconds(entry.ModifiedUnixSeconds))
        {
            iflags = ItemFlags.None;
            return false;
        }

        iflags = ItemFlags.ReportTime;
        return true;
    }

    /// <summary>
    /// Phase-0 request writer: walks the sorted flist, creates directories, and requests every
    /// regular file that fails the fast path — posting each requested ndx to <paramref name="requestedNdx"/>
    /// as it goes so the concurrently running reply reader can grow its expectation set without
    /// waiting for every request to be written first.
    /// </summary>
    private static async Task WritePhase0RequestsAsync(
        MultiplexWriter writer,
        NdxCodec codec,
        IReadOnlyList<FileEntry> entries,
        string destinationDirectory,
        SessionContext session,
        List<(string Name, string Reason)> skipped,
        HashSet<int> excluded,
        ChannelWriter<int> requestedNdx,
        CancellationToken cancellationToken)
    {
        for (int ndx = 0; ndx < entries.Count; ndx++)
        {
            if (excluded.Contains(ndx))
                continue; // name collision loser (or its descendant) — never touched downstream

            FileEntry entry = entries[ndx];
            if (entry.IsDirectory)
            {
                if (TryComputeDirectoryIflags(entry, destinationDirectory, out ItemFlags dirIflags))
                    WriteNdxAndFlags(writer, codec, ndx, dirIflags);
                // else: mtime already matches — no itemize echo, mirroring the up-to-date capture
                // where the whole tree (dirs included) produces zero generator bytes.
            }
            else if (entry.IsRegularFile)
            {
                if (!TryComputeRequestIflags(entry, destinationDirectory, out ItemFlags iflags))
                    continue; // already up to date — no request, no ndx, nothing on the wire

                await WriteRegularFileRequestAsync(
                    writer, codec, ndx, iflags, entry, destinationDirectory, session,
                    s2Length: null, cancellationToken);
                await writer.FlushAsync(cancellationToken);
                await requestedNdx.WriteAsync(ndx, cancellationToken);
            }
            else
            {
                // symlinks/devices land in a later phase
                skipped.Add((entry.Name, entry.IsSymlink ? "symlink" : "not a regular file"));
            }
        }
        WriteNdxDone(writer);
        await writer.FlushAsync(cancellationToken);
        requestedNdx.Complete();
    }

    /// <summary>
    /// P6: builds a real signature request (sum head + block sums) for a regular file that already
    /// exists locally, instead of P4's all-zero "send everything" head. Opens the current on-disk
    /// bytes as a basis via <see cref="BasisFileStore"/>, generates the signature over them, then
    /// closes the handle again immediately — <see cref="ReplyReceiver.ReceiveIntoFileAsync"/> opens
    /// its own basis handle later for reconstruction (reopen-in-receiver design, see there), so the
    /// two never overlap and both see identical bytes as long as nothing else writes the file
    /// meanwhile, which holds here (single-threaded request loop, destination untouched until the
    /// reply for this very ndx is received).
    /// </summary>
    /// <param name="iflags">Already excludes <see cref="ItemFlags.IsNew"/> for any file with a basis
    /// to sign; a missing file (that flag set) always falls straight through to a full transfer.</param>
    /// <param name="s2Length">Null for the phase-0 derived truncation; the redo caller passes
    /// <c>MIN(16, xfer_sum_len)</c> per <c>docs/transfer-spec.md</c> §6.</param>
    private static async Task WriteRegularFileRequestAsync(
        MultiplexWriter writer,
        NdxCodec codec,
        int ndx,
        ItemFlags iflags,
        FileEntry entry,
        string destinationDirectory,
        SessionContext session,
        int? s2Length,
        CancellationToken cancellationToken)
    {
        if (iflags.HasFlag(ItemFlags.IsNew))
        {
            // Missing locally: no basis is possible, full transfer regardless of phase.
            WriteTransferRequest(writer, codec, ndx, iflags);
            return;
        }

        string path = LocalPath(destinationDirectory, entry);
        FileStream? basis = BasisFileStore.Open(path);
        if (basis is null)
        {
            // Locked or vanished between the fast-path check and here: fall back to a full
            // transfer rather than failing the whole session over one file.
            WriteTransferRequest(writer, codec, ndx, iflags);
            return;
        }

        SignatureResult signature;
        await using (basis)
        {
            signature = await SignatureGenerator.GenerateAsync(
                basis, session.TransferChecksum, session.ChecksumSeed, session.ChecksumSeedFix,
                s2Length: s2Length, cancellationToken: cancellationToken);
        }

        WriteNdxAndFlags(writer, codec, ndx, iflags);
        writer.Write(signature.Wire);
    }

    /// <summary>
    /// Phase-1 (redo) request writer: re-requests every ndx the sender reported a checksum
    /// mismatch for, then the measured DONE#2/#3/#4 burst that closes this phase. Same iflags
    /// computation as phase 0 (capture-pinned: the redo request is indistinguishable in shape,
    /// e.g. <c>0x8008</c> again for a mtime-only mismatch) but a full-length s2length — the basis
    /// on disk is unchanged, since a failed phase-0 receive discards its temp file and never
    /// touches the destination.
    /// </summary>
    private static async Task WriteRedoRequestsAsync(
        MultiplexWriter writer,
        NdxCodec codec,
        IReadOnlyList<FileEntry> entries,
        string destinationDirectory,
        SessionContext session,
        List<int> redo,
        ChannelWriter<int> requestedNdx,
        CancellationToken cancellationToken)
    {
        int redoS2Length = Math.Min(16, StrongChecksum.DigestLength(session.TransferChecksum));
        foreach (int ndx in redo)
        {
            FileEntry entry = entries[ndx];
            if (!TryComputeRequestIflags(entry, destinationDirectory, out ItemFlags iflags))
                iflags = ItemFlags.Transfer; // still redoing even if a race made this look up to date

            await WriteRegularFileRequestAsync(
                writer, codec, ndx, iflags, entry, destinationDirectory, session,
                s2Length: redoS2Length, cancellationToken);
            await writer.FlushAsync(cancellationToken);
            await requestedNdx.WriteAsync(ndx, cancellationToken);
        }
        WriteNdxDone(writer);
        WriteNdxDone(writer);
        WriteNdxDone(writer);
        await writer.FlushAsync(cancellationToken);
        requestedNdx.Complete();
    }

    private const int RequestChannelCapacity = 64;

    /// <summary>
    /// Runs one phase's request writer and reply reader concurrently over a bounded ndx channel.
    /// A writer fault cancels the reader (which may otherwise wait forever for more wire input);
    /// the reader finishing at all — success <em>or</em> failure — cancels the writer (which may
    /// otherwise be parked forever on a full channel), but the writer's own normal completion
    /// never cancels the reader, which still needs to keep reading until it sees the sender's
    /// DONE. If the reader sees DONE while the writer has not itself finished writing every
    /// request, the sender sent DONE early — a protocol violation, surfaced as
    /// <see cref="InvalidDataException"/> instead of silently returning as if the phase succeeded.
    /// </summary>
    private static async Task<List<int>> RunPhaseAsync(
        Func<ChannelWriter<int>, CancellationToken, Task> writeRequestsAsync,
        Func<ChannelReader<int>, CancellationToken, Task<List<int>>> receiveRepliesAsync,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Channel<int> requested = Channel.CreateBounded<int>(RequestChannelCapacity);

        Task writer = writeRequestsAsync(requested.Writer, cts.Token);
        Task<List<int>> reader = receiveRepliesAsync(requested.Reader, cts.Token);
        CancelOnFault(writer, cts);
        CancelOnCompletion(reader, cts);

        // Await both to settle before inspecting anything — a writer OperationCanceledException
        // triggered by our own cancellation above must never be the exception that surfaces here.
        try { await writer; } catch { /* inspected via Task state below, not rethrown here */ }
        try { await reader; } catch { /* inspected via Task state below, not rethrown here */ }

        Exception? readerFault = reader.IsFaulted ? reader.Exception!.GetBaseException() : null;
        Exception? writerFault = writer.IsFaulted ? writer.Exception!.GetBaseException() : null;
        bool writerFaultIsGenuine = writerFault is not null and not OperationCanceledException;

        // A genuine reader fault always wins outright; a genuine writer fault beats a reader that
        // only saw its own read cancelled out from under it as a side effect of that writer fault.
        if (readerFault is not null && !(readerFault is OperationCanceledException && writerFaultIsGenuine))
            throw readerFault;
        if (writerFaultIsGenuine)
            throw writerFault!;
        if (readerFault is not null)
            throw readerFault;

        // Reader completed normally (saw DONE). If the writer has not also finished — it was
        // still parked writing requests and only unblocked via our deliberate cancellation above
        // — the sender sent DONE before we finished asking: a protocol violation, not success.
        if (writer.Status != TaskStatus.RanToCompletion)
            throw new InvalidDataException(
                "transfer: sender sent DONE before the request writer finished writing requests — stream is desynced");

        // Clean completion: the writer already called Complete(), so draining whatever is left
        // on the channel here cannot unblock anything — unlike draining on DONE from inside the
        // reader itself, which would race with the premature-DONE detection above by freeing
        // channel space for a writer that is deliberately still meant to look stuck. Anything
        // still sitting here was requested but the reader never got a matching reply for it (no
        // reply ever pulled it out of the channel via ConsumeExpectedAsync's own lazy draining).
        List<int> mismatched = reader.Result;
        while (requested.Reader.TryRead(out int neverAnswered))
            mismatched.Add(neverAnswered);
        return mismatched;
    }

    private static void CancelOnFault(Task task, CancellationTokenSource cts) =>
        task.ContinueWith(
            static (t, state) => { if (t.IsFaulted) ((CancellationTokenSource)state!).Cancel(); },
            cts, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Cancels <paramref name="cts"/> when <paramref name="task"/> completes — success or
    /// failure alike — so a writer parked on a full/blocked channel unblocks as soon as the
    /// reader is done with it, instead of only on a fault.
    /// </summary>
    private static void CancelOnCompletion(Task task, CancellationTokenSource cts) =>
        task.ContinueWith(
            static (_, state) => ((CancellationTokenSource)state!).Cancel(),
            cts, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>Reads sender replies until an NDX_DONE echo, writing files as they stream in.</summary>
    private sealed class ReplyReceiver(SessionChannel channel, NdxCodec inbound, string destination)
    {
        // Owned solely by ReceiveUntilDoneAsync — one phase runs at a time, never concurrently
        // with itself, so no synchronization is needed beyond the channel handoff below.
        private readonly HashSet<int> _expected = new();

        public int TransferredFiles { get; private set; }
        public long TransferredBytes { get; private set; }
        public long MatchedBytes { get; private set; }

        /// <summary>
        /// Reads replies for one phase, growing the expectation set from <paramref name="requestedNdx"/>
        /// as needed instead of requiring it to be fully populated up front. This runs concurrently
        /// with the phase's request writer (joined via <c>Task.WhenAll</c> by the caller); a reply for
        /// an ndx not yet seen on the channel simply waits for more requests to arrive rather than
        /// failing immediately, since the writer may not have posted it yet.
        /// </summary>
        public async Task<List<int>> ReceiveUntilDoneAsync(
            ChannelReader<int> requestedNdx, CancellationToken cancellationToken)
        {
            var mismatched = new List<int>();
            while (true)
            {
                int ndx = await channel.Reader.ReadNdxAsync(inbound, cancellationToken);
                if (ndx == RsyncConstants.NdxDone)
                {
                    // Any ndx already pulled into the expectation set — while draining to match
                    // some other reply — was requested but never answered and never explained by
                    // MSG_NO_SEND; fold it into this phase's returned list so it gets a redo (or
                    // becomes a real failure after the redo phase) instead of silently vanishing
                    // as success. Deliberately NOT draining the channel itself here: doing so
                    // would free channel space and could let a writer that is still genuinely
                    // stuck (a premature/early DONE — RunPhaseAsync's job to detect) make illegal
                    // further progress instead of staying stuck for that check. RunPhaseAsync
                    // drains any remainder itself, once it has confirmed the writer already
                    // finished and draining is therefore safe.
                    mismatched.AddRange(_expected);
                    _expected.Clear();
                    return mismatched;
                }

                ItemFlags iflags = await channel.Reader.ReadItemFlagsAsync(cancellationToken);
                if (iflags.HasFlag(ItemFlags.BasisTypeFollows))
                    await channel.Reader.ReadDataByteAsync(cancellationToken);
                if (iflags.HasFlag(ItemFlags.XnameFollows))
                    await ReadVstringAsync(channel.Reader, cancellationToken);
                if (!iflags.HasFlag(ItemFlags.Transfer))
                    continue; // attribute-only echo

                if (ndx < 0 || ndx >= channel.FileList.Entries.Count
                    || !await ConsumeExpectedAsync(ndx, requestedNdx, cancellationToken))
                    throw new InvalidDataException(
                        $"transfer: sender replied with unrequested ndx {ndx} — stream is desynced");

                FileEntry entry = channel.FileList.Entries[ndx];
                if (!await ReceiveIntoFileAsync(entry, cancellationToken))
                    mismatched.Add(ndx);
            }
        }

        /// <summary>
        /// True and removes <paramref name="ndx"/> once it has been seen either in the local
        /// expectation set or (after draining) on <paramref name="requestedNdx"/>. False only once
        /// the channel has completed and drained without ever producing it — a genuine desync.
        /// </summary>
        private async Task<bool> ConsumeExpectedAsync(
            int ndx, ChannelReader<int> requestedNdx, CancellationToken cancellationToken)
        {
            while (!_expected.Remove(ndx))
            {
                if (!await requestedNdx.WaitToReadAsync(cancellationToken))
                    return false; // writer is done and never posted this ndx
                while (requestedNdx.TryRead(out int posted))
                    _expected.Add(posted);
            }
            return true;
        }

        private async Task<bool> ReceiveIntoFileAsync(FileEntry entry, CancellationToken cancellationToken)
        {
            string finalPath = LocalPath(destination, entry);
            string tempFileName = $".{Path.GetFileName(finalPath)}.{Guid.NewGuid().ToString("N")[..8]}.rsyncwin-tmp";
            string tempPath = Path.Combine(Path.GetDirectoryName(finalPath)!, tempFileName);

            try
            {
                // Reopen-in-receiver design (see WriteRegularFileRequestAsync): the request writer
                // already closed its own basis handle right after signing it, so opening a fresh one
                // here targets whatever is on disk *now*, which may differ from what was signed if
                // the file was deleted/locked/truncated in between. A null (or short) basis here is
                // survivable, not a protocol error: FileReceiver zero-fills the missing bytes for any
                // block-reference token (matching rsync's map_ptr behavior for a changed file), which
                // makes the whole-file checksum trailer below mismatch and routes this file through
                // the normal redo path — the next phase re-signs from the current on-disk state (or
                // sends a full-transfer head if the file is gone). Only a genuinely hostile/desynced
                // reply (out-of-range block index, or a block reference inside an actual
                // full-transfer request) still throws out of FileReceiver.
                FileReceiveResult received;
                FileStream? basis = BasisFileStore.Open(finalPath);
                try
                {
                    await using (var temp = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                    {
                        received = await FileReceiver.ReceiveAsync(
                            channel.Reader,
                            temp,
                            channel.Session.TransferChecksum,
                            channel.Session.ChecksumSeed,
                            channel.Session.Protocol,
                            StrongChecksum.DigestLength(channel.Session.TransferChecksum),
                            cancellationToken,
                            basis);
                    }
                }
                finally
                {
                    // Must close before the move below: on Windows, File.Move(temp, final,
                    // overwrite: true) throws UnauthorizedAccessException while ANY handle — even
                    // FileShare.Delete — is still open on `final` (measured, BasisFileStoreTests).
                    // Dispose-then-move keeps the existing overwrite-move path working, so no
                    // delete-then-rename switch is needed here.
                    if (basis is not null)
                        await basis.DisposeAsync();
                }

                if (!received.ChecksumMatches)
                {
                    // Redo semantics: discard the update, leave whatever was there before, retry
                    // in the next phase. A second failure is the caller's exit-23 signal.
                    return false;
                }

                try
                {
                    // rsync's contract replaces read-only destinations; File.Move alone refuses.
                    if (File.Exists(finalPath))
                    {
                        FileAttributes attributes = File.GetAttributes(finalPath);
                        if ((attributes & FileAttributes.ReadOnly) != 0)
                            File.SetAttributes(finalPath, attributes & ~FileAttributes.ReadOnly);
                    }
                    File.Move(tempPath, finalPath, overwrite: true);
                    File.SetLastWriteTimeUtc(finalPath, ClampedMtimeUtc(entry.ModifiedUnixSeconds));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // The token stream is fully consumed by here, so a local finalize failure is
                    // per-file, not fatal: fall into the same retry-then-exit-23 path a checksum
                    // mismatch takes.
                    return false;
                }

                TransferredFiles++;
                TransferredBytes += received.BytesWritten;
                MatchedBytes += received.MatchedBytes;
                return true;
            }
            finally
            {
                // Success moved it into place; no other path may leak the temp. A failure while
                // creating the temp itself still aborts the session above — the token stream was
                // never consumed, so there is nothing to resynchronize to.
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        private static async ValueTask ReadVstringAsync(MultiplexReader reader, CancellationToken cancellationToken)
        {
            int length = await reader.ReadDataByteAsync(cancellationToken);
            if ((length & 0x80) != 0)
                length = (length & 0x7F) << 8 | await reader.ReadDataByteAsync(cancellationToken);
            if (length > 0)
                await reader.ReadDataExactlyAsync(length, cancellationToken);
        }
    }
}
