using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Hand-builds a minimal protocol-31/md5 server byte stream (handshake prologue + flist + generator
/// replies) for scenarios no capture exercises — an early/premature <c>NDX_DONE</c> and a requested
/// file the sender never answers. Every helper mirrors the exact wire shapes the production readers
/// (<c>HandshakeRunner</c>, <c>FileListReader</c>, <c>ReplyReceiver</c>) expect, built from the same
/// span codecs they use (<see cref="VarintCodec"/>, <see cref="NdxCodec"/>), never hardcoded from a
/// GPL source.
/// </summary>
internal static class ScriptedSessionBuilder
{
    /// <summary>
    /// Version 31, compat_flags 510 (CF_VARINT_FLIST_FLAGS set, matching the captured
    /// <c>HandshakeRunnerTests</c> value), a single "md5" checksum offer, and an arbitrary seed.
    /// </summary>
    public static readonly byte[] HandshakePrologue =
    [
        0x1f, 0x00, 0x00, 0x00,               // version int32 LE = 31
        0x81, 0xfe,                            // compat_flags varint = 510
        0x03, (byte)'m', (byte)'d', (byte)'5', // checksum-list vstring: len=3, "md5"
        0x44, 0x33, 0x22, 0x11,                // checksum_seed int32 LE (arbitrary, unused by md5)
    ];

    /// <summary>One flist entry for a zero-byte regular file: no SAME_* bits, no long name.</summary>
    public static byte[] BuildRegularFileEntry(string name, long mtime = 0)
    {
        var buffer = new List<byte>();
        WriteVarint(buffer, 0x04); // ExtendedFlags zero-substitute xflags — no SAME_* bits
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        buffer.Add((byte)nameBytes.Length); // l2, no LONG_NAME
        buffer.AddRange(nameBytes);
        WriteVarlong(buffer, 0, minBytes: 3); // size = 0
        WriteVarlong(buffer, mtime, minBytes: 4);
        Span<byte> mode = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(mode, 0x81A4); // regular file, 0644
        buffer.AddRange(mode.ToArray());
        return [.. buffer];
    }

    /// <summary>The flist terminator: xflags varint(0), io_error varint(0). No id lists follow
    /// since these tests never set PreserveUid/PreserveGid.</summary>
    public static byte[] FlistTerminator => [0x00, 0x00];

    /// <summary>
    /// A full-content transfer reply for a zero-byte file: ndx, ITEM_TRANSFER|ITEM_IS_NEW, the
    /// all-zero sum head, a single end token, and the (correct) whole-file md5 trailer for an
    /// empty file — so <see cref="RsyncWin.Protocol.Delta.FileReceiver"/> reports a checksum match.
    /// </summary>
    public static byte[] BuildEmptyFileReply(NdxCodec codec, int ndx)
    {
        var buffer = new List<byte>();
        Span<byte> ndxBytes = stackalloc byte[NdxCodec.MaxLength];
        int ndxLength = codec.Write(ndxBytes, ndx);
        buffer.AddRange(ndxBytes[..ndxLength].ToArray());

        const ushort iflags = 0xA000; // ITEM_TRANSFER | ITEM_IS_NEW
        buffer.Add((byte)(iflags & 0xFF));
        buffer.Add((byte)(iflags >> 8));

        buffer.AddRange(new byte[16]); // SumHeader.Null — full transfer, no basis
        buffer.AddRange(new byte[4]);  // Token: int32 0 = end of file data (no literal bytes)
        buffer.AddRange(MD5.HashData([])); // whole-file md5 of zero bytes — never seeded
        return [.. buffer];
    }

    /// <summary>The single-byte <c>NDX_DONE</c> marker (state untouched — never goes through a codec).</summary>
    public static byte[] NdxDone => [0x00];

    /// <summary>Five zero varlong(3) stats fields — content doesn't matter for these tests.</summary>
    public static byte[] StatsBlock =>
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    ];

    /// <summary>Frames <paramref name="payload"/> as a single MSG_DATA multiplex frame.</summary>
    public static byte[] Wrap(params IEnumerable<byte>[] parts)
    {
        byte[] payload = [.. parts.SelectMany(p => p)];
        byte[] header = new byte[MuxHeader.Size];
        new MuxHeader(MessageTag.Data, payload.Length).Write(header);
        return [.. header, .. payload];
    }

    private static void WriteVarint(List<byte> buffer, int value)
    {
        Span<byte> tmp = stackalloc byte[VarintCodec.MaxVarintLength];
        int n = VarintCodec.WriteVarint(tmp, value);
        buffer.AddRange(tmp[..n].ToArray());
    }

    private static void WriteVarlong(List<byte> buffer, long value, int minBytes)
    {
        Span<byte> tmp = stackalloc byte[VarintCodec.MaxVarlongLength];
        int n = VarintCodec.WriteVarlong(tmp, value, minBytes);
        buffer.AddRange(tmp[..n].ToArray());
    }
}
