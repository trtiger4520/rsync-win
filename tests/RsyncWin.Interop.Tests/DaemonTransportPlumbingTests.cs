using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using RsyncWin.Transport;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Socket-plumbing checks for <see cref="DaemonTcpTransport"/> against an in-process loopback
/// listener — deliberately NOT trait-gated: no Docker, no rsync daemon, runs in the fast tier.
/// These pin the pumping, shutdown, and EOF mechanics; live daemon interop belongs to a separate
/// Category=Interop suite.
/// </summary>
public class DaemonTransportPlumbingTests
{
    private static async Task<byte[]> DrainAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        var all = new MemoryStream();
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            foreach (ReadOnlyMemory<byte> segment in result.Buffer)
                all.Write(segment.Span);
            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
                return all.ToArray();
        }
    }

    [Fact]
    public async Task RoundTrip_EchoesBinaryBlob_IncludingControlBytes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Task<byte[]> echoTask = Task.Run(async () =>
        {
            using TcpClient serverSide = await listener.AcceptTcpClientAsync(cts.Token);
            using NetworkStream stream = serverSide.GetStream();
            var buffer = new byte[4096];
            var received = new MemoryStream();
            int read;
            while (received.Length < 6 && (read = await stream.ReadAsync(buffer, cts.Token)) > 0)
                received.Write(buffer, 0, read);
            await stream.WriteAsync(received.ToArray(), cts.Token);
            return received.ToArray();
        }, cts.Token);

        await using var transport = await DaemonTcpTransport.ConnectAsync("127.0.0.1", port, cts.Token);
        byte[] payload = [0x00, 0x0A, 0x0D, 0xFF, 0x41, 0x42];
        await transport.Output.WriteAsync(payload, cts.Token);

        byte[] echoed = await echoTask;
        Assert.Equal(payload, echoed);

        ReadResult result = await transport.Input.ReadAsync(cts.Token);
        Assert.Equal(payload, result.Buffer.ToArray());
        transport.Input.AdvanceTo(result.Buffer.End);
    }

    [Fact]
    public async Task StandardError_CompletesImmediately_WithNoBufferedBytes()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync(cts.Token).AsTask();
        await using var transport = await DaemonTcpTransport.ConnectAsync("127.0.0.1", port, cts.Token);
        using TcpClient serverSide = await acceptTask;

        ReadResult result = await transport.StandardError.ReadAsync(cts.Token);
        Assert.True(result.IsCompleted);
        Assert.Equal(0, result.Buffer.Length);
    }

    [Fact]
    public async Task RemoteClose_PropagatesEofOnInput_NoHang()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Task acceptAndCloseTask = Task.Run(async () =>
        {
            using TcpClient serverSide = await listener.AcceptTcpClientAsync(cts.Token);
            serverSide.Client.Shutdown(SocketShutdown.Send);
        }, cts.Token);

        await using var transport = await DaemonTcpTransport.ConnectAsync("127.0.0.1", port, cts.Token);
        await acceptAndCloseTask;

        byte[] tail = await DrainAsync(transport.Input, cts.Token);
        Assert.Empty(tail);
    }

    [Fact]
    public async Task DisposeAsync_WhileReadPending_CompletesWithoutHanging()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync(cts.Token).AsTask();
        var transport = await DaemonTcpTransport.ConnectAsync("127.0.0.1", port, cts.Token);
        using TcpClient serverSide = await acceptTask;

        Task<ReadResult> pendingRead = transport.Input.ReadAsync(cts.Token).AsTask();

        await transport.DisposeAsync().AsTask().WaitAsync(cts.Token);

        // The pending read must resolve (either completed or cancelled) rather than hang forever.
        await Task.WhenAny(pendingRead, Task.Delay(Timeout.Infinite, cts.Token));
    }

    [Fact]
    public async Task ConnectToUnlistenedPort_FailsPromptly()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop(); // reserve a free port, then vacate it so nothing listens there

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAnyAsync<SocketException>(
            () => DaemonTcpTransport.ConnectAsync("127.0.0.1", port, cts.Token));
    }
}
