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

    /// <summary>Reads one iflags word — 16-bit little-endian.</summary>
    public static async ValueTask<ItemFlags> ReadItemFlagsAsync(
        this MultiplexReader input, CancellationToken cancellationToken = default)
    {
        byte[] wire = await input.ReadDataExactlyAsync(2, cancellationToken);
        return (ItemFlags)(wire[0] | wire[1] << 8);
    }

    /// <summary>
    /// Reads one file index through the stateful <paramref name="codec"/>, consuming exactly the
    /// bytes of whichever wire form arrives (1–6 bytes).
    /// </summary>
    public static async ValueTask<int> ReadNdxAsync(
        this MultiplexReader input, NdxCodec codec, CancellationToken cancellationToken = default)
    {
        // Incremental form detection mirrors NdxCodec.Read: [00] | [FF?] then [diff] | [FE x y]
        // | [FE x|80 y m n]. Bytes are assembled so the codec sees one contiguous span and all
        // register updates stay in one place.
        byte[] buffer = new byte[NdxCodec.MaxLength];
        int length = 0;
        buffer[length++] = await input.ReadDataByteAsync(cancellationToken);
        if (buffer[0] == 0x00)
            return codec.Read(buffer.AsSpan(0, length)).Ndx;

        if (buffer[0] == 0xFF)
            buffer[length++] = await input.ReadDataByteAsync(cancellationToken);

        if (buffer[length - 1] == 0xFE)
        {
            buffer[length++] = await input.ReadDataByteAsync(cancellationToken);
            buffer[length++] = await input.ReadDataByteAsync(cancellationToken);
            if ((buffer[length - 2] & 0x80) != 0)
            {
                buffer[length++] = await input.ReadDataByteAsync(cancellationToken);
                buffer[length++] = await input.ReadDataByteAsync(cancellationToken);
            }
        }
        return codec.Read(buffer.AsSpan(0, length)).Ndx;
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
