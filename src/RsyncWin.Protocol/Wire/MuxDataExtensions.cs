using System.Buffers.Binary;
using RsyncWin.Protocol.Mux;

namespace RsyncWin.Protocol.Wire;

/// <summary>
/// Reads wire primitives from a demultiplexed data channel, consuming exactly one value each —
/// the streaming glue between the span codecs and <see cref="MultiplexReader"/>.
/// </summary>
public static class MuxDataExtensions
{
    public static async ValueTask<int> ReadInt32Async(
        this MultiplexReader input, CancellationToken cancellationToken = default) =>
        BinaryPrimitives.ReadInt32LittleEndian(await input.ReadDataExactlyAsync(4, cancellationToken));

    public static async ValueTask<int> ReadVarintAsync(
        this MultiplexReader input, CancellationToken cancellationToken = default)
    {
        byte header = await input.ReadDataByteAsync(cancellationToken);
        int total = VarintCodec.WireLength(header);

        byte[] wire = new byte[total];
        wire[0] = header;
        if (total > 1)
            (await input.ReadDataExactlyAsync(total - 1, cancellationToken)).CopyTo(wire, 1);
        return VarintCodec.ReadVarint(wire).Value;
    }

    public static async ValueTask<long> ReadVarlongAsync(
        this MultiplexReader input, int minBytes, CancellationToken cancellationToken = default)
    {
        byte[] head = await input.ReadDataExactlyAsync(minBytes, cancellationToken);
        int total = VarintCodec.VarlongWireLength(head[0], minBytes);

        byte[] wire;
        if (total == minBytes)
        {
            wire = head;
        }
        else
        {
            wire = new byte[total];
            head.CopyTo(wire, 0);
            (await input.ReadDataExactlyAsync(total - minBytes, cancellationToken)).CopyTo(wire, minBytes);
        }
        return VarintCodec.ReadVarlong(wire, minBytes).Value;
    }
}
