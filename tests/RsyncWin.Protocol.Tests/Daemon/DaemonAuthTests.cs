using RsyncWin.Protocol.Daemon;

namespace RsyncWin.Protocol.Tests.Daemon;

/// <summary>
/// The load-bearing daemon-auth test: reproduces the captured client digest from the captured
/// challenge and the manifest's plaintext password, byte-for-byte, for every auth vector — including
/// the deliberately wrong password in <c>daemon31-auth-fail</c>, which proves the client's digest was
/// well-formed and the server rejected it, not that the client sent garbage.
/// </summary>
public class DaemonAuthTests
{
    [Theory]
    [InlineData("daemon31-auth-pull", 1)] // greeting, module ("secret") before the reply line
    [InlineData("daemon29-auth-pull", 1)]
    [InlineData("daemon31-auth-fail", 1)]
    public void ComputeDigest_ReproducesTheCapturedClientDigest(string vector, int moduleLineIndex)
    {
        byte[] s2c = TestFixtures.Bytes(vector, "s2c.bin");
        (_, int afterGreeting) = DaemonVectorLines.ReadLine(s2c, offset: 0);
        (string authLine, _) = DaemonVectorLines.ReadLine(s2c, afterGreeting);
        DaemonServerLine classified = DaemonServerLine.Classify(authLine);
        Assert.Equal(DaemonLineKind.AuthRequired, classified.Kind);
        string challenge = classified.Text!;

        byte[] c2s = TestFixtures.Bytes(vector, "c2s.bin");
        int offset = 0;
        for (int i = 0; i <= moduleLineIndex; i++)
            (_, offset) = DaemonVectorLines.ReadLine(c2s, offset);
        (string replyLine, _) = DaemonVectorLines.ReadLine(c2s, offset);
        string[] replyParts = replyLine.Split(' ');
        string user = replyParts[0];
        string capturedDigest = replyParts[1];

        string password = ReadManifestPassword(vector);

        Assert.Equal("alice", user);
        Assert.Equal(capturedDigest, DaemonAuth.ComputeDigest(password, challenge));
    }

    [Fact]
    public void FormatReply_MatchesTheCapturedReplyLine()
    {
        byte[] c2s = TestFixtures.Bytes("daemon31-auth-pull", "c2s.bin");
        (_, int afterGreeting) = DaemonVectorLines.ReadLine(c2s, offset: 0);
        (_, int afterModule) = DaemonVectorLines.ReadLine(c2s, afterGreeting);
        (string replyLine, _) = DaemonVectorLines.ReadLine(c2s, afterModule);

        Assert.Equal(replyLine + "\n", DaemonAuth.FormatReply("alice", "u0qCkuq+uGjvPV1avQikyw"));
    }

    private static string ReadManifestPassword(string vector)
    {
        foreach (string line in File.ReadAllLines(TestFixtures.PathOf(vector, "auth-manifest.txt")))
        {
            if (line.StartsWith("password=", StringComparison.Ordinal))
                return line["password=".Length..];
        }
        throw new InvalidOperationException($"{vector}/auth-manifest.txt has no password= line");
    }
}
