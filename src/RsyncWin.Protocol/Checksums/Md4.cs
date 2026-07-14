using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace RsyncWin.Protocol.Checksums;

/// <summary>
/// MD4 message digest (RFC 1320). Not provided by the BCL; needed for the legacy strong-checksum
/// path when a session negotiates below protocol 30.
/// </summary>
/// <remarks>
/// Implemented from the RFC 1320 specification. Validated against the RFC's Appendix A.5 test
/// suite and against digests produced by OpenSSL's legacy provider
/// (<c>test-fixtures/vectors/md4-openssl-vectors.txt</c>).
/// <para>
/// This is the plain, correct MD4. rsync's <em>older</em> protocols (&lt; 27) additionally emulate
/// historical bugs (seed placement, omitted trailing length block for large files); those variants
/// are out of scope — protocol 29 is our negotiation floor.
/// </para>
/// </remarks>
public struct Md4
{
    public const int DigestLength = 16;
    private const int BlockLength = 64;

    [InlineArray(BlockLength)]
    private struct PendingBuffer
    {
        private byte _element0;
    }

    private uint _a, _b, _c, _d;
    private ulong _totalBytes;
    private PendingBuffer _pending;
    private int _pendingCount;

    public static Md4 Create()
    {
        Md4 md4 = default;
        md4._a = 0x67452301;
        md4._b = 0xefcdab89;
        md4._c = 0x98badcfe;
        md4._d = 0x10325476;
        return md4;
    }

    /// <summary>Convenience one-shot hash.</summary>
    public static byte[] HashData(ReadOnlySpan<byte> data)
    {
        var md4 = Create();
        md4.Append(data);
        var digest = new byte[DigestLength];
        md4.Finish(digest);
        return digest;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        _totalBytes += (ulong)data.Length;

        Span<byte> pendingSpan = _pending;

        if (_pendingCount > 0)
        {
            int fill = Math.Min(BlockLength - _pendingCount, data.Length);
            data[..fill].CopyTo(pendingSpan[_pendingCount..]);
            _pendingCount += fill;
            data = data[fill..];

            if (_pendingCount < BlockLength)
                return;

            ProcessBlock(pendingSpan);
            _pendingCount = 0;
        }

        while (data.Length >= BlockLength)
        {
            ProcessBlock(data[..BlockLength]);
            data = data[BlockLength..];
        }

        if (!data.IsEmpty)
        {
            data.CopyTo(pendingSpan);
            _pendingCount = data.Length;
        }
    }

    /// <summary>Applies RFC 1320 padding and writes the 16-byte digest. The instance must not be reused.</summary>
    public void Finish(Span<byte> digest)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(digest.Length, DigestLength);

        ulong bitLength = _totalBytes * 8;

        // Padding: a single 0x80, zeros to 56 mod 64, then the original bit length as a 64-bit LE.
        Span<byte> pad = stackalloc byte[BlockLength * 2];
        pad.Clear();
        pad[0] = 0x80;
        int padLength = (int)((_totalBytes % BlockLength < 56)
            ? 56 - _totalBytes % BlockLength
            : 120 - _totalBytes % BlockLength);
        BinaryPrimitives.WriteUInt64LittleEndian(pad[padLength..], bitLength);
        Append(pad[..(padLength + 8)]);

