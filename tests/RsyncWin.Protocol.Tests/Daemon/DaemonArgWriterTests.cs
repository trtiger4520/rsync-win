using RsyncWin.Protocol.Daemon;

namespace RsyncWin.Protocol.Tests.Daemon;

public class DaemonArgWriterTests
{
    [Fact]
    public void Write_Protocol31_MatchesTheCapturedArgvSegment()
    {
        // daemon31-pull-rt c2s.bin: greeting line, then the module line "tree\n", then the argv
        // block (NUL-terminated words + a lone NUL terminator) right up against the checksum vstring.
        byte[] c2s = TestFixtures.Bytes("daemon31-pull-rt", "c2s.bin");
        (_, int afterGreeting) = DaemonVectorLines.ReadLine(c2s, offset: 0);
        (string moduleLine, int argvStart) = DaemonVectorLines.ReadLine(c2s, afterGreeting);
        Assert.Equal("tree", moduleLine);

        string[] words = ["--server", "--sender", "-tre.LsfxCIvu", ".", "tree/"];
        byte[] framed = DaemonArgWriter.Write(words, protocol: 31);

        Assert.Equal(c2s[argvStart..(argvStart + framed.Length)], framed);
    }

    [Fact]
    public void Write_Protocol29_MatchesTheCapturedArgvSegment()
    {
        // daemon29-auth-pull c2s.bin: greeting, module line "secret\n", auth reply line, then the
        // argv block ('\n'-separated words + a lone '\n' terminator) right against the binary phase.
        byte[] c2s = TestFixtures.Bytes("daemon29-auth-pull", "c2s.bin");
        (_, int afterGreeting) = DaemonVectorLines.ReadLine(c2s, offset: 0);
        (string moduleLine, int afterModule) = DaemonVectorLines.ReadLine(c2s, afterGreeting);
        Assert.Equal("secret", moduleLine);
        (_, int argvStart) = DaemonVectorLines.ReadLine(c2s, afterModule);

        string[] words = ["--server", "--sender", "-tr", ".", "secret/"];
        byte[] framed = DaemonArgWriter.Write(words, protocol: 29);

        Assert.Equal(c2s[argvStart..(argvStart + framed.Length)], framed);
    }
}
