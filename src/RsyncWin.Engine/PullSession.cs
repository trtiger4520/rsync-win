using System.Buffers.Binary;
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
public static class PullSession
{
    public sealed record Result(
        SessionContext Session,
        IReadOnlyList<FileEntry> Entries,
        int TransferredFiles,
        long TransferredBytes,
        IReadOnlyList<string> RedoneFiles,
        IReadOnlyList<string> FailedFiles,
        IReadOnlyList<string> SkippedNonRegular,
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
        // must abort the session with nothing created.
        foreach (FileEntry entry in entries)
            _ = LocalPath(destinationDirectory, entry);
        Directory.CreateDirectory(destinationDirectory);

        var outbound = new NdxCodec();   // one encoder and one decoder per direction, whole session
        var inbound = new NdxCodec();
        var skipped = new List<string>();
        var expected = new HashSet<int>();

        // ---- phase 0: walk the sorted list, create dirs, request every regular file ----------
        for (int ndx = 0; ndx < entries.Count; ndx++)
        {
            FileEntry entry = entries[ndx];
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(LocalPath(destinationDirectory, entry));
                // Itemize echoes, mirroring the captured client: the transfer root exists already
                // (mtime differs), every other dir is newly created.
                WriteNdxAndFlags(channel.Writer, outbound, ndx,
                    ndx == 0 ? ItemFlags.ReportTime : ItemFlags.IsNew | ItemFlags.LocalChange);
            }
            else if (entry.IsRegularFile)
            {
                WriteTransferRequest(channel.Writer, outbound, ndx);
                expected.Add(ndx);
            }
            else
            {
                skipped.Add(entry.Name); // symlinks/devices land in a later phase
            }
        }
        WriteNdxDone(channel.Writer);
        await channel.Writer.FlushAsync(cancellationToken);

        // ---- phase-0 replies; checksum mismatches queue for the redo pass --------------------
        var receiver = new ReplyReceiver(channel, inbound, destinationDirectory, expected);
        List<int> redo = await receiver.ReceiveUntilDoneAsync(cancellationToken);

        // ---- phase 1 (redo) + the measured DONE#2/#3/#4 burst ---------------------------------
        foreach (int ndx in redo)
        {
            WriteTransferRequest(channel.Writer, outbound, ndx);
            expected.Add(ndx);
        }
        WriteNdxDone(channel.Writer);
        WriteNdxDone(channel.Writer);
        WriteNdxDone(channel.Writer);
        await channel.Writer.FlushAsync(cancellationToken);

        List<int> failed = await receiver.ReceiveUntilDoneAsync(cancellationToken);

        // ---- tail: sender's final DONE, stats, goodbye exchange -------------------------------
        await SessionSetup.ExpectNdxDoneAsync(channel.Reader, cancellationToken);
        SessionStats stats = await SessionSetup.ReadStatsAsync(channel.Reader, cancellationToken);
        await SessionSetup.ExpectNdxDoneAsync(channel.Reader, cancellationToken);
        WriteNdxDone(channel.Writer);
        await channel.Writer.FlushAsync(cancellationToken);

