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

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void Parse_HelpFlag_ReturnsShowHelp(string flag)
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse([flag]);

        Assert.Null(failure);
        Assert.Equal(ParsedAction.ShowHelp, command!.Action);
    }

    [Theory]
    [InlineData("-h", "host:/src", @"D:\backup")] // help alongside a valid transfer
    [InlineData("-rh", "host:/src", @"D:\backup")] // bundled with other short flags
    [InlineData("--help", "--foo")] // help wins even over an otherwise-unsupported option
    public void Parse_HelpFlag_WinsOverOtherArguments(params string[] args)
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(args);

        Assert.Null(failure);
        Assert.Equal(ParsedAction.ShowHelp, command!.Action);
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
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["-q", "host:/src", @"D:\backup"]);

        Assert.Null(command);
        Assert.NotNull(failure);
        Assert.Contains("-q", failure!.Message);
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
    public void Parse_SecludedArgsShortFlag_IsAccepted()
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["-s", "host:/src", @"D:\backup"]);

        Assert.Null(failure);
        Assert.Equal(ParsedAction.SshPull, command!.Action);
        Assert.True(command.Secluded);
    }

    [Fact]
    public void Parse_SecludedArgsLongFlag_IsAccepted()
    {
        (ParsedCommand? command, ParseFailure? failure) =
            CommandLineParser.Parse(["--secluded-args", "host:/src", @"D:\backup"]);

        Assert.Null(failure);
        Assert.True(command!.Secluded);
    }

    [Fact]
    public void Parse_ProtectArgsAlias_IsAccepted()
    {
        (ParsedCommand? command, ParseFailure? failure) =
            CommandLineParser.Parse(["--protect-args", "host:/src", @"D:\backup"]);

        Assert.Null(failure);
        Assert.True(command!.Secluded);
    }

    [Fact]
    public void Parse_BundledSecludedArgsShortFlag_IsAccepted()
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["-rs", "host:/src", @"D:\backup"]);

        Assert.Null(failure);
        Assert.True(command!.Recurse);
        Assert.True(command.Secluded);
    }

    [Theory]
    [InlineData("-z")]
    [InlineData("--compress")]
    public void Parse_CompressFlag_IsAccepted(string flag)
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse([flag, "host:/src", @"D:\backup"]);

        Assert.Null(failure);
        Assert.True(command!.Compress);
    }

    [Fact]
    public void Parse_BundledCompressWithRecurse_IsAccepted()
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["-rz", @"D:\backup", "host:/dst"]);

        Assert.Null(failure);
        Assert.Equal(ParsedAction.SshPush, command!.Action);
        Assert.True(command.Compress);
        Assert.True(command.Recurse);
    }

    [Fact]
    public void Parse_PushWithChecksum_IsAccepted()
    {
        // P10: push --checksum is now supported (F_SUM emission) — no longer rejected.
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["-r", "-c", @"D:\backup", "host:/dst"]);

        Assert.Null(failure);
        Assert.Equal(ParsedAction.SshPush, command!.Action);
        Assert.True(command.Checksum);
    }

    [Fact]
    public void Parse_PushWithDelete_IsAccepted()
    {
        // P10: push --delete is now supported (MSG_DELETED handling) — no longer rejected.
        (ParsedCommand? command, ParseFailure? failure) =
            CommandLineParser.Parse(["-r", "--delete", @"D:\backup", "host:/dst"]);

        Assert.Null(failure);
        Assert.Equal(ParsedAction.SshPush, command!.Action);
        Assert.True(command.Delete);
    }

    [Theory]
    [InlineData(@"D:\a", @"D:\b")]
    [InlineData("D:/a", "D:/b")]
    [InlineData(@"D:\a\", @"D:\b")] // trailing separator survives into Source verbatim
    [InlineData(@"\\server\share\a", @"D:\b")] // UNC counts as local
    [InlineData("relative-src", "relative-dst")] // no drive letter at all is local too
    public void Parse_LocalToLocal_ClassifiesAsLocalCopy(string source, string dest)
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["-r", source, dest]);

        Assert.Null(failure);
        Assert.Equal(ParsedAction.LocalCopy, command!.Action);
        Assert.Equal(source, command.Source);
        Assert.Equal(dest, command.Dest);
        Assert.Null(command.Endpoint);
    }

    [Fact]
    public void Parse_LocalToLocalDeleteWithoutRecurse_IsStillRejected()
    {
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["--delete", @"D:\a", @"D:\b"]);

        Assert.Null(command);
        Assert.NotNull(failure);
        Assert.Contains("--delete", failure!.Message);
    }

    [Fact]
    public void Parse_LocalToLocalWithCompressAndSecluded_IsAcceptedAsNoOp()
    {
        // -z/-s mean nothing without a wire; they parse fine and the local path just never reads them.
        (ParsedCommand? command, ParseFailure? failure) = CommandLineParser.Parse(["-rzs", @"D:\a", @"D:\b"]);

        Assert.Null(failure);
        Assert.Equal(ParsedAction.LocalCopy, command!.Action);
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

    [Fact]
    public void Parse_ProgressFlag_SetsProgressNotProgress2()
    {
        (ParsedCommand? command, ParseFailure? failure) =
            CommandLineParser.Parse(["--progress", "host:/src", @"D:\backup"]);

        Assert.Null(failure);
        Assert.True(command!.Progress);
        Assert.False(command.InfoProgress2);
    }

    [Fact]
    public void Parse_InfoProgress2_SetsProgress2NotPerFile()
    {
        (ParsedCommand? command, ParseFailure? failure) =
            CommandLineParser.Parse(["--info=progress2", "host:/src", @"D:\backup"]);

        Assert.Null(failure);
        Assert.True(command!.InfoProgress2);
        Assert.False(command.Progress);
    }

    [Theory]
    [InlineData("--info=progress")]
    [InlineData("--info=progress1")]
    public void Parse_InfoProgressPerFileForms_SetPerFileProgress(string flag)
    {
        (ParsedCommand? command, ParseFailure? failure) =
            CommandLineParser.Parse([flag, "host:/src", @"D:\backup"]);

        Assert.Null(failure);
        Assert.True(command!.Progress);
        Assert.False(command.InfoProgress2);
    }

    [Fact]
    public void Parse_UnsupportedInfoFlag_ReturnsSyntaxError()
    {
        (ParsedCommand? command, ParseFailure? failure) =
            CommandLineParser.Parse(["--info=stats2", "host:/src", @"D:\backup"]);

        Assert.Null(command);
        Assert.NotNull(failure);
        Assert.Contains("stats2", failure!.Message);
    }

    [Fact]
    public void Parse_DashCapitalP_IsStillRejected()
    {
        // -P (= --partial --progress) is not implemented: --partial's keep-partial-on-failure
        // semantics are a receiver behavior change, so -P stays rejected (docs/progress-spec.md).
        (ParsedCommand? command, ParseFailure? failure) =
            CommandLineParser.Parse(["-P", "host:/src", @"D:\backup"]);

        Assert.Null(command);
        Assert.NotNull(failure);
        Assert.Contains("-P", failure!.Message);
    }

    [Theory]
    [InlineData("ssh -p 2222", new[] { "ssh", "-p", "2222" })]
    [InlineData("ssh", new[] { "ssh" })]
    // A bare Windows path: backslashes are literal (NOT escapes) and there is no whitespace, so it
    // stays a single token — identical to the pre-passthrough behavior for a plain executable path.
    [InlineData(@"C:\Windows\System32\OpenSSH\ssh.exe", new[] { @"C:\Windows\System32\OpenSSH\ssh.exe" })]
    // Runs of whitespace collapse; leading/trailing whitespace is ignored.
    [InlineData("  ssh   -p   2222  ", new[] { "ssh", "-p", "2222" })]
    // A single-quoted path with spaces survives as one token; \P etc. would be corrupted if '\' escaped.
    [InlineData(@"'C:\Program Files\Git\usr\bin\ssh.exe' -p 2222", new[] { @"C:\Program Files\Git\usr\bin\ssh.exe", "-p", "2222" })]
    // Same, double-quoted.
    [InlineData("\"C:\\Program Files\\ssh.exe\" -i key", new[] { @"C:\Program Files\ssh.exe", "-i", "key" })]
    public void SplitRsh_QuoteAware_SplitsProgramAndArgs(string command, string[] expected)
    {
        Assert.Equal(expected, CommandLineParser.SplitRsh(command));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void SplitRsh_EmptyOrWhitespace_ReturnsEmpty(string command)
    {
        Assert.Empty(CommandLineParser.SplitRsh(command));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("''")]
    public void SplitRsh_QuotedEmpty_YieldsOneEmptyToken(string command)
    {
        // A quoted-empty command tokenizes to a single empty word (not "no tokens"). ResolveRsh
        // relies on this: an empty program word means "no rsh", so it falls back to the in-box ssh.exe
        // instead of trying to launch "".
        Assert.Equal(new[] { "" }, CommandLineParser.SplitRsh(command));
    }

    [Fact]
    public void Parse_RshWithArgs_KeepsRawCommandUnsplit()
    {
        // The split happens at ssh-launch time, not parse time: RshOverride carries the raw command
        // string verbatim so a spaces-containing -e value is a single positional-free argument.
        (ParsedCommand? command, ParseFailure? failure) =
            CommandLineParser.Parse(["-e", "ssh -p 2222", "host:/src", @"D:\backup"]);

        Assert.Null(failure);
        Assert.Equal(ParsedAction.SshPull, command!.Action);
        Assert.Equal("ssh -p 2222", command.RshOverride);
    }
}
