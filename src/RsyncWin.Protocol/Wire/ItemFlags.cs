namespace RsyncWin.Protocol.Wire;

/// <summary>
/// The 16-bit iflags word that follows every non-negative ndx on both the request and reply
/// channels (protocol ≥ 29). Little-endian on the wire. Bit values are scalar wire facts
/// (<c>docs/codec-spec.md</c> §5); capture-pinned sightings: 0x0008, 0x6000, 0xA000.
/// </summary>
/// <remarks>
/// Payload order after the word is fixed: the basis-type byte (iff <see cref="BasisTypeFollows"/>),
/// then the xname vstring (iff <see cref="XnameFollows"/>). <see cref="Transfer"/> means a sum
/// head follows on the request channel, and sum-head echo + token stream + whole-file checksum
/// on the reply channel. rsync's local-only bits (0x10000+) never appear on the wire.
/// </remarks>
[Flags]
public enum ItemFlags : ushort
{
    None = 0,
    ReportAtime = 0x0001,
    ReportChange = 0x0002,
    /// <summary>Doubles as TIMEFAIL for symlinks.</summary>
    ReportSize = 0x0004,
    ReportTime = 0x0008,
    ReportPerms = 0x0010,
    ReportOwner = 0x0020,
    ReportGroup = 0x0040,
    ReportAcl = 0x0080,
    ReportXattr = 0x0100,
    ReportCrtime = 0x0400,
    BasisTypeFollows = 0x0800,
    XnameFollows = 0x1000,
    IsNew = 0x2000,
    LocalChange = 0x4000,
    Transfer = 0x8000,
}
