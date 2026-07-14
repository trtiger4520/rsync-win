using System.Text;

namespace RsyncWin.Protocol.FileList;

/// <summary>
/// One decoded file-list entry. Names are kept as the raw bytes off the wire — the sort that
/// positional indexes depend on is defined over bytes, and any culture-aware detour desyncs it.
/// </summary>
public sealed record FileEntry
{
    // Unix S_IFMT type bits — wire facts; rsync remaps only on platforms whose native values differ.
    private const int TypeMask = 0xF000;   // 0170000
    public const int Directory = 0x4000;   // 0040000
    public const int RegularFile = 0x8000; // 0100000
    public const int Symlink = 0xA000;     // 0120000
    public const int CharDevice = 0x2000;  // 0020000
    public const int BlockDevice = 0x6000; // 0060000
    public const int Fifo = 0x1000;        // 0010000
    public const int Socket = 0xC000;      // 0140000

    /// <summary>Relative path exactly as sent, '/'-separated, no NUL.</summary>
    public required byte[] NameBytes { get; init; }

    /// <summary>Unix mode word: type bits + permissions.</summary>
    public required int Mode { get; init; }

    public required long Size { get; init; }

    public required long ModifiedUnixSeconds { get; init; }

    /// <summary>Sub-second mtime part (protocol 31 <c>XMIT_MOD_NSEC</c>); 0 when not transmitted.</summary>
    public int ModifiedNanoseconds { get; init; }

    public int Uid { get; init; }

    public int Gid { get; init; }

    public int RdevMajor { get; init; }

    public int RdevMinor { get; init; }

    /// <summary>Symlink target bytes; null unless <c>-l</c> was active and this entry is a symlink.</summary>
    public byte[]? LinkTarget { get; init; }

    /// <summary>Whole-file checksum from the flist under <c>--checksum</c> (<c>F_SUM</c>); null unless
    /// <c>-c</c> was active and this entry is a regular file.</summary>
    public byte[]? FlistChecksum { get; init; }

    public string Name => Encoding.UTF8.GetString(NameBytes);

    public int FileType => Mode & TypeMask;

    public bool IsDirectory => FileType == Directory;

    public bool IsRegularFile => FileType == RegularFile;

    public bool IsSymlink => FileType == Symlink;

    /// <summary>Permission bits in the familiar octal form (e.g. 644).</summary>
    public int Permissions => Mode & 0xFFF;
}
