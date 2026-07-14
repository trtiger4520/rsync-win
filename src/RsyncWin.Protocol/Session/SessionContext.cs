using RsyncWin.Protocol.Checksums;

namespace RsyncWin.Protocol.Session;

/// <summary>
/// The immutable result of a completed handshake. Everything downstream reads negotiated facts from
/// here — role engines must never branch on a raw protocol version int scattered through the code.
/// </summary>
/// <remarks>
/// Produced by <see cref="HandshakeRunner"/>. The multiplex properties record <em>measured</em>
/// directionality (<c>docs/wire-notes.md</c>): the server→client stream is framed right after the
/// seed at every protocol we speak, while client→server is framed only at 30+ (protocol 29 writes
/// raw bytes).
/// </remarks>
public sealed record SessionContext
{
    /// <summary>The negotiated session version: <c>min(ours, peer's)</c>.</summary>
    public required int Protocol { get; init; }

    /// <summary>
    /// The server's compat_flags varint (0 below protocol 30). The server derives these bits from
    /// the capability letters we sent after <c>-e.</c> in the server argv, so with
    /// <see cref="ServerArgvBuilder.ClientInfo"/> the expected value against rsync ≥ 3.2.7 is 510.
    /// </summary>
    public required int CompatFlags { get; init; }

    /// <summary>Session checksum seed, read LAST in the handshake as a 4-byte LE int.</summary>
    public required int ChecksumSeed { get; init; }

    /// <summary>Per-block / transfer strong checksum algorithm (<c>xfer_sum</c>).</summary>
    public required ChecksumAlgorithm TransferChecksum { get; init; }

    /// <summary>Whole-file checksum algorithm (<c>file_sum</c>). Same as the transfer sum in v1.</summary>
    public required ChecksumAlgorithm FileChecksum { get; init; }

    public bool ChecksumSeedFix => (CompatFlags & RsyncConstants.CompatChecksumSeedFix) != 0;

    public bool VarintFlistFlags => (CompatFlags & RsyncConstants.CompatVarintFlistFlags) != 0;

    public bool SafeFileList => (CompatFlags & RsyncConstants.CompatSafeFlist) != 0;

    /// <summary>Server→client frames are multiplexed post-handshake at every protocol we speak.</summary>
    public bool MultiplexedInput => true;

    /// <summary>Client→server frames are multiplexed only at protocol 30+; 29 writes raw bytes.</summary>
    public bool MultiplexedOutput => Protocol >= 30;
}
