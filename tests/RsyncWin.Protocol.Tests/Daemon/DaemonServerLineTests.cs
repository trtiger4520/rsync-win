using RsyncWin.Protocol.Daemon;

namespace RsyncWin.Protocol.Tests.Daemon;

public class DaemonServerLineTests
{
    [Fact]
    public void Classify_Ok()
        => Assert.Equal(new DaemonServerLine(DaemonLineKind.Ok), DaemonServerLine.Classify("@RSYNCD: OK"));

    [Fact]
    public void Classify_Exit()
        => Assert.Equal(new DaemonServerLine(DaemonLineKind.Exit), DaemonServerLine.Classify("@RSYNCD: EXIT"));

    [Fact]
    public void Classify_AuthRequired_ExtractsTheChallenge()
    {
        DaemonServerLine line = DaemonServerLine.Classify("@RSYNCD: AUTHREQD NA08xsBNp7g58/f/MZdzCA");

        Assert.Equal(DaemonLineKind.AuthRequired, line.Kind);
        Assert.Equal("NA08xsBNp7g58/f/MZdzCA", line.Text);
    }

    [Fact]
    public void Classify_Error_ExtractsTheMessage()
    {
        DaemonServerLine line = DaemonServerLine.Classify("@ERROR: Unknown module 'nonexistent'");

        Assert.Equal(DaemonLineKind.Error, line.Kind);
        Assert.Equal("Unknown module 'nonexistent'", line.Text);
    }

    [Fact]
    public void Classify_AnythingElse_IsText()
    {
        DaemonServerLine line = DaemonServerLine.Classify("Welcome to the rsyncwin capture daemon");

        Assert.Equal(DaemonLineKind.Text, line.Kind);
        Assert.Equal("Welcome to the rsyncwin capture daemon", line.Text);
    }
}
