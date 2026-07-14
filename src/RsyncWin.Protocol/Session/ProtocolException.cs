namespace RsyncWin.Protocol.Session;

/// <summary>
/// A semantic protocol failure that maps to a specific rsync exit code — e.g. no common protocol
/// version (exit 2). Distinct from <see cref="InvalidDataException"/>, which the codecs throw for
/// stream-level garbage and which maps to exit 12.
/// </summary>
public sealed class ProtocolException(RsyncExitCode exitCode, string message) : Exception(message)
{
    public RsyncExitCode ExitCode { get; } = exitCode;
}
