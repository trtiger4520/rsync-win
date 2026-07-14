using System.ComponentModel;
using System.Net.Sockets;
using RsyncWin.Cli;
using RsyncWin.Protocol.Session;

namespace RsyncWin.Cli.Tests;

/// <summary>One assertion per rsync exit code <see cref="ExitCodeMapper"/> is responsible for,
/// including the ssh-255-takes-precedence ordering fix for <see cref="InvalidDataException"/>.</summary>
public class ExitCodeMapperTests
{
    [Fact]
    public void Ok_IsZero()
    {
        // No exception is ever mapped for the success path — this just pins the numeric value the
        // rest of the CLI returns verbatim on a clean run.
        Assert.Equal(0, (int)RsyncExitCode.Ok);
    }

    [Fact]
    public void ProtocolException_ProtocolIncompatibility_MapsToTwo()
    {
        var ex = new ProtocolException(RsyncExitCode.ProtocolIncompatibility, "no common protocol version");
        Assert.Equal(RsyncExitCode.ProtocolIncompatibility, ExitCodeMapper.Map(ex));
    }

    [Fact]
    public void ProtocolException_FileSelectionError_MapsToThree()
    {
        var ex = new ProtocolException(RsyncExitCode.FileSelectionError, "bad file selection");
        Assert.Equal(RsyncExitCode.FileSelectionError, ExitCodeMapper.Map(ex));
    }

    [Fact]
    public void ProtocolException_UnsupportedAction_MapsToFour()
    {
        var ex = new ProtocolException(RsyncExitCode.UnsupportedAction, "unsupported action");
        Assert.Equal(RsyncExitCode.UnsupportedAction, ExitCodeMapper.Map(ex));
    }

    [Fact]
    public void Win32Exception_MapsToFive()
    {
        var ex = new Win32Exception("failed to start ssh.exe");
        Assert.Equal(RsyncExitCode.StartClientServerError, ExitCodeMapper.Map(ex));
    }

    [Fact]
    public void InvalidDataException_WithSsh255_MapsToFive_NotTwelve()
    {
        // The ordering fix: ssh itself dying is the real cause, the dead wire stream is a symptom.
        var ex = new InvalidDataException("stream is desynced");
        Assert.Equal(RsyncExitCode.StartClientServerError, ExitCodeMapper.Map(ex, sshExitCode: 255));
    }

    [Fact]
    public void SocketException_MapsToTen()
    {
        var ex = new SocketException((int)SocketError.ConnectionRefused);
        Assert.Equal(RsyncExitCode.SocketIoError, ExitCodeMapper.Map(ex));
    }

    [Fact]
    public void IOException_MapsToEleven()
    {
        var ex = new IOException("disk full");
        Assert.Equal(RsyncExitCode.FileIoError, ExitCodeMapper.Map(ex));
    }

    [Fact]
    public void UnauthorizedAccessException_MapsToEleven()
    {
        var ex = new UnauthorizedAccessException("access denied");
        Assert.Equal(RsyncExitCode.FileIoError, ExitCodeMapper.Map(ex));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    public void InvalidDataException_WithoutSsh255_MapsToTwelve(int? sshExitCode)
    {
        var ex = new InvalidDataException("stream is desynced");
        Assert.Equal(RsyncExitCode.ProtocolStreamError, ExitCodeMapper.Map(ex, sshExitCode));
    }

    [Fact]
    public void ProtocolException_PartialTransferError_MapsToTwentyThree()
    {
        var ex = new ProtocolException(RsyncExitCode.PartialTransferError, "partial transfer");
        Assert.Equal(RsyncExitCode.PartialTransferError, ExitCodeMapper.Map(ex));
    }

    [Fact]
    public void ProtocolException_PartialTransferVanished_MapsToTwentyFour()
    {
        var ex = new ProtocolException(RsyncExitCode.PartialTransferVanished, "source vanished");
        Assert.Equal(RsyncExitCode.PartialTransferVanished, ExitCodeMapper.Map(ex));
    }

    [Fact]
    public void ProtocolException_Timeout_MapsToThirty()
    {
        var ex = new ProtocolException(RsyncExitCode.Timeout, "no keep-alive");
        Assert.Equal(RsyncExitCode.Timeout, ExitCodeMapper.Map(ex));
    }

    [Fact]
    public void ProtocolException_WithSsh255_PrefersStartClientServerError()
    {
        // Even a ProtocolException that would otherwise carry its own specific code defers to ssh
        // having died — the protocol-level error is downstream noise from the dead transport.
        var ex = new ProtocolException(RsyncExitCode.ProtocolIncompatibility, "no common protocol version");
        Assert.Equal(RsyncExitCode.StartClientServerError, ExitCodeMapper.Map(ex, sshExitCode: 255));
    }
}
