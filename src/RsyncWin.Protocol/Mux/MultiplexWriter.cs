using System.Buffers;
using System.IO.Pipelines;

namespace RsyncWin.Protocol.Mux;

/// <summary>
/// Frames our outbound bytes as <see cref="MessageTag.Data"/> multiplex frames. Mirrors rsync's
/// buffered <c>io_flush</c> model: writes accumulate, and each <see cref="FlushAsync"/> emits the
/// pending bytes as one frame (split only when a frame would exceed the 24-bit length field).
/// Frame boundaries carry no semantics for the peer.
/// </summary>
/// <remarks>Only used when <c>SessionContext.MultiplexedOutput</c> is true — a protocol-29 client
/// writes raw bytes instead. Framing data the server does not expect desyncs immediately.</remarks>
public sealed class MultiplexWriter(PipeWriter output)
{
    private readonly ArrayBufferWriter<byte> _pending = new();

    /// <summary>Appends bytes to the pending data frame.</summary>
    public void Write(ReadOnlySpan<byte> payload) => _pending.Write(payload);

    /// <summary>Frames and flushes everything written since the last flush.</summary>
    public async ValueTask FlushAsync(CancellationToken cancellationToken = default)
    {
        ReadOnlyMemory<byte> pending = _pending.WrittenMemory;
        while (!pending.IsEmpty)
        {
            int length = Math.Min(pending.Length, RsyncConstants.MaxMuxPayload);

            Span<byte> header = output.GetSpan(MuxHeader.Size);
            new MuxHeader(MessageTag.Data, length).Write(header);
            output.Advance(MuxHeader.Size);

            pending.Span[..length].CopyTo(output.GetSpan(length));
            output.Advance(length);

            pending = pending[length..];
        }
        _pending.Clear();
        await output.FlushAsync(cancellationToken);
    }

    /// <summary>Sends a zero-length data frame — the protocol's liveness signal.</summary>
    public async ValueTask WriteKeepAliveAsync(CancellationToken cancellationToken = default)
    {
        Span<byte> header = output.GetSpan(MuxHeader.Size);
        new MuxHeader(MessageTag.Data, 0).Write(header);
        output.Advance(MuxHeader.Size);
        await output.FlushAsync(cancellationToken);
    }
}
