using System.Buffers.Binary;
using System.Text;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Session;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Protocol.FileList;

/// <summary>Which optional flist fields are on the wire — derived from the options WE put in the
/// server argv, because the sender encodes against that same set.</summary>
public sealed record FileListOptions
{
    public required int Protocol { get; init; }
    public bool PreserveUid { get; init; }
    public bool PreserveGid { get; init; }
    public bool PreserveLinks { get; init; }
    public bool PreserveDevices { get; init; }
    public bool PreserveSpecials { get; init; }

    /// <summary>CF_ID0_NAMES was negotiated: the id-list terminator carries a name for id 0.</summary>
    public bool Id0Names { get; init; }

    /// <summary><c>--checksum</c> (<c>-c</c>, <c>always_checksum</c>) is active: the sender appends a
    /// whole-file checksum to every regular-file entry.</summary>
    public bool Checksum { get; init; }

    /// <summary>Negotiated transfer-checksum digest length (<c>xfer_sum_len</c>) — 16 for
    /// xxh128/md5/md4, 8 for xxh64/xxh3. Only consulted when <see cref="Checksum"/> is set.</summary>
    public int ChecksumLength { get; init; }
}

public sealed record FileListResult(
    IReadOnlyList<FileEntry> Entries,
    int IoError,
    IReadOnlyDictionary<int, string> UserNames,
    IReadOnlyDictionary<int, string> GroupNames);

/// <summary>
/// Decodes the sender's file list from the demultiplexed data channel — protocol 30/31 with
/// <c>CF_VARINT_FLIST_FLAGS</c> negotiated (which we always negotiate).
/// </summary>
/// <remarks>
/// Byte layout spec'd from behavior sources and validated byte-exact against four captures
/// (<c>docs/wire-notes.md</c>, flist section). Entries arrive in the sender's readdir order —
/// NEVER assume sorted; both sides sort afterwards with <see cref="FileNameComparer"/>, and the
/// returned <see cref="FileListResult.Entries"/> is already sorted into ndx order.
/// </remarks>
public static class FileListReader
{
    private const int MaxPathLength = 4096; // MAXPATHLEN — canonical recv aborts at l1+l2 >= this

    private const XmitFlags SupportedFlags =
        XmitFlags.TopDir | XmitFlags.SameMode | XmitFlags.ExtendedFlags |
        XmitFlags.SameUid | XmitFlags.SameGid | XmitFlags.SameName | XmitFlags.LongName |
        XmitFlags.SameTime | XmitFlags.SameRdevMajor | XmitFlags.ModNsec;

    public static async ValueTask<FileListResult> ReadAsync(
        MultiplexReader input, FileListOptions options, CancellationToken cancellationToken = default)
    {
        if (options.Checksum && options.ChecksumLength is <= 0 or > 64)
            throw new ProtocolException(RsyncExitCode.UnsupportedAction,
                $"flist: --checksum active but ChecksumLength={options.ChecksumLength} was never negotiated");

        var entries = new List<FileEntry>();

        // Delta state, seeded identically on both sides and persisting across the whole list.
        byte[] previousName = [];
        long previousMtime = 0;
        int previousMode = 0, previousUid = 0, previousGid = 0, previousRdevMajor = 0;

        while (true)
        {
            var xflags = (XmitFlags)await input.ReadVarintAsync(cancellationToken);
            if (xflags == XmitFlags.None)
                break;
            ValidateFlags(xflags);

            // --- name: l1 prefix share + l2 suffix ------------------------------------------
            int l1 = xflags.HasFlag(XmitFlags.SameName)
                ? await input.ReadDataByteAsync(cancellationToken)
                : 0;
            int l2 = xflags.HasFlag(XmitFlags.LongName)
                ? await input.ReadVarintAsync(cancellationToken)
                : await input.ReadDataByteAsync(cancellationToken);
            // Bound l2 on its own before summing: a hostile LONG_NAME varint near int.MaxValue
            // must fail as a stream error, not wrap the sum negative and crash the allocator.
            if (l1 > previousName.Length || l2 < 0 || l2 >= MaxPathLength || l1 + l2 >= MaxPathLength)
                throw new InvalidDataException($"flist: name lengths l1={l1} l2={l2} out of range");

            byte[] name = new byte[l1 + l2];
            previousName.AsSpan(0, l1).CopyTo(name);
            (await input.ReadDataExactlyAsync(l2, cancellationToken)).CopyTo(name, l1);
            previousName = name;
            ValidateName(name);

            // --- fixed fields ---------------------------------------------------------------
            long size = await input.ReadVarlongAsync(3, cancellationToken);
            if (size < 0)
                throw new ProtocolException(RsyncExitCode.UnsupportedAction,
                    $"flist: negative file length for \"{Encoding.UTF8.GetString(name)}\"");

            long mtime = xflags.HasFlag(XmitFlags.SameTime)
                ? previousMtime
                : await input.ReadVarlongAsync(4, cancellationToken);
            previousMtime = mtime;

            int nsec = 0;
            if (xflags.HasFlag(XmitFlags.ModNsec))
            {
                nsec = await input.ReadVarintAsync(cancellationToken);
                if (nsec is < 0 or > 999_999_999)
                    throw new ProtocolException(RsyncExitCode.ProtocolIncompatibility,
                        $"flist: mtime nanoseconds {nsec} out of range");
            }

            int mode = xflags.HasFlag(XmitFlags.SameMode)
                ? previousMode
                : BinaryPrimitives.ReadInt32LittleEndian(await input.ReadDataExactlyAsync(4, cancellationToken));
            previousMode = mode;
            ValidateMode(mode, name);

            // --- option-gated fields (absent from the wire entirely when the option is off) --
            int uid = previousUid, gid = previousGid;
            if (options.PreserveUid && !xflags.HasFlag(XmitFlags.SameUid))
                uid = await input.ReadVarintAsync(cancellationToken);
            previousUid = uid;
            if (options.PreserveGid && !xflags.HasFlag(XmitFlags.SameGid))
                gid = await input.ReadVarintAsync(cancellationToken);
            previousGid = gid;

            int fileType = mode & 0xF000;
            int rdevMajor = 0, rdevMinor = 0;
            if (options.PreserveDevices && fileType is FileEntry.CharDevice or FileEntry.BlockDevice)
            {
                rdevMajor = xflags.HasFlag(XmitFlags.SameRdevMajor)
                    ? previousRdevMajor
                    : await input.ReadVarintAsync(cancellationToken);
                previousRdevMajor = rdevMajor;
                rdevMinor = await input.ReadVarintAsync(cancellationToken);
                size = 0; // receiver forces device lengths to zero
            }
            else if (options.PreserveSpecials && fileType is FileEntry.Fifo or FileEntry.Socket
                     && options.Protocol < 31)
            {
                rdevMinor = await input.ReadVarintAsync(cancellationToken); // major forced SAME
            }

            byte[]? linkTarget = null;
            if (options.PreserveLinks && fileType == FileEntry.Symlink)
            {
                int targetLength = await input.ReadVarintAsync(cancellationToken);
                if (targetLength is < 0 or >= MaxPathLength)
                    throw new InvalidDataException($"flist: symlink target length {targetLength} out of range");
                linkTarget = await input.ReadDataExactlyAsync(targetLength, cancellationToken);
            }

            byte[]? flistChecksum = null;
            if (options.Checksum && fileType == FileEntry.RegularFile)
                flistChecksum = await input.ReadDataExactlyAsync(options.ChecksumLength, cancellationToken);

            entries.Add(new FileEntry
            {
                NameBytes = name,
                Mode = mode,
                Size = size,
                ModifiedUnixSeconds = mtime,
                ModifiedNanoseconds = nsec,
                Uid = uid,
                Gid = gid,
                RdevMajor = rdevMajor,
                RdevMinor = rdevMinor,
                LinkTarget = linkTarget,
                FlistChecksum = flistChecksum,
            });
        }

        // End of list is always exactly: varint(0) xflags + varint(io_error). Then the id→name
        // lists, one per active preserve option. No NDX_FLIST_EOF outside incremental recursion.
        int ioError = await input.ReadVarintAsync(cancellationToken);
        var userNames = options.PreserveUid
            ? await ReadIdListAsync(input, options.Id0Names, cancellationToken)
            : new Dictionary<int, string>();
        var groupNames = options.PreserveGid
            ? await ReadIdListAsync(input, options.Id0Names, cancellationToken)
            : new Dictionary<int, string>();

        entries.Sort(FileNameComparer.Instance);
        return new FileListResult(entries, ioError, userNames, groupNames);
    }

