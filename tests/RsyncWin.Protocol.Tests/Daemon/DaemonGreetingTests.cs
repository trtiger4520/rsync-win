using System.Text;
using RsyncWin.Protocol.Daemon;

namespace RsyncWin.Protocol.Tests.Daemon;

public class DaemonGreetingTests
{
    [Fact]
    public void Parse_ReadsTheCapturedServerGreeting()
    {
        byte[] s2c = TestFixtures.Bytes("daemon31-pull-rt", "s2c.bin");
        (string line, _) = DaemonVectorLines.ReadLine(s2c, offset: 0);

        (int version, int subversion, IReadOnlyList<string> digests) = DaemonGreeting.Parse(line);

        Assert.Equal(32, version);
        Assert.Equal(0, subversion);
        Assert.Equal(["md5", "md4"], digests);
    }

    [Theory]
    [InlineData("daemon31-pull-rt", 31)]
    [InlineData("daemon30-pull-rt", 30)]
    [InlineData("daemon29-auth-pull", 29)]
    public void FormatClient_MatchesTheCapturedClientGreeting(string vector, int protocol)
    {
        byte[] c2s = TestFixtures.Bytes(vector, "c2s.bin");
        (string capturedLine, int nextOffset) = DaemonVectorLines.ReadLine(c2s, offset: 0);
        byte[] captured = c2s[..nextOffset];

        byte[] formatted = Encoding.UTF8.GetBytes(DaemonGreeting.FormatClient(protocol));

        Assert.Equal(captured, formatted);
        Assert.Equal($"@RSYNCD: {protocol}.0 md5 md4", capturedLine);
    }

    [Fact]
    public void Parse_RejectsALineWithoutThePrefix()
        => Assert.Throws<InvalidDataException>(() => DaemonGreeting.Parse("not a greeting"));
}
