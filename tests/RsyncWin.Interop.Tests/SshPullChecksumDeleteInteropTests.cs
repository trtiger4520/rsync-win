using RsyncWin.Engine;
using RsyncWin.Protocol.Session;
using RsyncWin.Transport;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// P9 live gates over ssh.exe against a real rsync 3.4.3: `--checksum` bypasses the mtime+size fast
/// path (an in-place same-size, same-mtime content change IS re-pulled), and `--delete` on a pull
/// removes extraneous local destination entries (the deletion is local — the server never sees it).
/// Own container fixture instance, so mutating remote trees here never disturbs other classes.
/// </summary>
[Trait("Category", "Interop")]
public sealed class SshPullChecksumDeleteInteropTests(SshRsyncContainer container) : IClassFixture<SshRsyncContainer>
{
    /// <summary>
    /// The `-c` discriminator no plain-mtime pull can catch: overwrite a file in place with the SAME
    /// byte length and reset its mtime to the original, so size+mtime match exactly while the content
    /// differs. The fast path would skip it forever; `--checksum` must re-pull exactly that one file.
    /// </summary>
    [Fact]
    public async Task ChecksumPull_ReTransfersASameSizeSameMtimeContentChange()
    {
        var setup = await container.ExecAsync("sh", "-c",
            "mkdir -p /t/cksum && " +
            "printf 'alpha stays put\\n' > /t/cksum/a.txt && " +      // 16 bytes, never changes
            "printf 'bravo content ##\\n' > /t/cksum/b.txt && " +     // 17 bytes, rewritten same-length below
            "touch -d '2021-06-15 12:00:00' /t/cksum/a.txt /t/cksum/b.txt /t/cksum && " +
            "chown -R syncer:syncer /t/cksum");
        Assert.Equal(0, setup.ExitCode);

        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-cksum-{Guid.NewGuid():N}");
        try
        {
            var argv = new ServerArgvBuilder { Sender = true, Recurse = true, Checksum = true, Paths = ["/t/cksum/"] };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

            // ---- pull #1: full transfer of both files ------------------------------------------
            await using (var t1 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build())))
            {
                PullSession.Result first = await PullSession.RunAsync(t1, argv, dest, cts.Token);
                Assert.Equal(2, first.TransferredFiles);
                Assert.Empty(first.FailedFiles);
                Assert.Equal(0, await t1.WaitForExitAsync(cts.Token));
            }
            Assert.Equal("bravo content ##\n", await File.ReadAllTextAsync(Path.Combine(dest, "b.txt"), cts.Token));

