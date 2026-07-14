using System.Text;
using RsyncWin.Protocol.Checksums;

namespace RsyncWin.Protocol.Tests.Checksums;

/// <summary>
/// The seeded vectors from docs/codec-spec.md §10 — recomputed during spec reconciliation,
/// xxHash values additionally confirmed against the canonical xxhash reference library.
/// Every one of these encodes a rule that silently breaks interop when wrong.
/// </summary>
public class StrongChecksumTests
{
    private const int Seed = 0x12345678;
    private static readonly byte[] Abc = Encoding.ASCII.GetBytes("abc");

    private static string BlockSum(ChecksumAlgorithm alg, int seed, bool seedFix, byte[] data)
    {
        Span<byte> digest = stackalloc byte[16];
        int length = StrongChecksum.ComputeBlockSum(alg, seed, seedFix, data, digest);
        return Convert.ToHexStringLower(digest[..length].ToArray());
    }

    private static string FileSum(ChecksumAlgorithm alg, int seed, int protocol, byte[] data)
    {
        var sum = StrongChecksum.CreateFileSum(alg, seed, protocol);
        sum.Append(data);
        Span<byte> digest = stackalloc byte[16];
        int length = sum.Finish(digest);
        return Convert.ToHexStringLower(digest[..length].ToArray());
    }

    // ---- context A: block sums -----------------------------------------------------------

    [Fact]
    public void Md4Block_AppendsSeed()
        => Assert.Equal("dfc21629291028d56ee023a48767e5c9", BlockSum(ChecksumAlgorithm.Md4, Seed, false, Abc));

    [Fact]
    public void Md4Block_SeedZero_ShortCircuits()
        // With seed 0 the block sum is plain MD4("abc").
        => Assert.Equal("a448017aaf21d8525fc10ae87aa6729d", BlockSum(ChecksumAlgorithm.Md4, 0, false, Abc));

    [Fact]
    public void Md4Block_SeedLandsExactlyOnBlockBoundary()
    {
        // 60 data bytes + 4 seed bytes = exactly 64 hashed bytes — pins the finalization path.
        byte[] data = new byte[60];
        Array.Fill(data, (byte)'a');
        Assert.Equal("267197955a81efc3101b737b22b8bab5", BlockSum(ChecksumAlgorithm.Md4, Seed, false, data));
    }

    [Fact]
    public void Md5Block_WithSeedFix_Prepends()
        => Assert.Equal("5a035b41f39449760a0c6a01e4060c62", BlockSum(ChecksumAlgorithm.Md5, Seed, true, Abc));

    [Fact]
    public void Md5Block_WithoutSeedFix_Appends()
        => Assert.Equal("a7e33be7c2164ebddcb3da6720a2d7d4", BlockSum(ChecksumAlgorithm.Md5, Seed, false, Abc));

    [Fact]
    public void Md5Block_SeedZero_ShortCircuits_RegardlessOfFlag()
    {
        string plain = "900150983cd24fb0d6963f7d28e17f72"; // MD5("abc")
        Assert.Equal(plain, BlockSum(ChecksumAlgorithm.Md5, 0, true, Abc));
        Assert.Equal(plain, BlockSum(ChecksumAlgorithm.Md5, 0, false, Abc));
    }

    [Fact]
    public void XxHash64Block_UsesNumericSeed_LittleEndianOnWire()
        // 0x0F7FD1655F1AF42B emitted LE.
        => Assert.Equal("2bf41a5f65d17f0f", BlockSum(ChecksumAlgorithm.XxHash64, Seed, false, Abc));

    [Fact]
    public void XxHash64Block_NegativeSeed_SignExtends()
        // seed -1 must become 0xFFFFFFFFFFFFFFFF, not 0x00000000FFFFFFFF.
        => Assert.Equal("7621c09c586e3028", BlockSum(ChecksumAlgorithm.XxHash64, -1, false, Abc));

    [Fact]
    public void XxHash64Block_SeedZero_DoesNotShortCircuit()
        // Seed 0 is just... seed 0. Same as the whole-file value for the same data.
        => Assert.Equal("990977adf52cbc44", BlockSum(ChecksumAlgorithm.XxHash64, 0, false, Abc));

    // ---- context B: whole-file sums ------------------------------------------------------

    [Fact]
    public void Md4File_Proto29_PrependsSeed_Unconditionally()
    {
        Assert.Equal("4d713279fde8d43637584c88006e02f8", FileSum(ChecksumAlgorithm.Md4, Seed, 29, Abc));

        // The ONE place the zero short-circuit does NOT apply: seed 0 still hashes 4 zero bytes.
        Assert.Equal("280f4da968ef17b2ca2fb5d289874be9", FileSum(ChecksumAlgorithm.Md4, 0, 29, Abc));
    }

    [Fact]
    public void Md4File_Proto30Plus_TakesNoSeed()
        => Assert.Equal("a448017aaf21d8525fc10ae87aa6729d", FileSum(ChecksumAlgorithm.Md4, Seed, 31, Abc));

    [Fact]
    public void Md5File_NeverSeeded()
        => Assert.Equal("900150983cd24fb0d6963f7d28e17f72", FileSum(ChecksumAlgorithm.Md5, Seed, 31, Abc));

    [Fact]
    public void XxHash64File_AlwaysSeedZero()
        // Session seed deliberately ignored: 0x44BC2CF5AD770999 emitted LE.
        => Assert.Equal("990977adf52cbc44", FileSum(ChecksumAlgorithm.XxHash64, Seed, 31, Abc));

    [Fact]
    public void FileSum_Streams_IdenticallyToOneShot()
    {
        byte[] data = TestFixtures.Bytes("rolling", "blob64k.bin");
        Span<byte> expected = stackalloc byte[16];
        Span<byte> actual = stackalloc byte[16];

        foreach (var algorithm in (ChecksumAlgorithm[])[ChecksumAlgorithm.Md4, ChecksumAlgorithm.Md5, ChecksumAlgorithm.XxHash64])
        {
            var oneShot = StrongChecksum.CreateFileSum(algorithm, Seed, 29);
            oneShot.Append(data);
            int length = oneShot.Finish(expected);

            var streamed = StrongChecksum.CreateFileSum(algorithm, Seed, 29);
            for (int i = 0; i < data.Length; i += 4093) // deliberately awkward chunking
                streamed.Append(data.AsSpan(i, Math.Min(4093, data.Length - i)));
            streamed.Finish(actual);

            Assert.True(expected[..length].SequenceEqual(actual[..length]), $"{algorithm} streamed digest diverged");
        }
    }
}