        BinaryPrimitives.WriteUInt32LittleEndian(digest, _a);
        BinaryPrimitives.WriteUInt32LittleEndian(digest[4..], _b);
        BinaryPrimitives.WriteUInt32LittleEndian(digest[8..], _c);
        BinaryPrimitives.WriteUInt32LittleEndian(digest[12..], _d);
    }

    private void ProcessBlock(ReadOnlySpan<byte> block)
    {
        Span<uint> x = stackalloc uint[16];
        for (int i = 0; i < 16; i++)
            x[i] = BinaryPrimitives.ReadUInt32LittleEndian(block[(i * 4)..]);

        uint a = _a, b = _b, c = _c, d = _d;

        // Round 1: F(x,y,z) = (x & y) | (~x & z)
        static uint F(uint x, uint y, uint z) => (x & y) | (~x & z);
        static uint Rot(uint v, int s) => uint.RotateLeft(v, s);

        a = Rot(a + F(b, c, d) + x[0], 3);  d = Rot(d + F(a, b, c) + x[1], 7);
        c = Rot(c + F(d, a, b) + x[2], 11); b = Rot(b + F(c, d, a) + x[3], 19);
        a = Rot(a + F(b, c, d) + x[4], 3);  d = Rot(d + F(a, b, c) + x[5], 7);
        c = Rot(c + F(d, a, b) + x[6], 11); b = Rot(b + F(c, d, a) + x[7], 19);
        a = Rot(a + F(b, c, d) + x[8], 3);  d = Rot(d + F(a, b, c) + x[9], 7);
        c = Rot(c + F(d, a, b) + x[10], 11); b = Rot(b + F(c, d, a) + x[11], 19);
        a = Rot(a + F(b, c, d) + x[12], 3); d = Rot(d + F(a, b, c) + x[13], 7);
        c = Rot(c + F(d, a, b) + x[14], 11); b = Rot(b + F(c, d, a) + x[15], 19);

        // Round 2: G(x,y,z) = (x & y) | (x & z) | (y & z), constant 0x5a827999
        static uint G(uint x, uint y, uint z) => (x & y) | (x & z) | (y & z);

        a = Rot(a + G(b, c, d) + x[0] + 0x5a827999, 3);  d = Rot(d + G(a, b, c) + x[4] + 0x5a827999, 5);
        c = Rot(c + G(d, a, b) + x[8] + 0x5a827999, 9);  b = Rot(b + G(c, d, a) + x[12] + 0x5a827999, 13);
        a = Rot(a + G(b, c, d) + x[1] + 0x5a827999, 3);  d = Rot(d + G(a, b, c) + x[5] + 0x5a827999, 5);
        c = Rot(c + G(d, a, b) + x[9] + 0x5a827999, 9);  b = Rot(b + G(c, d, a) + x[13] + 0x5a827999, 13);
        a = Rot(a + G(b, c, d) + x[2] + 0x5a827999, 3);  d = Rot(d + G(a, b, c) + x[6] + 0x5a827999, 5);
        c = Rot(c + G(d, a, b) + x[10] + 0x5a827999, 9); b = Rot(b + G(c, d, a) + x[14] + 0x5a827999, 13);
        a = Rot(a + G(b, c, d) + x[3] + 0x5a827999, 3);  d = Rot(d + G(a, b, c) + x[7] + 0x5a827999, 5);
        c = Rot(c + G(d, a, b) + x[11] + 0x5a827999, 9); b = Rot(b + G(c, d, a) + x[15] + 0x5a827999, 13);

        // Round 3: H(x,y,z) = x ^ y ^ z, constant 0x6ed9eba1
        static uint H(uint x, uint y, uint z) => x ^ y ^ z;

        a = Rot(a + H(b, c, d) + x[0] + 0x6ed9eba1, 3);  d = Rot(d + H(a, b, c) + x[8] + 0x6ed9eba1, 9);
        c = Rot(c + H(d, a, b) + x[4] + 0x6ed9eba1, 11); b = Rot(b + H(c, d, a) + x[12] + 0x6ed9eba1, 15);
        a = Rot(a + H(b, c, d) + x[2] + 0x6ed9eba1, 3);  d = Rot(d + H(a, b, c) + x[10] + 0x6ed9eba1, 9);
        c = Rot(c + H(d, a, b) + x[6] + 0x6ed9eba1, 11); b = Rot(b + H(c, d, a) + x[14] + 0x6ed9eba1, 15);
        a = Rot(a + H(b, c, d) + x[1] + 0x6ed9eba1, 3);  d = Rot(d + H(a, b, c) + x[9] + 0x6ed9eba1, 9);
        c = Rot(c + H(d, a, b) + x[5] + 0x6ed9eba1, 11); b = Rot(b + H(c, d, a) + x[13] + 0x6ed9eba1, 15);
        a = Rot(a + H(b, c, d) + x[3] + 0x6ed9eba1, 3);  d = Rot(d + H(a, b, c) + x[11] + 0x6ed9eba1, 9);
        c = Rot(c + H(d, a, b) + x[7] + 0x6ed9eba1, 11); b = Rot(b + H(c, d, a) + x[15] + 0x6ed9eba1, 15);

        _a += a; _b += b; _c += c; _d += d;
    }
}
