using System.Security.Cryptography;
using RsyncWin.Fs;

namespace RsyncWin.Interop.Tests;

/// <summary>Application-level gates that execute the published CLI against real rsync peers
/// The 100,000-file integrity case intentionally belongs to the full correctness/benchmark tier in
/// tools/RsyncWin.Perf, keeping the default live interop suite bounded</summary>
[Trait("Category", "Interop")]
[Trait("Layer", "Application")]
public sealed class CliApplicationInteropTests(
    SshRsyncContainer sshContainer,
    RsyncdContainer daemonContainer) :
    IClassFixture<SshRsyncContainer>,
    IClassFixture<RsyncdContainer>
{
    private static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(120);
    private sealed record ManifestEntry(string Path, string Type, long Size, string? Sha256, long MtimeSeconds);

    [Fact]
    public async Task SshPull_CombinedFlags_DeletesExtras_PreservesMtime_AndRerunTransfersNothing()
    {
        string id = Guid.NewGuid().ToString("N");
        string remoteRoot = $"/t/cli pull {id}";
        string localRoot = TempPath($"rsyncwin-cli-pull-{id}");
        string wrapperRoot = TempPath($"rsyncwin-cli-ssh-{id}");
        try
        {
            var setup = await sshContainer.ExecAsync("sh", "-c",
                $"mkdir -p {Quote(remoteRoot)}/nested && " +
                $"printf 'application pull' > {Quote(remoteRoot + "/small.txt")} && " +
                $"head -c 65536 /dev/zero > {Quote(remoteRoot + "/binary.bin")} && " +
                $"printf 'unicode' > {Quote(remoteRoot + "/中文 檔名.txt")} && " +
                $"printf 'nested' > {Quote(remoteRoot + "/nested/deep.txt")} && " +
                $"touch {Quote(remoteRoot + "/empty.bin")} && mkdir {Quote(remoteRoot + "/empty-dir")} && " +
                $"printf 'read only' > {Quote(remoteRoot + "/readonly.txt")} && chmod 444 {Quote(remoteRoot + "/readonly.txt")} && " +
                $"printf 'reserved' > {Quote(remoteRoot + "/CON")} && " +
                $"printf 'ads' > {Quote(remoteRoot + "/log.txt:hidden")} && " +
                $"printf 'backslash' > {Quote(remoteRoot + "/back\\slash.txt")} && " +
                $"printf 'escape' > {Quote(remoteRoot + $"/..\\..\\escaped-{id}.txt")} && " +
                $"printf 'trailing' > {Quote(remoteRoot + "/trailing.")} && " +
                $"find {Quote(remoteRoot)} -exec touch -d @1609459200 {{}} + && " +
                $"chown -R syncer:syncer {Quote(remoteRoot)}");
            Assert.Equal(0, setup.ExitCode);

            Directory.CreateDirectory(localRoot);
            await File.WriteAllTextAsync(Path.Combine(localRoot, "delete-me.txt"), "extra");
            string wrapper = await CliProcessRunner.CreateSshWrapperAsync(sshContainer, wrapperRoot);
            string remote = $"{sshContainer.User}@{sshContainer.Host}:{remoteRoot}/";

            CliProcessResult first = await CliProcessRunner.RunAsync(
                ["-acsz", "--delete", "-e", wrapper, remote, localRoot], CliTimeout);

            AssertSuccessful(first);
            Assert.Contains("files transferred: 11", first.StandardError, StringComparison.Ordinal);
            Assert.Contains("deleting", first.StandardError, StringComparison.Ordinal);
            Assert.Contains("delete-me.txt", first.StandardError, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(localRoot, "delete-me.txt")));
            AssertManifestsEqual(
                await RemoteManifestAsync(sshContainer, remoteRoot, mapForWindows: true),
                await LocalManifestAsync(localRoot));
            Assert.Equal(1609459200, new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(localRoot, "small.txt"))).ToUnixTimeSeconds());
            Assert.True(File.Exists(Path.Combine(localRoot, "CON_")));
            Assert.True(File.Exists(Path.Combine(localRoot, "log.txt_hidden")));
            Assert.True(File.Exists(Path.Combine(localRoot, "back_slash.txt")));
            Assert.True(File.Exists(Path.Combine(localRoot, $".._.._escaped-{id}.txt")));
            Assert.True(File.Exists(Path.Combine(localRoot, "trailing._")));
            Assert.False(File.Exists(Path.GetFullPath(Path.Combine(localRoot, "..", "..", $"escaped-{id}.txt"))));
            Assert.All(Directory.EnumerateFileSystemEntries(localRoot, "*", SearchOption.AllDirectories), path =>
                Assert.StartsWith(
                    Path.GetFullPath(localRoot) + Path.DirectorySeparatorChar,
                    Path.GetFullPath(path),
                    StringComparison.OrdinalIgnoreCase));
            Assert.Contains("mapped:", first.StandardError, StringComparison.Ordinal);

            CliProcessResult second = await CliProcessRunner.RunAsync(
                ["--recursive", "--times", "--checksum", "--protect-args", "--compress", "--delete",
                 "--rsh", wrapper, remote, localRoot], CliTimeout);

            AssertSuccessful(second);
            Assert.Contains("files transferred: 0, bytes: 0", second.StandardError, StringComparison.Ordinal);
            AssertManifestsEqual(
                await RemoteManifestAsync(sshContainer, remoteRoot, mapForWindows: true),
                await LocalManifestAsync(localRoot));
        }
        finally
        {
            await sshContainer.ExecAsync("rm", "-rf", remoteRoot);
            DeleteDirectory(localRoot);
            DeleteDirectory(wrapperRoot);
        }
    }

    [Fact]
    public async Task SshPush_CombinedFlags_DeletesRemoteExtras_AndRerunHasZeroLiteralBytes()
    {
        string id = Guid.NewGuid().ToString("N");
        string remoteRoot = $"/t/cli push {id}";
        string localRoot = TempPath($"rsyncwin-cli-push-{id}");
        string wrapperRoot = TempPath($"rsyncwin-cli-ssh-{id}");
        try
        {
            await CreateLocalTreeAsync(localRoot);
            var setup = await sshContainer.ExecAsync("sh", "-c",
                $"mkdir -p {Quote(remoteRoot)} && printf 'extra' > {Quote(remoteRoot + "/delete-me.txt")} && " +
                $"chown -R syncer:syncer {Quote(remoteRoot)}");
            Assert.Equal(0, setup.ExitCode);
            string wrapper = await CliProcessRunner.CreateSshWrapperAsync(sshContainer, wrapperRoot);
            string remote = $"{sshContainer.User}@{sshContainer.Host}:{remoteRoot}/";

            CliProcessResult first = await CliProcessRunner.RunAsync(
                ["-rtcz", "--protect-args", "--delete", "-e", wrapper, localRoot, remote], CliTimeout);

            AssertSuccessful(first);
            Assert.Contains("files sent: 6", first.StandardError, StringComparison.Ordinal);
            Assert.Contains("deleting delete-me.txt", first.StandardError, StringComparison.Ordinal);
            AssertManifestsEqual(await LocalManifestAsync(localRoot), await RemoteManifestAsync(sshContainer, remoteRoot));

            CliProcessResult second = await CliProcessRunner.RunAsync(
                ["-rtczs", "--delete", "--rsh", wrapper, localRoot, remote], CliTimeout);

            AssertSuccessful(second);
            Assert.Contains("files sent: 0, literal bytes: 0", second.StandardError, StringComparison.Ordinal);
            AssertManifestsEqual(await LocalManifestAsync(localRoot), await RemoteManifestAsync(sshContainer, remoteRoot));
        }
        finally
        {
            await sshContainer.ExecAsync("rm", "-rf", remoteRoot);
            DeleteDirectory(localRoot);
            DeleteDirectory(wrapperRoot);
        }
    }

    [Fact]
    public async Task DaemonCli_ModuleListing_AuthenticatedPull_AndWrongPasswordExitAreObservable()
    {
        string endpoint = $"rsync://{daemonContainer.Host}:{daemonContainer.Port}";
        string localRoot = TempPath($"rsyncwin-cli-daemon-pull-{Guid.NewGuid():N}");
        string failedRoot = TempPath($"rsyncwin-cli-daemon-failed-{Guid.NewGuid():N}");
        try
        {
            CliProcessResult list = await CliProcessRunner.RunAsync([$"{endpoint}/"], CliTimeout);
            AssertSuccessful(list);
            Assert.Contains("tree", list.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("push", list.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("secret", list.StandardOutput, StringComparison.Ordinal);

            string authenticatedEndpoint =
                $"rsync://{RsyncdContainer.AuthUser}@{daemonContainer.Host}:{daemonContainer.Port}/secret/";
            CliProcessResult pull = await CliProcessRunner.RunAsync(
                ["--archive", "--checksum", "--compress", authenticatedEndpoint, localRoot],
                CliTimeout,
                new Dictionary<string, string?> { ["RSYNC_PASSWORD"] = RsyncdContainer.AuthPassword });
            AssertSuccessful(pull);
            Assert.Contains("files transferred: 7", pull.StandardError, StringComparison.Ordinal);
            AssertManifestsEqual(await RemoteManifestAsync(daemonContainer, "/t/tree"), await LocalManifestAsync(localRoot));
            Assert.Equal(1577934245, new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(localRoot, "b001_small.txt"))).ToUnixTimeSeconds());

            Directory.CreateDirectory(failedRoot);
            CliProcessResult denied = await CliProcessRunner.RunAsync(
                ["-r", $"rsync://{RsyncdContainer.AuthUser}@{daemonContainer.Host}:{daemonContainer.Port}/secret/", failedRoot],
                CliTimeout,
                new Dictionary<string, string?> { ["RSYNC_PASSWORD"] = "wrong-password" });
            Assert.False(denied.TimedOut);
            Assert.Equal(5, denied.ExitCode);
            Assert.Contains("auth failed", denied.StandardError, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFileSystemEntries(failedRoot));
        }
        finally
        {
            DeleteDirectory(localRoot);
            DeleteDirectory(failedRoot);
        }
    }

    [Fact]
    public async Task DaemonCli_PushChecksumCompressionDelete_AndSyntaxErrorHaveExactExitCodes()
    {
        string localRoot = TempPath($"rsyncwin-cli-daemon-push-{Guid.NewGuid():N}");
        try
        {
            await daemonContainer.ResetPushModuleAsync();
            await CreateLocalTreeAsync(localRoot);
            var plant = await daemonContainer.ExecAsync("sh", "-c", "printf 'extra' > /t/dpush/delete-me.txt");
            Assert.Equal(0, plant.ExitCode);
            string endpoint = $"rsync://{daemonContainer.Host}:{daemonContainer.Port}/push/";

            CliProcessResult first = await CliProcessRunner.RunAsync(
                ["--recursive", "--times", "--checksum", "--compress", "--delete", localRoot, endpoint], CliTimeout);
            AssertSuccessful(first);
            Assert.Contains("deleting delete-me.txt", first.StandardError, StringComparison.Ordinal);
            AssertManifestsEqual(await LocalManifestAsync(localRoot), await RemoteManifestAsync(daemonContainer, "/t/dpush"));

            CliProcessResult second = await CliProcessRunner.RunAsync(
                ["-rtcz", "--delete", localRoot, endpoint], CliTimeout);
            AssertSuccessful(second);
            Assert.Contains("files sent: 0, literal bytes: 0", second.StandardError, StringComparison.Ordinal);

            CliProcessResult invalid = await CliProcessRunner.RunAsync(["--not-a-real-option"], TimeSpan.FromSeconds(15));
            Assert.False(invalid.TimedOut);
            Assert.Equal(1, invalid.ExitCode);
            Assert.Contains("unsupported option", invalid.StandardError, StringComparison.Ordinal);
        }
        finally
        {
            await daemonContainer.ResetPushModuleAsync();
            DeleteDirectory(localRoot);
        }
    }

    [Fact]
    public async Task DaemonPush_LockedChecksumSource_Exits23_TransfersOtherFiles_AndLeavesNoPartialFile()
    {
        string localRoot = TempPath($"rsyncwin-cli-locked-{Guid.NewGuid():N}");
        try
        {
            await daemonContainer.ResetPushModuleAsync();
            Directory.CreateDirectory(localRoot);
            await File.WriteAllTextAsync(Path.Combine(localRoot, "kept.txt"), "transfer despite sibling failure");
            string lockedPath = Path.Combine(localRoot, "locked.txt");
            await File.WriteAllTextAsync(lockedPath, "cannot be opened by the CLI");

            // This complements SshPushInteropTests' vanished-source gate: application coverage here
            // holds the source open across checksum precomputation and the real daemon request
            string endpoint = $"rsync://{daemonContainer.Host}:{daemonContainer.Port}/push/";
            CliProcessResult result;
            await using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                result = await CliProcessRunner.RunAsync(["-rc", localRoot, endpoint], CliTimeout);
            }

            Assert.False(result.TimedOut);
            Assert.Equal(23, result.ExitCode);
            Assert.Contains("locked.txt", result.StandardError, StringComparison.Ordinal);
            var remote = await daemonContainer.ExecAsync("sh", "-c",
                "test \"$(cat /t/dpush/kept.txt)\" = 'transfer despite sibling failure' && " +
                "test ! -e /t/dpush/locked.txt && " +
                "test -z \"$(find /t/dpush -mindepth 1 -name '.*' -print)\" && " +
                "test -z \"$(find /t/dpush -mindepth 1 -name '*rsyncwin*' -print)\" && echo OK");
            Assert.Equal(0, remote.ExitCode);
            Assert.Equal("OK", remote.StdOut.Trim());
        }
        finally
        {
            await daemonContainer.ResetPushModuleAsync();
            DeleteDirectory(localRoot);
        }
    }

    [Fact]
    public async Task DaemonDoubleColon_UsesDefaultPort_AndConnectionRefusalExits10()
    {
        // The live daemon fixture publishes a random host port, so successful application transfer
        // coverage uses daemon URLs above; this process gate proves double-colon classification and
        // its fixed port 873 without making the suite claim that the random fixture listens there
        CliProcessResult result = await CliProcessRunner.RunAsync(
            ["127.0.0.2::tree", TempPath($"rsyncwin-double-colon-{Guid.NewGuid():N}")],
            TimeSpan.FromSeconds(15));

        Assert.False(result.TimedOut);
        Assert.Equal(10, result.ExitCode);
        Assert.Contains("127.0.0.2:873", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("failed to connect to daemon", result.StandardError, StringComparison.Ordinal);
    }

    private static async Task CreateLocalTreeAsync(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        await File.WriteAllTextAsync(Path.Combine(root, "small.txt"), "application push");
        await File.WriteAllBytesAsync(Path.Combine(root, "binary.bin"), Enumerable.Range(0, 65536).Select(i => (byte)(i * 31)).ToArray());
        await File.WriteAllTextAsync(Path.Combine(root, "中文 檔名.txt"), "unicode");
        await File.WriteAllTextAsync(Path.Combine(root, "nested", "deep.txt"), "nested");
        await File.WriteAllBytesAsync(Path.Combine(root, "empty.bin"), []);
        Directory.CreateDirectory(Path.Combine(root, "empty-dir"));
        string readOnly = Path.Combine(root, "readonly.txt");
        await File.WriteAllTextAsync(readOnly, "read only but transferable");
        File.SetAttributes(readOnly, File.GetAttributes(readOnly) | FileAttributes.ReadOnly);

        DateTime fixedMtime = DateTimeOffset.FromUnixTimeSeconds(1609459200).UtcDateTime;
        foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(path, fixedMtime);
        foreach (string path in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
            Directory.SetLastWriteTimeUtc(path, fixedMtime);
    }

    private static async Task<SortedDictionary<string, ManifestEntry>> LocalManifestAsync(string root)
    {
        var manifest = new SortedDictionary<string, ManifestEntry>(StringComparer.Ordinal);
        foreach (string directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
            long mtime = new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory)).ToUnixTimeSeconds();
            manifest[relative] = new ManifestEntry(relative, "dir", 0, null, mtime);
        }
        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var info = new FileInfo(file);
            await using FileStream stream = File.OpenRead(file);
            manifest[relative] = new ManifestEntry(
                relative, "file", info.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream)),
                new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds());
        }
        return manifest;
    }

    private static async Task<SortedDictionary<string, ManifestEntry>> RemoteManifestAsync(
        SshRsyncContainer container, string root, bool mapForWindows = false)
    {
        var result = await container.ExecAsync("sh", "-c", RemoteManifestCommand(root));
        Assert.Equal(0, result.ExitCode);
        return ParseRemoteManifest(result.StdOut, mapForWindows);
    }

    private static async Task<SortedDictionary<string, ManifestEntry>> RemoteManifestAsync(
        RsyncdContainer container, string root, bool mapForWindows = false)
    {
        var result = await container.ExecAsync("sh", "-c", RemoteManifestCommand(root));
        Assert.Equal(0, result.ExitCode);
        return ParseRemoteManifest(result.StdOut, mapForWindows);
    }

    private static string RemoteManifestCommand(string root) =>
        $"cd {Quote(root)} && find . -mindepth 1 -print | sort | while IFS= read -r p; do " +
        "if test -f \"$p\"; then set -- $(sha256sum \"$p\"); " +
        "printf 'file\\t%s\\t%s\\t%s\\t%s\\n' \"$(stat -c %s \"$p\")\" \"$(stat -c %Y \"$p\")\" \"$1\" \"$p\"; " +
        "elif test -d \"$p\"; then printf 'dir\\t0\\t%s\\t-\\t%s\\n' \"$(stat -c %Y \"$p\")\" \"$p\"; fi; done";

    private static SortedDictionary<string, ManifestEntry> ParseRemoteManifest(string output, bool mapForWindows)
    {
        var manifest = new SortedDictionary<string, ManifestEntry>(StringComparer.Ordinal);
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.TrimEnd('\r').Split('\t', 5);
            Assert.Equal(5, fields.Length);
            string relative = fields[4].StartsWith("./", StringComparison.Ordinal)
                ? fields[4][2..]
                : fields[4];
            if (mapForWindows)
                relative = WindowsPathMapper.Map(relative).Mapped.Replace('\\', '/');
            manifest[relative] = new ManifestEntry(
                relative, fields[0], long.Parse(fields[1]), fields[3] == "-" ? null : fields[3], long.Parse(fields[2]));
        }
        return manifest;
    }

    private static void AssertManifestsEqual(
        SortedDictionary<string, ManifestEntry> expected,
        SortedDictionary<string, ManifestEntry> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach ((string path, ManifestEntry expectedEntry) in expected)
        {
            ManifestEntry actualEntry = actual[path];
            Assert.Equal(expectedEntry.Path, actualEntry.Path);
            Assert.Equal(expectedEntry.Type, actualEntry.Type);
            Assert.Equal(expectedEntry.Size, actualEntry.Size);
            Assert.Equal(expectedEntry.Sha256, actualEntry.Sha256);
            // rsync's wire mtime is whole seconds, while NTFS and container filesystems retain
            // sub-second values; one second is the explicit cross-filesystem precision allowance
            Assert.InRange(Math.Abs(expectedEntry.MtimeSeconds - actualEntry.MtimeSeconds), 0, 1);
        }
    }

    private static void AssertSuccessful(CliProcessResult result)
    {
        Assert.False(result.TimedOut, $"CLI timed out; stdout={result.StandardOutput}; stderr={result.StandardError}");
        Assert.True(result.ExitCode == 0, $"CLI exited {result.ExitCode}; stdout={result.StandardOutput}; stderr={result.StandardError}");
    }

    private static string TempPath(string name) => Path.Combine(Path.GetTempPath(), name);

    private static string Quote(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