            // ---- pull #2: identical dest under -c transfers nothing (content matches) -----------
            await using (var t2 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build())))
            {
                PullSession.Result second = await PullSession.RunAsync(t2, argv, dest, cts.Token);
                Assert.Equal(0, second.TransferredFiles);
                Assert.Equal(0, await t2.WaitForExitAsync(cts.Token));
            }

            // ---- in-place, SAME-length rewrite of b.txt + restore the ORIGINAL mtime -----------
            // 'bravo content ##\n' and 'BRAVO CONTENT !!\n' are both 17 bytes, so the size is
            // unchanged; the mtime is forced back to the original second.
            var rewrite = await container.ExecAsync("sh", "-c",
                "printf 'BRAVO CONTENT !!\\n' > /t/cksum/b.txt && touch -d '2021-06-15 12:00:00' /t/cksum/b.txt");
            Assert.Equal(0, rewrite.ExitCode);

            // ---- pull #3 under -c: exactly b.txt re-transfers; a.txt stays on the fast path -----
            await using (var t3 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build())))
            {
                PullSession.Result third = await PullSession.RunAsync(t3, argv, dest, cts.Token);
                Assert.Equal(1, third.TransferredFiles);
                Assert.Empty(third.FailedFiles);
                Assert.Equal(0, await t3.WaitForExitAsync(cts.Token));
            }
            Assert.Equal("BRAVO CONTENT !!\n", await File.ReadAllTextAsync(Path.Combine(dest, "b.txt"), cts.Token));
            Assert.Equal("alpha stays put\n", await File.ReadAllTextAsync(Path.Combine(dest, "a.txt"), cts.Token));

            // ---- pull #4 under -c: stable again, nothing to do ---------------------------------
            await using var t4 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
            PullSession.Result fourth = await PullSession.RunAsync(t4, argv, dest, cts.Token);
            Assert.Equal(0, fourth.TransferredFiles);
            Assert.Equal(0, await t4.WaitForExitAsync(cts.Token));
        }
        finally
        {
            try { Directory.Delete(dest, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    /// <summary>
    /// The other `-c` discriminator: a file whose content still matches but whose LOCAL mtime has
    /// gone stale (touched, not re-synced) must be fixed up via the attribute-only `0x0008` wire
    /// path — ndx+iflags, no sum head, never posted to the reply channel — not a full transfer. This
    /// proves the generator's request, the server's echo, and our consume path never hang against a
    /// live peer (P9 adversarial review gap: PullChecksumReplayTests covers this hermetically; this
    /// is the live-wire confirmation).
    /// </summary>
    [Fact]
    public async Task ChecksumPull_FixesAnAttributeOnlyStaleMtime_WithoutRetransferring()
    {
        var setup = await container.ExecAsync("sh", "-c",
            "mkdir -p /t/attrfix && " +
            "printf 'alpha stays put\\n' > /t/attrfix/a.txt && " +
            "printf 'bravo unchanged\\n' > /t/attrfix/b.txt && " +
            "touch -d '2021-06-15 12:00:00' /t/attrfix/a.txt /t/attrfix/b.txt /t/attrfix && " +
            "chown -R syncer:syncer /t/attrfix");
        Assert.Equal(0, setup.ExitCode);

        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-attrfix-{Guid.NewGuid():N}");
        try
        {
            var argv = new ServerArgvBuilder { Sender = true, Recurse = true, Checksum = true, Paths = ["/t/attrfix/"] };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

            // ---- pull #1: full transfer of both files into an empty dest -----------------------
            await using (var t1 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build())))
            {
                PullSession.Result first = await PullSession.RunAsync(t1, argv, dest, cts.Token);
                Assert.Equal(2, first.TransferredFiles);
                Assert.Empty(first.FailedFiles);
                Assert.Equal(0, await t1.WaitForExitAsync(cts.Token));
            }
            string bPath = Path.Combine(dest, "b.txt");
            DateTime originalMtime = File.GetLastWriteTimeUtc(bPath);
            string originalContent = await File.ReadAllTextAsync(bPath, cts.Token);

            // ---- stale ONE dest file's mtime LOCALLY, content left untouched -------------------
            File.SetLastWriteTimeUtc(bPath, new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            // ---- pull #2 under -c: attribute-only fix, not a transfer --------------------------
            await using (var t2 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build())))
            {
                PullSession.Result second = await PullSession.RunAsync(t2, argv, dest, cts.Token);
                Assert.Equal(0, second.TransferredFiles); // 0x0008 consumed without a hang, not a transfer
                Assert.Empty(second.FailedFiles);
                Assert.Equal(0, await t2.WaitForExitAsync(cts.Token));
            }

            Assert.Equal(originalContent, await File.ReadAllTextAsync(bPath, cts.Token)); // content untouched
            Assert.Equal(originalMtime, File.GetLastWriteTimeUtc(bPath)); // mtime corrected back to the source's
        }
        finally
        {
            try { Directory.Delete(dest, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    /// <summary>
    /// `--delete` on a pull: after syncing a tree, an extraneous local file and directory in the
    /// destination must be removed (locally — the server argv carries no --delete and no del-stats
    /// cross the wire), the synced files must survive, and a re-run must delete/transfer nothing.
    /// </summary>
    [Fact]
    public async Task DeletePull_RemovesExtraneousLocalEntries_ThenIsStable()
    {
        var setup = await container.ExecAsync("sh", "-c",
            "mkdir -p /t/deltree/sub && " +
            "printf 'keep one\\n' > /t/deltree/keep1.txt && " +
            "printf 'keep two\\n' > /t/deltree/sub/keep2.txt && " +
            "touch -d '2021-06-15 12:00:00' /t/deltree/keep1.txt /t/deltree/sub/keep2.txt /t/deltree/sub /t/deltree && " +
            "chown -R syncer:syncer /t/deltree");
        Assert.Equal(0, setup.ExitCode);

        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-del-{Guid.NewGuid():N}");
        try
        {
            var argv = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/deltree/"] };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

            // ---- pull #1 (no delete) establishes the destination tree --------------------------
            await using (var t1 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build())))
            {
                PullSession.Result first = await PullSession.RunAsync(t1, argv, dest, cts.Token);
                Assert.Equal(2, first.TransferredFiles);
                Assert.Equal(0, await t1.WaitForExitAsync(cts.Token));
            }

            // ---- plant extraneous local entries the source does not have -----------------------
            string extraFile = Path.Combine(dest, "extra.txt");
            string extraDir = Path.Combine(dest, "extradir");
            await File.WriteAllTextAsync(extraFile, "delete me", cts.Token);
            Directory.CreateDirectory(extraDir);
            await File.WriteAllTextAsync(Path.Combine(extraDir, "inside.txt"), "delete me too", cts.Token);

            // ---- pull #2 with --delete: the extras go, the synced files stay -------------------
            await using (var t2 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build())))
            {
                PullSession.Result second = await PullSession.RunAsync(t2, argv, dest, cts.Token, delete: true);
                Assert.Empty(second.FailedFiles);
                Assert.Equal(0, await t2.WaitForExitAsync(cts.Token));
            }

            Assert.False(File.Exists(extraFile), "extraneous file should have been deleted");
            Assert.False(Directory.Exists(extraDir), "extraneous directory should have been deleted");
            Assert.True(File.Exists(Path.Combine(dest, "keep1.txt")));
            Assert.True(File.Exists(Path.Combine(dest, "sub", "keep2.txt")));
            Assert.Equal("keep one\n", await File.ReadAllTextAsync(Path.Combine(dest, "keep1.txt"), cts.Token));

            // ---- pull #3 with --delete: stable -- nothing to transfer or delete ----------------
            await using var t3 = OpenSshProcessTransport.Start(
                OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
            PullSession.Result third = await PullSession.RunAsync(t3, argv, dest, cts.Token, delete: true);
            Assert.Equal(0, third.TransferredFiles);
            Assert.Empty(third.FailedFiles);
            Assert.Equal(0, await t3.WaitForExitAsync(cts.Token));
            Assert.True(File.Exists(Path.Combine(dest, "keep1.txt")));
        }
        finally
        {
            try { Directory.Delete(dest, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }
}
