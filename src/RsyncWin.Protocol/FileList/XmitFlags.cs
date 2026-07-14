namespace RsyncWin.Protocol.FileList;

/// <summary>
/// Per-entry transmit flags (<c>XMIT_*</c>) — the xflags word that leads every file-list entry.
/// Values are scalar wire facts; several bits are REUSED across protocol versions or entry types
/// and must be disambiguated by context (see <c>docs/wire-notes.md</c>, flist section).
/// </summary>
/// <remarks>
/// Under <c>CF_VARINT_FLIST_FLAGS</c> (always negotiated by us at 30/31) the word arrives as a
/// varint; a value of 0 terminates the list. Capture-pinned sightings: 0x19, 0x18, 0x3a,
/// 0xba (as varint <c>80 ba</c>), 0x2019 (as <c>a0 19</c>, carrying <see cref="ModNsec"/>).
/// </remarks>
[Flags]
public enum XmitFlags
{
    None = 0,

    /// <summary>A transfer-root directory, including the root <c>.</c> itself.</summary>
    TopDir = 1 << 0,

    SameMode = 1 << 1,

    /// <summary>In varint mode only a zero-substitute (a real all-zero word would look like end-of-list).</summary>
    ExtendedFlags = 1 << 2,

    /// <summary>Always set when <c>-o</c> was not requested (except the first-entry guard).</summary>
    SameUid = 1 << 3,

    /// <summary>Always set when <c>-g</c> was not requested (except the first-entry guard).</summary>
    SameGid = 1 << 4,

    /// <summary>An l1 prefix-share byte follows: that many leading bytes copy from the previous name.</summary>
    SameName = 1 << 5,

    /// <summary>The l2 name length is a varint instead of a single byte (names &gt; 255 bytes).</summary>
    LongName = 1 << 6,

    SameTime = 1 << 7,

    /// <summary>Devices: rdev major elided. Dirs (30+): sent without content — same bit, mode-typed.</summary>
    SameRdevMajor = 1 << 8,

    /// <summary>Entry is part of a hardlink cluster (<c>-H</c> — we never request it).</summary>
    Hlinked = 1 << 9,

    /// <summary>uid is followed by an inline user name (incremental recursion only — never for us).</summary>
    UserNameFollows = 1 << 10,

    /// <summary>gid is followed by an inline group name (incremental recursion only — never for us).</summary>
    GroupNameFollows = 1 << 11,

    /// <summary>First entry of a hardlink cluster (with <see cref="Hlinked"/>).</summary>
    HlinkFirst = 1 << 12,

    /// <summary>Protocol 31: an mtime-nanoseconds varint follows the mtime. Stat-driven, not option-gated.</summary>
    ModNsec = 1 << 13,

    /// <summary>Only with <c>--atimes</c> (never for us).</summary>
    SameAtime = 1 << 14,
}
