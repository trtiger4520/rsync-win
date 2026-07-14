using System.Buffers;
using System.IO.Pipelines;
using RsyncWin.Transport;

namespace RsyncWin.Interop.Tests;

/// <summary>An in-memory transport: scripted server bytes in, captured client bytes out.</summary>
internal sealed class ScriptedTransport(byte[] serverBytes) : IRsyncTransport
{
    private readonly Pipe _written = new(new PipeOptions(pauseWriterThreshold: 0));

    public PipeReader Input { get; } = PipeReader.Create(new MemoryStream(serverBytes));
    public PipeWriter Output => _written.Writer;
    public PipeReader StandardError { get; } = PipeReader.Create(new MemoryStream([]));

    public ValueTask<int> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(0);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Everything the session wrote, available once the session has finished.</summary>
    public async Task<byte[]> WrittenBytesAsync()
    {
        await _written.Writer.CompleteAsync();
        var all = new MemoryStream();
        while (true)
        {
            ReadResult result = await _written.Reader.ReadAsync();
            foreach (ReadOnlyMemory<byte> segment in result.Buffer)
                all.Write(segment.Span);
            _written.Reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
                return all.ToArray();
        }
    }
}