    private static void ValidateFlags(XmitFlags xflags)
    {
        if (xflags.HasFlag(XmitFlags.Hlinked))
            throw new ProtocolException(RsyncExitCode.UnsupportedAction,
                "flist: entry carries hardlink data, but -H was never requested");
        if (xflags.HasFlag(XmitFlags.UserNameFollows) || xflags.HasFlag(XmitFlags.GroupNameFollows))
            throw new ProtocolException(RsyncExitCode.UnsupportedAction,
                "flist: inline id names are an incremental-recursion feature we never negotiate");
        if ((xflags & ~SupportedFlags) != 0)
            throw new InvalidDataException(
                $"flist: xflags 0x{(int)xflags:x} carries bits we cannot skip safely — stream is desynced or an unrequested option is active");
    }

    private static void ValidateName(ReadOnlySpan<byte> name)
    {
        // Mirrors canonical receive-side safety: no absolute paths, no '..' components (exit 4).
        if (name.IsEmpty || name[0] == (byte)'/')
            throw new ProtocolException(RsyncExitCode.UnsupportedAction, "flist: absolute or empty name");
        foreach (Range component in name.Split((byte)'/'))
        {
            if (name[component] is [(byte)'.', (byte)'.'])
                throw new ProtocolException(RsyncExitCode.UnsupportedAction, "flist: '..' path component");
        }
    }

    private static void ValidateMode(int mode, ReadOnlySpan<byte> name)
    {
        if ((mode & 0xF000) is not (FileEntry.RegularFile or FileEntry.Directory or FileEntry.Symlink
            or FileEntry.CharDevice or FileEntry.BlockDevice or FileEntry.Fifo or FileEntry.Socket))
        {
            throw new ProtocolException(RsyncExitCode.ProtocolIncompatibility,
                $"flist: mode 0{Convert.ToString(mode, 8)} of \"{Encoding.UTF8.GetString(name)}\" has no valid type bits");
        }
    }

    private static async ValueTask<Dictionary<int, string>> ReadIdListAsync(
        MultiplexReader input, bool id0Names, CancellationToken cancellationToken)
    {
        var names = new Dictionary<int, string>();
        while (true)
        {
            int id = await input.ReadVarintAsync(cancellationToken);
            if (id == 0 && !id0Names)
                break;

            int length = await input.ReadDataByteAsync(cancellationToken);
            names[id] = Encoding.UTF8.GetString(await input.ReadDataExactlyAsync(length, cancellationToken));
            if (id == 0)
                break; // with CF_ID0_NAMES the terminator itself carries root's name
        }
        return names;
    }
}
