using RsyncWin.Engine;
using RsyncWin.Protocol.Session;
using RsyncWin.Transport;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// The P3 gate, live: a complete <c>--list-only</c> session against a real rsync — handshake,
/// filter list, flist decode, sort, stats, goodbye — ending with the remote side exiting 0.
/// Exit 0 is the strongest possible assertion: the server ran its entire protocol state machine
/// against our bytes and found nothing to complain about.
/// </summary>
[Trait("Category", "Interop")]
public sealed class SshListOnlyInteropTests(SshRsyncContainer container) : IClassFixture<SshRsyncContainer>
{
    [Fact]
    public async Task ListOnly_MatchesTheContainerTree_AndExitsZero()
    {
        var argv = new ServerArgvBuilder
        {
            Sender = true,
            Recurse = true,
            ListOnly = true,
            Paths = ["/t/tree/"],
        };
        await using var transport = OpenSshProcessTransport.Start(
            OpenSshProcessTransport.DefaultSshExePath, container.SshArgs(argv.Build()));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        ListOnlySession.Result result = await ListOnlySession.RunAsync(transport, argv, cts.Token);

        Assert.Equal(31, result.Session.Protocol);
        Assert.Equal(
            [
                ".", "b000_empty", "b001_small.txt", "b002_64k.bin", "b003_300k.bin",
                "b004_中文檔名.txt", "b005 name with space.txt", "subdir", "subdir/nested.txt",
            ],
            result.FileList.Entries.Select(e => e.Name));

        var byName = result.FileList.Entries.ToDictionary(e => e.Name);
        Assert.Equal(0, byName["b000_empty"].Size);
        Assert.Equal(12, byName["b001_small.txt"].Size);
        Assert.Equal(65536, byName["b002_64k.bin"].Size);
        Assert.Equal(300000, byName["b003_300k.bin"].Size);
        Assert.Equal(4, byName["b004_中文檔名.txt"].Size);
        Assert.Equal(5, byName["b005 name with space.txt"].Size);
        Assert.Equal(7, byName["subdir/nested.txt"].Size);
        Assert.True(byName["."].IsDirectory);
        Assert.True(byName["subdir"].IsDirectory);
        Assert.Equal(0, result.FileList.IoError);

        // The sender's stats block self-validates: total_size is the sum of non-dir sizes.
        long nonDirBytes = result.FileList.Entries.Where(e => !e.IsDirectory).Sum(e => e.Size);
        Assert.Equal(nonDirBytes, result.Stats.TotalSize);

        Assert.Equal(0, await transport.WaitForExitAsync(cts.Token));
    }
}
