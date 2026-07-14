using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace RsyncWin.Protocol.Checksums;

/// <summary>Transfer/file checksum algorithms we implement. Negotiated by name (§10 of the spec).</summary>
public enum ChecksumAlgorithm
{
    /// <summary>MD4 — the protocol-29 fallback. Never advertise at 30/31: stock rsync's
    /// OpenSSL-EVP and builtin paths disagree on seed placement there.</summary>
    Md4,

    /// <summary>MD5 — the protocol ≥ 30 default fallback and our primary offer.</summary>
    Md5,

    /// <summary>xxHash64 (negotiated "xxh64").</summary>
    XxHash64,
}

/// <summary>
/// The seed-placement layer over MD4/MD5/xxHash64. rsync mixes the session's
/// <c>checksum_seed</c> differently in three contexts, and getting any one wrong produces a
/// silent full resend rather than an error.
/// </summary>
/// <remarks>
/// Specified in <c>docs/codec-spec.md</c> §10 (11 recomputed seeded vectors; xxHash values
/// additionally confirmed against the canonical xxhash library). The rules:
/// <list type="bullet">
/// <item><b>Block sums</b>: MD4 appends the seed (skip when 0); MD5 prepends when
/// <c>CF_CHKSUM_SEED_FIX</c> was negotiated, else appends (skip when 0); xxHash64 uses the seed
/// as its numeric seed, sign-extended, with no zero short-circuit.</item>
/// <item><b>Whole-file sums</b>: proto-29 MD4 prepends the seed <em>unconditionally</em> — the one
/// place the zero short-circuit does not apply; negotiated MD4/MD5 take no seed at all; xxHash64
/// always uses seed 0.</item>
/// <item>Block sums go on the wire truncated to <c>s2length</c> (prefix-take); whole-file sums are
/// full length and may use a different negotiated algorithm than the block sums.</item>
/// </list>
/// </remarks>
public static class StrongChecksum
{
    public static int DigestLength(ChecksumAlgorithm algorithm) =>
        algorithm switch
        {
            ChecksumAlgorithm.Md4 or ChecksumAlgorithm.Md5 => 16,
            ChecksumAlgorithm.XxHash64 => 8,
            _ => throw new ArgumentOutOfRangeException(nameof(algorithm)),
        };

    /// <summary>
    /// Computes a per-block strong sum into <paramref name="digest"/> and returns the full digest
    /// length. The caller truncates to <c>s2length</c> when writing the wire.
    /// </summary>
    public static int ComputeBlockSum(
        ChecksumAlgorithm algorithm,
        int seed,
        bool checksumSeedFix,
        ReadOnlySpan<byte> block,
        Span<byte> digest)
    {
        Span<byte> seedBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(seedBytes, seed);

        switch (algorithm)
        {
            case ChecksumAlgorithm.Md4:
            {
                var md4 = Md4.Create();
                md4.Append(block);
                if (seed != 0)
                    md4.Append(seedBytes);
                md4.Finish(digest);
                return 16;
            }
            case ChecksumAlgorithm.Md5:
            {
                using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                if (seed != 0 && checksumSeedFix)
                    md5.AppendData(seedBytes);
                md5.AppendData(block);
                if (seed != 0 && !checksumSeedFix)
                    md5.AppendData(seedBytes);
                md5.GetHashAndReset(digest);
                return 16;
            }
            case ChecksumAlgorithm.XxHash64:
            {
                // Numeric seed, SIGN-extended ((long)seed, never (long)(uint)seed), no zero
                // short-circuit. .NET emits xxHash bytes big-endian; rsync is little-endian —
                // always go through the UInt64.
                ulong value = XxHash64.HashToUInt64(block, seed);
                BinaryPrimitives.WriteUInt64LittleEndian(digest, value);
                return 8;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(algorithm));
        }
    }

    /// <summary>Creates a streaming hasher for a whole-file transfer sum.</summary>
    /// <param name="protocol">The negotiated protocol; 29 selects the old MD4 seed-prepend rule.</param>
    public static WholeFileChecksum CreateFileSum(ChecksumAlgorithm algorithm, int seed, int protocol) =>
        WholeFileChecksum.Create(algorithm, seed, protocol);
}

/// <summary>Streaming whole-file checksum (context B of the spec). Finalize-once.</summary>
public struct WholeFileChecksum
{
    private Md4 _md4;
    private IncrementalHash? _md5;
    private XxHash64? _xxh;
    private ChecksumAlgorithm _algorithm;

    internal static WholeFileChecksum Create(ChecksumAlgorithm algorithm, int seed, int protocol)
    {
        var sum = new WholeFileChecksum { _algorithm = algorithm };
        switch (algorithm)
        {
            case ChecksumAlgorithm.Md4:
                sum._md4 = Md4.Create();
                if (protocol < 30)
                {
                    // Proto-29 MD4_OLD prepends the seed UNCONDITIONALLY — even a zero seed
                    // hashes four zero bytes. Negotiated MD4 at 30+ takes no seed at all.
                    Span<byte> seedBytes = stackalloc byte[4];
                    BinaryPrimitives.WriteInt32LittleEndian(seedBytes, seed);
                    sum._md4.Append(seedBytes);
                }
                break;
            case ChecksumAlgorithm.Md5:
                sum._md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5); // never seeded
                break;
            case ChecksumAlgorithm.XxHash64:
                sum._xxh = new XxHash64(0); // session seed deliberately ignored here
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(algorithm));
        }
        return sum;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        switch (_algorithm)
        {
            case ChecksumAlgorithm.Md4: _md4.Append(data); break;
            case ChecksumAlgorithm.Md5: _md5!.AppendData(data); break;
            case ChecksumAlgorithm.XxHash64: _xxh!.Append(data); break;
        }
    }

    /// <summary>Writes the full-length digest and returns its length. The instance must not be reused.</summary>
    public int Finish(Span<byte> digest)
    {
        switch (_algorithm)
        {
            case ChecksumAlgorithm.Md4:
                _md4.Finish(digest);
                return 16;
            case ChecksumAlgorithm.Md5:
                _md5!.GetHashAndReset(digest);
                _md5.Dispose();
                return 16;
            case ChecksumAlgorithm.XxHash64:
                BinaryPrimitives.WriteUInt64LittleEndian(digest, _xxh!.GetCurrentHashAsUInt64());
                return 8;
            default:
                throw new InvalidOperationException("uninitialized");
        }
    }
}
