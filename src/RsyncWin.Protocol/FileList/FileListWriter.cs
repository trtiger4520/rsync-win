using System.Buffers.Binary;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Protocol.FileList;

/// <summary>
/// Encodes the sender's file list onto a multiplexed data channel — the byte-exact inverse of
/// <see cref="FileListReader.ReadAsync"/> for our supported option set.
/// </summary>
/// <remarks>
/// <para>
/// Scope (P7 T2): `-tr` only. <see cref="FileListOptions"/>'s uid/gid/rdev/link preservation flags
/// must all be off — a caller requesting any of them gets <see cref="NotSupportedException"/>
/// rather than silently wrong bytes, since none of those field shapes are exercised or verified
/// yet. Byte layout and the SAME_*/xflags decision rules: <c>docs/flist-spec.md</c> §1-4, pinned
/// byte-exact against <c>test-fixtures/vectors/ssh31-push-rt/c2s.bin</c>.
/// </para>
/// <para>
/// Entries are emitted in whatever order the caller supplies — the wire format carries no ordering
/// requirement, only the receiver's post-transfer sort (<see cref="FileNameComparer"/>) does, so
/// callers may pass readdir order (hermetic tests) or pre-sorted order (a future PushSession)
/// equally validly.
/// </para>
/// </remarks>
public static class FileListWriter
{
    public static void Write(
        MultiplexWriter writer, IReadOnlyList<FileEntry> entries, FileListOptions options, int ioError = 0)
    {
        if (options.PreserveUid || options.PreserveGid || options.PreserveLinks
            || options.PreserveDevices || options.PreserveSpecials)
        {
            throw new NotSupportedException(
                "FileListWriter (P7 T2) only encodes the `-tr` field set — uid/gid/rdev/symlink " +
                "writing belongs to a later phase");
        }

        byte[] previousName = [];
        long previousMtime = 0;
        int previousMode = 0;

        foreach (FileEntry entry in entries)
            WriteEntry(writer, entry, ref previousName, ref previousMtime, ref previousMode);

        WriteVarint(writer, 0); // xflags-position 0 terminates the loop (docs/flist-spec.md §4)
        WriteVarint(writer, ioError);
        // No id lists follow: only reachable with -o/-g, which are rejected above.
    }

    private static void WriteEntry(
        MultiplexWriter writer, FileEntry entry, ref byte[] previousName, ref long previousMtime, ref int previousMode)
    {
        if (entry.Size < 0)
            throw new ArgumentException($"flist: negative size for \"{entry.Name}\"", nameof(entry));

        byte[] name = entry.NameBytes;

        // Rule: SAME_NAME iff the new name shares a non-empty byte prefix with the previous one,
        // capped at 255 (l1 is a single wire byte) — mirrors the sender's own cap.
        int l1 = CommonPrefixLength(previousName, name, cap: 255);
        bool sameName = l1 > 0;
        int l2 = name.Length - l1;
        // Rule: LONG_NAME iff the suffix alone would overflow the 1-byte length field.
        bool longName = l2 > 255;

        // Rule: SAME_TIME/SAME_MODE iff the value is byte-identical to the running previous value,
        // including against the zero seed on entry #0 — neither has a first-entry guard, unlike
        // SAME_UID/SAME_GID below (docs/flist-spec.md §3).
        bool sameTime = entry.ModifiedUnixSeconds == previousMtime;
        bool sameMode = entry.Mode == previousMode;

        // Rule: XMIT_MOD_NSEC is stat-driven, not option-gated — set whenever the source mtime has
        // a nonzero sub-second part (docs/flist-spec.md §1, XMIT_MOD_NSEC row).
        bool modNsec = entry.ModifiedNanoseconds != 0;

        // Rule: TOP_DIR marks a transfer-root directory. FileEntry carries no explicit "is a
        // source arg" bit, so for our current single-source-arg, non-`--relative` scope the root
        // is unambiguously the literal name "." (docs/flist-spec.md §3).
        bool topDir = name is [(byte)'.'];

        // Rule: SAME_UID/SAME_GID are unconditionally set because -o/-g are permanently off in
        // this phase (guarded in Write) — the first-entry exception in the spec only matters once
        // real uid/gid values are being compared, which never happens here (docs/flist-spec.md §3).
        XmitFlags xflags = XmitFlags.SameUid | XmitFlags.SameGid;
        if (topDir)
            xflags |= XmitFlags.TopDir;
        if (sameMode)
            xflags |= XmitFlags.SameMode;
        if (sameName)
            xflags |= XmitFlags.SameName;
        if (longName)
            xflags |= XmitFlags.LongName;
        if (sameTime)
            xflags |= XmitFlags.SameTime;
        if (modNsec)
            xflags |= XmitFlags.ModNsec;

        // Rule: a genuinely all-zero flag word would be read back as end-of-list, so the sender
        // substitutes EXTENDED_FLAGS (bit 2, meaningless in varint mode) instead. Unreachable while
        // SAME_UID/SAME_GID are always set above, kept for correctness (docs/flist-spec.md §2 step 0).
        if (xflags == XmitFlags.None)
            xflags = XmitFlags.ExtendedFlags;

        WriteVarint(writer, (int)xflags);

        if (sameName)
        {
            Span<byte> l1Byte = [(byte)l1];
            writer.Write(l1Byte);
        }

        if (longName)
        {
            WriteVarint(writer, l2);
        }
        else
        {
            Span<byte> l2Byte = [(byte)l2];
            writer.Write(l2Byte);
        }
        writer.Write(name.AsSpan(l1));

        WriteVarlong(writer, entry.Size, minBytes: 3);

        if (!sameTime)
            WriteVarlong(writer, entry.ModifiedUnixSeconds, minBytes: 4);

        if (modNsec)
            WriteVarint(writer, entry.ModifiedNanoseconds);

        if (!sameMode)
        {
            Span<byte> modeBytes = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(modeBytes, entry.Mode);
            writer.Write(modeBytes);
        }

        previousName = name;
        previousMtime = entry.ModifiedUnixSeconds;
        previousMode = entry.Mode;
    }

    private static int CommonPrefixLength(ReadOnlySpan<byte> previous, ReadOnlySpan<byte> current, int cap)
    {
        int max = Math.Min(Math.Min(previous.Length, current.Length), cap);
        int i = 0;
        while (i < max && previous[i] == current[i])
            i++;
        return i;
    }

    private static void WriteVarint(MultiplexWriter writer, int value)
    {
        Span<byte> buffer = stackalloc byte[VarintCodec.MaxVarintLength];
        int length = VarintCodec.WriteVarint(buffer, value);
        writer.Write(buffer[..length]);
    }

    private static void WriteVarlong(MultiplexWriter writer, long value, int minBytes)
    {
        Span<byte> buffer = stackalloc byte[VarintCodec.MaxVarlongLength];
        int length = VarintCodec.WriteVarlong(buffer, value, minBytes);
        writer.Write(buffer[..length]);
    }
}
