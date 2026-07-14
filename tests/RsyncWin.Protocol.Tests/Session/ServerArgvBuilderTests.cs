using RsyncWin.Protocol.Session;

namespace RsyncWin.Protocol.Tests.Session;

/// <summary>
/// Every captured <c>argv.txt</c> golden records what a real rsync 3.4.3 client asked the server to
/// exec for a given option set; our builder must reproduce each word-for-word, or the server ends up
/// in a different mode than the session assumes.
/// </summary>
public class ServerArgvBuilderTests
{
    public static TheoryData<string, ServerArgvBuilder> Goldens => new()
    {
        { "ssh31-pull-rt", new() { Sender = true, Recurse = true, Paths = ["/t/tree/"] } },
        { "ssh30-pull-rt", new() { Sender = true, Recurse = true, Protocol = 30, Paths = ["/t/tree/"] } },
        { "ssh29-pull-rt", new() { Sender = true, Recurse = true, Protocol = 29, Paths = ["/t/tree/"] } },
        {
            "ssh31-pull-a",
            new()
            {
                Sender = true, Recurse = true, PreserveLinks = true, PreserveOwner = true,
                PreserveGroup = true, PreserveDevices = true, PreservePerms = true, Paths = ["/t/tree/"],
            }
        },
        { "ssh31-pull-delta", new() { Sender = true, Paths = ["/t/tree/b003_300k.bin"] } },
        { "ssh31-sizes-list", new() { Sender = true, Recurse = true, ListOnly = true, Paths = ["/t/sizes/"] } },
        { "ssh31-push-rt", new() { Sender = false, Recurse = true, Paths = ["/t/pushdst/"] } },
        { "ssh31-push-nsec1", new() { Sender = false, Recurse = true, Paths = ["/t/nsdst/"] } },
        { "ssh31-push-delta", new() { Sender = false, Paths = ["/t/pushdelta/"] } },
        { "ssh31-pull-checksum", new() { Sender = true, Recurse = true, Checksum = true, Paths = ["/t/c1src/"] } },
        // P10: push --checksum (server gets the same -c bundle letter as a pull) and push --delete
        // (the server IS the receiver, so its argv carries --delete after the bundle).
        { "ssh31-push-checksum", new() { Sender = false, Recurse = true, Checksum = true, Paths = ["/t/pcdst/"] } },
        { "ssh31-push-delete", new() { Sender = false, Recurse = true, Delete = true, Paths = ["/t/pddst/"] } },
        // P10: --secluded-args (-s) — 's' leads the bundle and the ". <paths>" tail is dropped
        // (the paths travel as a pre-handshake NUL list instead).
        { "ssh31-secluded-pull", new() { Sender = true, Recurse = true, SecludedArgs = true, Paths = ["/t/tree/"] } },
        { "ssh31-secluded-push", new() { Sender = false, Recurse = true, SecludedArgs = true, Paths = ["/t/spushdst/"] } },
        // P10: -z forces zlibx via --new-compress (no 'z' bundle letter, no compression string).
        { "ssh31-pull-z-zlibx", new() { Sender = true, Recurse = true, Compress = true, Paths = ["/t/ztree/"] } },
        // ssh31-push-redo is deliberately NOT a golden here: its capture used --bwlimit=200 (a
        // capture-time throttle to stretch the transfer for the redo window, see capture.sh) which
        // is outside the option set ServerArgvBuilder supports — otherwise it is the same "-t"
        // shape as push-delta.
    };

    [Theory]
    [MemberData(nameof(Goldens))]
    public void Build_MatchesTheCapturedArgv(string vector, ServerArgvBuilder builder)
        => Assert.Equal(TestFixtures.Lines(vector, "argv.txt"), builder.Build());

    [Fact]
    public void CapabilityLetters_AreOmittedBelowProtocol30()
    {
        var argv = new ServerArgvBuilder { Sender = true, Protocol = 29, Paths = ["/x"] }.Build();
        Assert.DoesNotContain(argv, a => a.Contains("e."));
    }

    [Fact]
    public void Checksum_LandsAfterRecurse_WithArchiveLetters()
    {
        var argv = new ServerArgvBuilder
        {
            Sender = true, Recurse = true, PreserveLinks = true, PreserveOwner = true,
            PreserveGroup = true, PreserveDevices = true, PreservePerms = true, Checksum = true,
            Paths = ["/t/tree/"],
        }.Build();
        Assert.Contains("-logDtprce.LsfxCIvu", argv);
    }
}
