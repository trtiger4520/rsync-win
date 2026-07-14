using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// A throwaway rsync-daemon container using the peer image selected by the interop manifest,
/// running
/// <c>rsync --daemon --no-detach</c> in the foreground with three modules: a read-only
/// pull tree, a writable push destination, and an authenticated read-only module.
/// rsync is never installed on the Windows host; Docker is the interop substrate.
/// </summary>
public sealed class RsyncdContainer : IAsyncLifetime
{
    private string? _containerId;
    private RsyncPeerSpec Peer => InteropPeerSelection.Current;

    public const string AuthUser = "alice";
    public const string AuthPassword = "opensesame";

    public string Host => "127.0.0.1";
    public int Port { get; private set; }

    /// <summary>Runs a command inside the container (for gathering ground truth / resets).</summary>
    public Task<(int ExitCode, string StdOut, string StdErr)> ExecAsync(params string[] command) =>
        RunAsync("docker", ["exec", _containerId!, .. command], TimeSpan.FromSeconds(60));

    /// <summary>Clears the writable [push] module between tests -- the module path is fixed, so
    /// tests reset it rather than getting a fresh per-test directory (mirrors the intent of
    /// <see cref="SshRsyncContainer"/>'s per-test remote dirs, adapted to a single daemon module).</summary>
    public async Task ResetPushModuleAsync()
    {
        var result = await ExecAsync("sh", "-c", "rm -rf /t/dpush/*");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"failed to reset /t/dpush: {result.StdErr}");
    }

    public async Task InitializeAsync()
    {
        string script =
            // Same deterministic tree shape as SshRsyncContainer / the golden-vector capture,
            // so hermetic and live daemon tests can share expectations.
            "mkdir -p /t/tree/subdir /t/dpush && cd /t/tree && " +
            "touch b000_empty && " +
            "printf 'twelve bytes' > b001_small.txt && " +
            "head -c 65536 /dev/zero > b002_64k.bin && " +
            "head -c 300000 /dev/urandom > b003_300k.bin && " +
            // ash does not expand \x in double quotes; printf does.
            "printf 'utf8' > \"$(printf 'b004_\\xe4\\xb8\\xad\\xe6\\x96\\x87\\xe6\\xaa\\x94\\xe5\\x90\\x8d.txt')\" && " +
            "printf 'space' > 'b005 name with space.txt' && " +
            "printf 'nested!' > subdir/nested.txt && " +
            "touch -d '2020-01-02 03:04:05' b000_empty b001_small.txt && " +
            "touch -d '2021-06-15 12:00:00' b002_64k.bin b003_300k.bin && " +
            "touch -d '2022-12-31 23:59:59' \"$(printf 'b004_\\xe4\\xb8\\xad\\xe6\\x96\\x87\\xe6\\xaa\\x94\\xe5\\x90\\x8d.txt')\" 'b005 name with space.txt' && " +
            "touch -d '2020-01-02 03:04:05' subdir/nested.txt subdir /t/tree && " +
            "cd / && " +
            "printf 'alice:opensesame\\n' > /etc/rsyncd.secrets && chmod 600 /etc/rsyncd.secrets && " +
            "printf '%s\\n' " +
                "'port = 873' 'use chroot = no' " +
                "'[tree]' '    path = /t/tree' '    read only = yes' " +
                "'[push]' '    path = /t/dpush' '    read only = no' '    uid = root' '    gid = root' " +
                "'[secret]' '    path = /t/tree' '    read only = yes' " +
                "'    auth users = alice' '    secrets file = /etc/rsyncd.secrets' " +
                "> /etc/rsyncd.conf && " +
            "exec rsync --daemon --no-detach --config=/etc/rsyncd.conf";

        var run = await RunAsync("docker",
            ["run", "-d", "--rm", "--label", "rsyncwin-interop=1",
             "-p", "127.0.0.1::873", Peer.Image, "sh", "-c", script],
            TimeSpan.FromSeconds(120));
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"docker run failed (is Docker running?): {run.StdErr}");
        _containerId = run.StdOut.Trim();

        await AssertPeerVersionAsync();

        var port = await RunAsync("docker", ["port", _containerId, "873/tcp"], TimeSpan.FromSeconds(30));
        if (port.ExitCode != 0)
            throw new InvalidOperationException($"docker port failed: {port.StdErr}");
        string firstLine = port.StdOut.Split('\n')[0].Trim();
        Port = int.Parse(firstLine[(firstLine.LastIndexOf(':') + 1)..]);

        await WaitUntilDaemonReadyAsync();
    }

    /// <summary>Polls until a TCP connect succeeds AND the daemon actually greets with
    /// "@RSYNCD: " -- a bare open port during image startup is not enough.</summary>
    private async Task WaitUntilDaemonReadyAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(150);
        string lastError = "";
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new TcpClient();
                using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.ConnectAsync(Host, Port, connectCts.Token);

                using NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                string? greeting = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (greeting is not null && greeting.StartsWith("@RSYNCD:", StringComparison.Ordinal))
                    return;
                lastError = $"unexpected greeting: {greeting ?? "<eof>"}";
            }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
            {
                lastError = ex.Message;
            }
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
        throw new TimeoutException($"rsync daemon never became reachable; last error: {lastError}");
    }

    public async Task DisposeAsync()
    {
        if (_containerId is not null)
        {
            await CaptureContainerLogAsync();
            await RunAsync("docker", ["rm", "-f", _containerId], TimeSpan.FromSeconds(60));
        }
    }

    private async Task AssertPeerVersionAsync()
    {
        var version = await RunAsync("docker", ["exec", _containerId!, "rsync", "--version"],
            TimeSpan.FromSeconds(30));
        if (version.ExitCode != 0)
            throw new InvalidOperationException($"rsync --version failed: {version.StdErr}");
        Peer.ValidateVersionOutput(version.StdOut);
    }

    private async Task CaptureContainerLogAsync()
    {
        string? artifacts = Environment.GetEnvironmentVariable("RSYNCWIN_INTEROP_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(artifacts))
            return;

        var logs = await RunAsync("docker", ["logs", _containerId!], TimeSpan.FromSeconds(30));
        Directory.CreateDirectory(artifacts);
        string path = Path.Combine(
            artifacts,
            $"container-daemon-{Peer.Id}-{Guid.NewGuid():N}.log");
        await File.WriteAllTextAsync(path, logs.StdOut + logs.StdErr);
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // docker/ssh emit UTF-8; the default console codepage would mangle non-ASCII names.
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"failed to start {fileName}");
        Task<string> stdOut = process.StandardOutput.ReadToEndAsync();
        Task<string> stdErr = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            return (-1, await stdOut, await stdErr);
        }
        return (process.ExitCode, await stdOut, await stdErr);
    }
}
