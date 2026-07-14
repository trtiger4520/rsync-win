using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Wire;

namespace RsyncWin.Protocol.Delta;

/// <summary>
/// One token of the uncompressed delta stream (<c>simple_recv_token</c>): a plain 4-byte LE int.
/// Positive = that many literal bytes follow raw; negative = a reference to basis block
/// <c>-(token + 1)</c>; zero = end of this file's data.
/// </summary>
public readonly record struct Token(int Raw)
{
    public bool IsEnd => Raw == 0;

    public bool IsLiteral => Raw > 0;

    /// <summary>Literal byte count (only when <see cref="IsLiteral"/>).</summary>
    public int LiteralLength => Raw;

    public bool IsBlockReference => Raw < 0;

    /// <summary>Zero-based basis block index (only when <see cref="IsBlockReference"/>).</summary>
    public int BlockIndex => -(Raw + 1);

    public static async ValueTask<Token> ReadAsync(
        MultiplexReader input, CancellationToken cancellationToken = default) =>
        new(await input.ReadInt32Async(cancellationToken));
}
