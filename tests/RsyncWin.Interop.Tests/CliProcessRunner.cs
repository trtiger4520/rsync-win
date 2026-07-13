using System.Diagnostics;
using System.Text;

namespace RsyncWin.Interop.Tests;

internal sealed record CliProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>Runs the Release rsyncwin executable as a child process so application tests cross the
/// same argument parsing, process transport, reporting, and exit-code boundaries as an operator</summary>
internal static class CliProcessRunner
{
    public static async Task<CliProcessResult> RunAsync(
        IEnumerable<string> arguments,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        string executable = FindReleaseExecutable();
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        if (environment is not null)
        {
            foreach ((string key, string? value) in environment)
                startInfo.Environment[key] = value;
        }

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"failed to start {executable}");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return new CliProcessResult(process.ExitCode, await standardOutput, await standardError, TimedOut: false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            await process.WaitForExitAsync();
            return new CliProcessResult(-1, await standardOutput, await standardError, TimedOut: true);
        }
    }

    public static async Task<string> CreateSshWrapperAsync(SshRsyncContainer container, string directory)
    {
        Directory.CreateDirectory(directory);
        string wrapper = Path.Combine(directory, "ssh.cmd");
        string ssh = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe");
        string script =
            "@echo off\r\n" +
            $"\"{ssh}\" -i \"{container.KeyPath}\" -p {container.Port} -o BatchMode=yes " +
            "-o IdentitiesOnly=yes -o StrictHostKeyChecking=no " +
            $"-o \"UserKnownHostsFile={container.KnownHostsPath}\" -o ConnectTimeout=10 %*\r\n";
        await File.WriteAllTextAsync(wrapper, script, Encoding.ASCII);
        return wrapper;
    }

    private static string FindReleaseExecutable()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "RsyncWin.Cli")))
            directory = directory.Parent;

        if (directory is null)
            throw new InvalidOperationException("could not locate the repository root from the test output directory");

        string fileName = OperatingSystem.IsWindows() ? "rsyncwin.exe" : "rsyncwin";
        string executable = Path.Combine(directory.FullName, "src", "RsyncWin.Cli", "bin", "Release", "net10.0", fileName);
        if (!File.Exists(executable))
            throw new FileNotFoundException("Release rsyncwin CLI was not built", executable);
        return executable;
    }
}
