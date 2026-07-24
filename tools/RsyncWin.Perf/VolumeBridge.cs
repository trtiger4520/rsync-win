using System.Diagnostics;

namespace RsyncWin.Perf;

/// <summary>
/// Moves benchmark data between the host workdir (a Windows bind mount at <c>/host</c> in the
/// helper container) and the fast VM-internal named volume (mounted at <c>/bench</c>). Every copy
/// runs as an in-container <c>cp</c>, so the slow bind mount is only ever touched during untimed
/// setup/verification — the measured transfer stays daemon-volume to client-volume.
/// </summary>
internal static class VolumeBridge
{
    private const string HostRoot = "/host";
    private const string VolumeRoot = "/bench";

    /// <summary>Copies <paramref name="relativePath"/> from the host bind mount into the volume.</summary>
    public static Task PushAsync(string container, string relativePath, CancellationToken cancellationToken)
    {
        string host = Join(HostRoot, relativePath);
        string vol = Join(VolumeRoot, relativePath);
        // Reset the volume target, copy contents in, then open permissions so the non-root
        // rsyncwin client (uid 1000) can write results under it.
        return ExecShellAsync(
            container,
            $"rm -rf {Quote(vol)} && mkdir -p {Quote(vol)} && cp -a {Quote(host + "/.")} {Quote(vol + "/")} 2>/dev/null; chmod -R 0777 {Quote(vol)}",
            cancellationToken);
    }

    /// <summary>Copies <paramref name="relativePath"/> from the volume back onto the host bind mount.</summary>
    public static Task PullAsync(string container, string relativePath, CancellationToken cancellationToken)
    {
        string host = Join(HostRoot, relativePath);
        string vol = Join(VolumeRoot, relativePath);
        return ExecShellAsync(
            container,
            $"rm -rf {Quote(host)} && mkdir -p {Quote(host)} && cp -a {Quote(vol + "/.")} {Quote(host + "/")} 2>/dev/null; true",
            cancellationToken);
    }

    /// <summary>Ensures the writable path chain (mount root down to the client results dir) is traversable.</summary>
    public static Task EnsureResultsWritableAsync(string container, string client, CancellationToken cancellationToken)
        => ExecShellAsync(
            container,
            $"mkdir -p {Quote(Join(VolumeRoot, "results/" + client))} && chmod 0777 {VolumeRoot} {VolumeRoot}/results {Quote(Join(VolumeRoot, "results/" + client))}",
            cancellationToken);

    private static async Task ExecShellAsync(string container, string script, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("exec");
        info.ArgumentList.Add(container);
        info.ArgumentList.Add("sh");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(script);
        using var process = new Process { StartInfo = info };
        process.Start();
        string stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"volume bridge step failed (exit {process.ExitCode}) in {container}: {stderr.Trim()}");
    }

    // Container paths are always POSIX; the relative path comes from Path.Combine so normalize separators.
    private static string Join(string root, string relative) => $"{root}/{relative.Replace('\\', '/').Trim('/')}";

    private static string Quote(string value) => $"'{value.Replace("'", "'\\''")}'";
}
