using RsyncWin.Cli;

namespace RsyncWin.Cli.Tests;

/// <summary>Flag parsing and source/dest classification — pure, no I/O, exercised directly against
/// <see cref="CommandLineParser.Parse"/> without ever spawning ssh.exe.</summary>
public class CommandLineParserTests
{
    [Theory]
    [InlineData(@"D:\backup")]
    [InlineData("D:/backup")]
    [InlineData("D:")]
    public void IsLocalWindowsPath_DriveLetterForms_AreLocal(string spec)
    {
        Assert.True(CommandLineParser.IsLocalWindowsPath(spec));
        Assert.False(CommandLineParser.IsRemoteSpec(spec));
    }

    [Fact]
    public void IsLocalWindowsPath_UncPath_IsLocal()
    {
        Assert.True(CommandLineParser.IsLocalWindowsPath(@"\\server\share"));
    }

    [Fact]
    public void IsRemoteSpec_UserAtHostPath_IsRemote()
    {
        Assert.True(CommandLineParser.IsRemoteSpec("user@host:/path"));
    }

    [Fact]
    public void IsRemoteSpec_BareHostPath_IsRemote()
    {
        Assert.True(CommandLineParser.IsRemoteSpec("host:/path"));
    }

    [Fact]
    public void Parse_DriveLetterSourceRemoteDest_ClassifiesAsPush()
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["-r", @"D:\backup", "host:/src"]);

        Assert.Null(failure);
        Assert.Equal(ParsedAction.SshPush, command!.Action);
        Assert.Equal(@"D:\backup", command.Source);
        Assert.Equal("host:/src", command.Dest);
    }

    [Fact]
    public void Parse_RemoteSourceDriveLetterDest_ClassifiesAsPull()
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["-r", "host:/src", @"D:\backup"]);

        Assert.Null(failure);
        Assert.Equal(ParsedAction.SshPull, command!.Action);
        Assert.Equal("host:/src", command.Source);
        Assert.Equal(@"D:\backup", command.Dest);
    }

    [Fact]
    public void Parse_UnknownLongFlag_ReturnsSyntaxError()
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["--foo", "host:/src", @"D:\backup"]);

        Assert.Null(command);
        Assert.NotNull(failure);
        Assert.Contains("--foo", failure!.Message);
    }

    [Fact]
    public void Parse_UnknownShortFlag_ReturnsSyntaxError()
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["-z", "host:/src", @"D:\backup"]);

        Assert.Null(command);
        Assert.NotNull(failure);
        Assert.Contains("-z", failure!.Message);
    }

    [Fact]
    public void Parse_ChecksumFlag_SetsChecksumOnPull()
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["-r", "-c", "host:/src", @"D:\backup"]);

        Assert.Null(failure);
        Assert.True(command!.Checksum);
    }

    [Fact]
    public void Parse_DeleteWithoutRecurseOrArchive_IsRejected()
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["--delete", "host:/src", @"D:\backup"]);

        Assert.Null(command);
        Assert.NotNull(failure);
        Assert.Contains("--delete", failure!.Message);
    }

    [Fact]
    public void Parse_DeleteWithRecurse_SetsDeleteOnPull()
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["-r", "--delete", "host:/src", @"D:\backup"]);

        Assert.Null(failure);
        Assert.True(command!.Delete);
        Assert.Equal(ParsedAction.SshPull, command.Action);
    }

    [Fact]
    public void Parse_BundledShortFlags_ParsesRecurseTimesChecksum()
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["-rtc", "host:/src", @"D:\backup"]);

        Assert.Null(failure);
        Assert.True(command!.Recurse);
        Assert.True(command.Checksum);
    }

    [Fact]
    public void Parse_SecludedArgsShortFlag_IsRejected()
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["-s", "host:/src", @"D:\backup"]);

        Assert.Null(command);
        Assert.NotNull(failure);
        Assert.Contains("secluded-args", failure!.Message);
    }

    [Fact]
    public void Parse_SecludedArgsLongFlag_IsRejected()
    {
        (ParsedCommand? command, ParseFailure? failure) =
            CommandLineParser.Parse(["--secluded-args", "host:/src", @"D:\backup"]);

        Assert.Null(command);
        Assert.NotNull(failure);
        Assert.Contains("secluded-args", failure!.Message);
    }

    [Fact]
    public void Parse_BundledSecludedArgsShortFlag_IsRejected()
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["-rs", "host:/src", @"D:\backup"]);

        Assert.Null(command);
        Assert.NotNull(failure);
        Assert.Contains("secluded-args", failure!.Message);
    }

    [Fact]
    public void Parse_PushWithChecksum_IsRejected()
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["-r", "-c", @"D:\backup", "host:/dst"]);

        Assert.Null(command);
        Assert.NotNull(failure);
        Assert.Contains("push", failure!.Message);
    }

    [Fact]
    public void Parse_PushWithDelete_IsRejected()
    {
        (ParsedCommand? command, ParseFailure? failure) =
            CommandLineParser.Parse(["-r", "--delete", @"D:\backup", "host:/dst"]);

        Assert.Null(command);
        Assert.NotNull(failure);
        Assert.Contains("push", failure!.Message);
    }

    [Fact]
    public void Parse_LocalToLocal_IsRejected()
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["-r", @"D:\a", @"D:\b"]);

        Assert.Null(command);
        Assert.NotNull(failure);
    }

    [Fact]
    public void Parse_RemoteToRemote_IsRejected()
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["-r", "host1:/a", "host2:/b"]);

        Assert.Null(command);
        Assert.NotNull(failure);
    }

    [Fact]
    public void Parse_DaemonUrlSource_ClassifiesAsDaemonPull()
    {
        (ParsedCommand? command, ParseFailure? failure) =
            CommandLineParser.Parse(["-r", "rsync://host/module/path", @"D:\backup"]);

        Assert.Null(failure);
        Assert.Equal(ParsedAction.DaemonPull, command!.Action);
        Assert.Equal("module", command.Endpoint!.Module);
    }

    [Fact]
    public void Parse_BareDaemonEndpoint_ClassifiesAsDaemonList()
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["rsync://host/"]);

        Assert.Null(failure);
        Assert.Equal(ParsedAction.DaemonList, command!.Action);
    }
}