        // Directory mtimes last, deepest first — the file writes above kept bumping them.
        foreach (FileEntry dir in entries.Where(e => e.IsDirectory)
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
            [.. redo.Select(n => entries[n].Name)],
            [.. failed.Select(n => entries[n].Name)],
            skipped,
            notSent,
            ioErrorFlags,
            stats,
            channel.ServerMessages);
    }

    /// <summary>
    /// Maps a wire name under the destination. <see cref="FileListReader"/> enforced the Unix-side
    /// rules (no leading '/', no '..' components split on '/'), but '\' and ':' are path syntax
    /// only on Windows — <c>..\evil</c> or <c>C:evil</c> passes that validation and still escapes
    /// through <see cref="Path.Combine(string[])"/>. Reject both characters outright (P5's name
    /// mapper will sanitize instead), then prove the resolved path stayed inside the destination.
    /// </summary>
    internal static string LocalPath(string destination, FileEntry entry)
    {
        if (entry.NameBytes is [(byte)'.'])
            return destination;

        string name = entry.Name;
        if (name.Contains('\\') || name.Contains(':'))
            throw new ProtocolException(RsyncExitCode.UnsupportedAction,
                $"refusing server-sent name with Windows path syntax: \"{name}\"");

        string full = Path.GetFullPath(Path.Combine([destination, .. name.Split('/')]));
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

    private static void WriteNdxAndFlags(MultiplexWriter writer, NdxCodec codec, int ndx, ItemFlags iflags)
    {
        Span<byte> buffer = stackalloc byte[NdxCodec.MaxLength + 2];
        int length = codec.Write(buffer, ndx);
        buffer[length++] = (byte)((ushort)iflags & 0xFF);
        buffer[length++] = (byte)((ushort)iflags >> 8);
        writer.Write(buffer[..length]);
    }

    private static void WriteTransferRequest(MultiplexWriter writer, NdxCodec codec, int ndx)
    {
        // A new file with no basis: ITEM_TRANSFER|ITEM_IS_NEW and the all-zero sum head
        // ("send me everything"), exactly the captured request shape 01 00 A0 + 16 zeros.
        WriteNdxAndFlags(writer, codec, ndx, ItemFlags.Transfer | ItemFlags.IsNew);
        Span<byte> nullHead = stackalloc byte[SumHeader.Size];
        SumHeader.Null.Write(nullHead);
        writer.Write(nullHead);
    }

    private static void WriteNdxDone(MultiplexWriter writer) => writer.Write([0x00]);

    /// <summary>Reads sender replies until an NDX_DONE echo, writing files as they stream in.</summary>
    private sealed class ReplyReceiver(
        SessionChannel channel, NdxCodec inbound, string destination, HashSet<int> expected)
    {
        public int TransferredFiles { get; private set; }
        public long TransferredBytes { get; private set; }

        public async Task<List<int>> ReceiveUntilDoneAsync(CancellationToken cancellationToken)
        {
            var mismatched = new List<int>();
            while (true)
            {
                int ndx = await channel.Reader.ReadNdxAsync(inbound, cancellationToken);
                if (ndx == RsyncConstants.NdxDone)
                    return mismatched;

                ItemFlags iflags = await channel.Reader.ReadItemFlagsAsync(cancellationToken);
                if (iflags.HasFlag(ItemFlags.BasisTypeFollows))
                    await channel.Reader.ReadDataByteAsync(cancellationToken);
                if (iflags.HasFlag(ItemFlags.XnameFollows))
                    await ReadVstringAsync(channel.Reader, cancellationToken);
                if (!iflags.HasFlag(ItemFlags.Transfer))
                    continue; // attribute-only echo

                if (ndx < 0 || ndx >= channel.FileList.Entries.Count || !expected.Remove(ndx))
                    throw new InvalidDataException(
                        $"transfer: sender replied with unrequested ndx {ndx} — stream is desynced");

                FileEntry entry = channel.FileList.Entries[ndx];
                if (!await ReceiveIntoFileAsync(entry, cancellationToken))
                    mismatched.Add(ndx);
            }
        }

        private async Task<bool> ReceiveIntoFileAsync(FileEntry entry, CancellationToken cancellationToken)
        {
            string finalPath = LocalPath(destination, entry);
            string tempPath = finalPath + ".rsyncwin-tmp";

            try
            {
                FileReceiveResult received;
                await using (var temp = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                {
                    received = await FileReceiver.ReceiveAsync(
                        channel.Reader,
                        temp,
                        channel.Session.TransferChecksum,
                        channel.Session.ChecksumSeed,
                        channel.Session.Protocol,
                        StrongChecksum.DigestLength(channel.Session.TransferChecksum),
                        cancellationToken);
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
