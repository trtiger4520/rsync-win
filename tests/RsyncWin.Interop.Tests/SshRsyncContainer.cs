using System.Diagnostics;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// A throwaway <c>sshd + rsync</c> container using the peer image selected by the interop manifest,
/// with ed25519 key-only auth for user <c>syncer</c>.
/// rsync is never installed on the Windows host; Docker is the interop substrate.
/// </summary>
public sealed class SshRsyncContainer : IAsyncLifetime
{
    private string? _containerId;
    private string _workDir = null!;
    private RsyncPeerSpec Peer => InteropPeerSelection.Current;

    public string User => "syncer";
    public string Host => "127.0.0.1";
    public int Port { get; private set; }
    public string KeyPath { get; private set; } = null!;
    public string KnownHostsPath { get; private set; } = null!;

    private static string OpenSshTool(string name) =>
        Path.Combine(Environment.SystemDirectory, "OpenSSH", name);

    /// <summary>Full ssh.exe argv for running <paramref name="remoteCommand"/> on the container.</summary>
    public IReadOnlyList<string> SshArgs(IEnumerable<string> remoteCommand) =>
        [.. BaseSshArgs(KeyPath), $"{User}@{Host}", .. remoteCommand];

    /// <summary>Like <see cref="SshArgs"/> but authenticating with an arbitrary key file.</summary>
    public IReadOnlyList<string> SshArgsWithKey(string keyPath, IEnumerable<string> remoteCommand) =>
        [.. BaseSshArgs(keyPath), $"{User}@{Host}", .. remoteCommand];

    /// <summary>Runs a command inside the container (for gathering ground truth).</summary>
    public Task<(int ExitCode, string StdOut, string StdErr)> ExecAsync(params string[] command) =>
        RunAsync("docker", ["exec", _containerId!, .. command], TimeSpan.FromSeconds(60));

    private List<string> BaseSshArgs(string keyPath) =>
    [
        "-i", keyPath,
        "-p", Port.ToString(),
        "-o", "BatchMode=yes",
        "-o", "IdentitiesOnly=yes",
        "-o", "StrictHostKeyChecking=no",
        "-o", $"UserKnownHostsFile={KnownHostsPath}",
        "-o", "ConnectTimeout=10",
    ];

    public async Task InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), $"rsyncwin-interop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workDir);
        KeyPath = Path.Combine(_workDir, "id_ed25519");
        KnownHostsPath = Path.Combine(_workDir, "known_hosts");

        var keygen = await RunAsync(OpenSshTool("ssh-keygen.exe"),
            ["-q", "-t", "ed25519", "-N", "", "-f", KeyPath], TimeSpan.FromSeconds(30));
        if (keygen.ExitCode != 0)
            throw new InvalidOperationException($"ssh-keygen failed: {keygen.StdErr}");
        string publicKey = (await File.ReadAllTextAsync(KeyPath + ".pub")).Trim();

        string script =
            "ssh-keygen -A && " +
            "adduser -D syncer && " +
            // BusyBox adduser locks the account ('!'); '*' unlocks it for key-only auth.
            "sed -i 's/^syncer:!/syncer:*/' /etc/shadow && " +
            "mkdir -p /home/syncer/.ssh && " +
            $"printf '%s\\n' '{publicKey}' > /home/syncer/.ssh/authorized_keys && " +
            "chmod 700 /home/syncer/.ssh && chmod 600 /home/syncer/.ssh/authorized_keys && " +
            "chown -R syncer:syncer /home/syncer/.ssh && " +
            // The same shape as the captured golden tree, so hermetic and live tests can share
            // one expected-entries table: known sizes, a UTF-8 name, a space name, a nested dir.
            "mkdir -p /t/tree/subdir && cd /t/tree && " +
            "touch b000_empty && " +
            "printf 'twelve bytes' > b001_small.txt && " +
            "head -c 65536 /dev/zero > b002_64k.bin && " +
            "head -c 300000 /dev/urandom > b003_300k.bin && " +
            // ash does not expand \x in double quotes; printf does.
            "printf 'utf8' > \"$(printf 'b004_\\xe4\\xb8\\xad\\xe6\\x96\\x87\\xe6\\xaa\\x94\\xe5\\x90\\x8d.txt')\" && " +
            "printf 'space' > 'b005 name with space.txt' && " +
            "printf 'nested!' > subdir/nested.txt && " +
            "chown -R syncer:syncer /t && " +
            "exec /usr/sbin/sshd -D -e";

        var run = await RunAsync("docker",
            ["run", "-d", "--rm", "--label", "rsyncwin-interop=1",
             "-p", "127.0.0.1::22", Peer.Image, "sh", "-c", script],
            TimeSpan.FromSeconds(120));
        if (run.ExitCode != 0)
            throw new InvalidOperationException($"docker run failed (is Docker running?): {run.StdErr}");
        _containerId = run.StdOut.Trim();

        await AssertPeerVersionAsync();

        var port = await RunAsync("docker", ["port", _containerId, "22/tcp"], TimeSpan.FromSeconds(30));
        if (port.ExitCode != 0)
            throw new InvalidOperationException($"docker port failed: {port.StdErr}");
        string firstLine = port.StdOut.Split('\n')[0].Trim();
        Port = int.Parse(firstLine[(firstLine.LastIndexOf(':') + 1)..]);

        await WaitUntilSshReadyAsync();
    }

    private async Task WaitUntilSshReadyAsync()
    {
        // Image + sshd startup inside the container takes seconds to tens of seconds (network).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(150);
        string lastError = "";
        while (DateTime.UtcNow < deadline)
        {
            var probe = await RunAsync(OpenSshTool("ssh.exe"),
                SshArgs(["true"]), TimeSpan.FromSeconds(20));
            if (probe.ExitCode == 0)
                return;
            lastError = probe.StdErr;
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
        throw new TimeoutException($"container sshd never became reachable; last ssh error: {lastError}");
    }

    public async Task DisposeAsync()
    {
        if (_containerId is not null)
        {
            await CaptureContainerLogAsync();
            await RunAsync("docker", ["rm", "-f", _containerId], TimeSpan.FromSeconds(60));
        }
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
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
            $"container-ssh-{Peer.Id}-{Guid.NewGuid():N}.log");
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
