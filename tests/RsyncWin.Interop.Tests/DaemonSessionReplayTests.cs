using System.Security.Cryptography;
using System.Text;
using RsyncWin.Engine;
using RsyncWin.Fs;
using RsyncWin.Protocol;
using RsyncWin.Protocol.Daemon;
using RsyncWin.Protocol.Mux;
using RsyncWin.Protocol.Session;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Hermetic (not trait-gated): replays captured daemon (<c>rsync://</c>) sessions — textual preamble
/// plus the identical-to-ssh binary phase — through <see cref="DaemonPreamble"/> and the existing role
/// sessions. Mirrors <see cref="PullSessionReplayTests"/>/<see cref="PushSessionReplayTests"/>'s
/// pattern: <see cref="ScriptedTransport"/> is fed the FULL captured <c>s2c.bin</c> (textual preamble
/// included), and every written byte — preamble text and binary alike — is compared against the
/// captured <c>c2s.bin</c>.
/// </summary>
public class DaemonSessionReplayTests
{
    // SHA-256 of b002_64k.bin from the shared capture tree (RsyncWin.Interop.Tests.PullSessionReplayTests).
    private const string B002Sha256 = "afdca747c04f45e53b27ad99e68b67fb3b8155e7ea846d0e32ffb916122a9c75";

    private static readonly DateTime T1 = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2021, 6, 15, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T3 = new(2022, 12, 31, 23, 59, 59, DateTimeKind.Utc);

    private static byte[] Capture(string vector, string file) =>
        File.ReadAllBytes(Path.Combine(FindVectors(), vector, file));

    private static string FindVectors()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "test-fixtures", "vectors");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("test-fixtures/vectors not found above the test binary");
    }

    /// <summary>
    /// Splits a captured line-based byte blob on '\n', dropping the trailing empty entry the final
    /// terminator produces. An independent decode of "what lines did the wire actually carry" — no
    /// assumption about which lines are motd vs. greeting vs. module-listing.
    /// </summary>
    private static List<string> DecodeLines(byte[] captured)
    {
        List<string> lines = [.. Encoding.UTF8.GetString(captured).Split('\n')];
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>
    /// Shape check for the module-listing format (docs/daemon-spec.md §1: <c>printf "%-15s\t%s\n"</c>).
    /// A second, independent copy of the same documented rule <see cref="DaemonPreamble"/> uses, kept
    /// here so the test has its own oracle rather than trusting the SUT's own classification.
    /// </summary>
    private static bool IsModuleListingShaped(string line) => line.Length >= 16 && line[15] == '\t';

    /// <summary>
    /// Strips mux framing down to the logical MSG_DATA payload stream. For the captured server side
    /// (<paramref name="strict"/> <c>false</c>, the default) non-Data frames are silently dropped —
    /// the real server is trusted. For OUR OWN written stream (F4a, adversarial review) pass
    /// <paramref name="strict"/> <c>true</c>: any tag other than <see cref="MessageTag.Data"/> means we
    /// spuriously emitted an out-of-band frame, which should fail the byte compare loudly instead of
    /// silently vanishing from the demuxed stream.
    /// </summary>
    private static byte[] Demux(byte[] framed, bool strict = false)
    {
        var logical = new MemoryStream();
        for (int offset = 0; offset + MuxHeader.Size <= framed.Length;)
        {
            MuxHeader header = MuxHeader.Read(framed.AsSpan(offset));
            offset += MuxHeader.Size;
            if (header.Tag == MessageTag.Data)
                logical.Write(framed, offset, header.PayloadLength);
            else if (strict)
                throw new InvalidOperationException(
                    $"our own written stream emitted an unexpected non-Data mux frame (tag {header.Tag}, {header.PayloadLength} bytes)");
            offset += header.PayloadLength;
        }
        return logical.ToArray();
    }

    /// <summary>
    /// The captured client sends the flist in genuine readdir order (both ends sort only after
    /// receipt). Independently decoded from every daemon push vector's own <c>c2s.bin</c> by locating
    /// each name's literal (or prefix-compressed suffix) bytes: identical across
    /// <c>daemon31-push-rt</c>, <c>daemon31-push-uptodate</c>, and <c>daemon31-push-readonly</c>
    /// — same container run, same underlying <c>/t/tree</c> — and, verified, identical to
    /// <c>ssh31-push-rt</c>'s <c>PushSessionReplayTests.CapturedTreeReaddirOrder</c>.
    /// </summary>
    private static readonly string[] CapturedTreeReaddirOrder =
    [
        ".", "b002_64k.bin", "b005 name with space.txt", "b001_small.txt", "b000_empty",
        "b004_中文檔名.txt", "b003_300k.bin", "subdir", "subdir/nested.txt",
    ];

    /// <summary>
    /// F4a (adversarial review): a spurious out-of-band mux frame on OUR OWN written side must fail
    /// the byte compare loudly, not vanish. Builds one Data frame followed by one Error frame and
    /// confirms strict mode throws while the lenient (server-side) default still drops it.
    /// </summary>
    [Fact]
    public void Demux_Strict_ThrowsOnNonDataFrame_LenientDefaultStillDrops()
    {
        byte[] dataPayload = "hello"u8.ToArray();
        var dataHeader = new MuxHeader(MessageTag.Data, dataPayload.Length);
        byte[] errorPayload = "oops"u8.ToArray();
        var errorHeader = new MuxHeader(MessageTag.Error, errorPayload.Length);

        var framed = new byte[MuxHeader.Size * 2 + dataPayload.Length + errorPayload.Length];
        dataHeader.Write(framed.AsSpan(0, MuxHeader.Size));
        dataPayload.CopyTo(framed.AsSpan(MuxHeader.Size));
        int errorOffset = MuxHeader.Size + dataPayload.Length;
        errorHeader.Write(framed.AsSpan(errorOffset, MuxHeader.Size));
        errorPayload.CopyTo(framed.AsSpan(errorOffset + MuxHeader.Size));

        Assert.Equal(dataPayload, Demux(framed)); // lenient default: server-side behavior, unchanged
        Assert.Throws<InvalidOperationException>(() => Demux(framed, strict: true));
    }

    private static List<EnumeratedEntry> PatchDirectorySizes(IReadOnlyList<EnumeratedEntry> entries) =>
        [.. entries.Select(e => e.Wire.IsDirectory ? e with { Wire = e.Wire with { Size = 4096 } } : e)];

    private static List<EnumeratedEntry> ReorderByName(IReadOnlyList<EnumeratedEntry> entries, IReadOnlyList<string> namesInOrder)
    {
        Dictionary<string, EnumeratedEntry> byName = entries.ToDictionary(e => e.Wire.Name);
        return [.. namesInOrder.Select(name => byName[name])];
    }

    private static async Task CreateSourceTreeAsync(
        string dest, byte[] b002Content, byte[] b003Content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(dest, "subdir"));

        async Task WriteAsync(string relative, byte[] content, DateTime mtimeUtc)
        {
            string path = Path.Combine(dest, relative);
            await File.WriteAllBytesAsync(path, content, cancellationToken);
            File.SetLastWriteTimeUtc(path, mtimeUtc);
        }

        await WriteAsync("b000_empty", [], T1);
        await WriteAsync("b001_small.txt", "hello rsync\n"u8.ToArray(), T1);
        await WriteAsync("b002_64k.bin", b002Content, T2);
        await WriteAsync("b003_300k.bin", b003Content, T2);
        await WriteAsync("b004_中文檔名.txt", "unicode name\n"u8.ToArray(), T3);
        await WriteAsync("b005 name with space.txt", "space name\n"u8.ToArray(), T3);
        await WriteAsync(Path.Combine("subdir", "nested.txt"), "nested\n"u8.ToArray(), T1);

        Directory.SetLastWriteTimeUtc(Path.Combine(dest, "subdir"), T1);
        Directory.SetLastWriteTimeUtc(dest, T1);
    }

    /// <summary>Byte length of the textual prologue (greeting + module/auth lines + argv block)
    /// our own client wrote, computed from the same pieces <see cref="DaemonPreamble"/> writes —
    /// never hardcoded, since the digest/challenge and protocol digit width can vary.</summary>
    private static int TextualPrologueLength(IEnumerable<string> lines, byte[] argvBytes) =>
        lines.Sum(Encoding.UTF8.GetByteCount) + argvBytes.Length;

    private static readonly HandshakeOptions XxhFor31 = new() { ChecksumOffer = "xxh128", PreNegotiatedProtocolVersion = 31 };

    /// <summary>
    /// F1 (adversarial review): the preamble negotiates <c>min(ours, server)</c> and would otherwise
    /// accept 29, but the binary phase (FileListReader etc.) only implements 30/31 — a protocol-29
    /// daemon would pass the preamble then silently desync mid-flist. A scripted greeting claiming
    /// "29.0" (no full vector needed — the floor check fires before any binary bytes are read) must
    /// make the preamble throw before writing the module line.
    /// </summary>
    [Fact]
    public async Task Preamble_ThrowsWhenNegotiatedProtocolBelow30_NothingWrittenAfterClientGreeting()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        byte[] s2c = Encoding.UTF8.GetBytes("@RSYNCD: 29.0 md5 md4\n");
        var serverArgs = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["tree/"] };
        await using var transport = new ScriptedTransport(s2c);

        ProtocolException ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            DaemonPreamble.RunAsync(transport, "tree", serverArgs, maxProtocol: 31, cancellationToken: cts.Token));

        Assert.Equal(RsyncExitCode.ProtocolIncompatibility, ex.ExitCode);
        Assert.Contains("29", ex.Message);
        Assert.Contains("30", ex.Message);

        byte[] written = await transport.WrittenBytesAsync();
        byte[] expectedClientGreeting = Encoding.UTF8.GetBytes(DaemonGreeting.FormatClient(29));
        Assert.Equal(expectedClientGreeting, written); // only our client greeting — no module line
    }

    /// <summary>
    /// F3 (adversarial review): <see cref="DaemonPreamble"/>'s line reader must cap the accumulated
    /// line length (4096 bytes) rather than buffering forever waiting for a '\n' that never arrives —
    /// a hostile or broken daemon streaming garbage should fail promptly with a protocol-stream error,
    /// not hang. The test itself is timeout-guarded so a regression back to unbounded buffering fails
    /// fast rather than hanging the suite.
    /// </summary>
    [Fact]
    public async Task Preamble_UnboundedGreetingLine_ThrowsPromptlyInsteadOfHanging()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        byte[] s2c = new byte[8192]; // no '\n' anywhere, well past the 4096-byte cap
        Array.Fill(s2c, (byte)'x');
        var serverArgs = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["tree/"] };
        await using var transport = new ScriptedTransport(s2c);

        ProtocolException ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            DaemonPreamble.RunAsync(transport, "tree", serverArgs, maxProtocol: 31, cancellationToken: cts.Token));

        Assert.Equal(RsyncExitCode.ProtocolStreamError, ex.ExitCode);
    }

    [Fact]
    public async Task PullRt_PreambleAndSessionByteIdenticalToCapture()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-daemon-pull-{Guid.NewGuid():N}");
        try
        {
            byte[] s2c = Capture("daemon31-pull-rt", "s2c.bin");
            var serverArgs = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["tree/"] };
            await using var transport = new ScriptedTransport(s2c);

            DaemonPreambleResult preamble = await DaemonPreamble.RunAsync(
                transport, "tree", serverArgs, maxProtocol: 31, cancellationToken: cts.Token);
            Assert.Equal(31, preamble.Protocol);
            Assert.Empty(preamble.MotdLines);

            PullSession.Result result = await PullSession.RunAsync(
                transport, serverArgs, dest, cts.Token, handshake: XxhFor31);

            Assert.Equal(7, result.TransferredFiles);
            Assert.Empty(result.FailedFiles);
            Assert.Equal(
                B002Sha256,
                Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(dest, "b002_64k.bin")))));

            byte[] written = await transport.WrittenBytesAsync();
            byte[] captured = Capture("daemon31-pull-rt", "c2s.bin");

            List<string> argvWords = [.. (serverArgs with { Protocol = preamble.Protocol }).Build().Skip(1)];
            Assert.Equal(["--server", "--sender", "-tre.LsfxCIvu", ".", "tree/"], argvWords);
            byte[] argvBytes = DaemonArgWriter.Write(argvWords, preamble.Protocol);

            int prologueLength = TextualPrologueLength(
                [DaemonGreeting.FormatClient(preamble.Protocol), "tree\n"], argvBytes);
            Assert.Equal(captured[..prologueLength], written[..prologueLength]);

            int ourOffset = prologueLength + 1 + "xxh128".Length;
            int capturedOffset = prologueLength + 1 + "xxh128 xxh3 xxh64 md5 md4".Length;
            Assert.Equal(Demux(captured[capturedOffset..]), Demux(written[ourOffset..], strict: true));
        }
        finally
        {
            try { Directory.Delete(dest, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public async Task ModuleList_ReturnsModuleLines_AndByteIdenticalGreetingRequest()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        byte[] s2c = Capture("daemon31-modlist", "s2c.bin");
        await using var transport = new ScriptedTransport(s2c);

        DaemonModuleListResult result = await DaemonPreamble.ListModulesAsync(
            transport, maxProtocol: 31, cancellationToken: cts.Token);

        List<string> allLines = DecodeLines(s2c);
        List<string> body = allLines[1..^1]; // strip the greeting and the terminal "@RSYNCD: EXIT"
        List<string> expectedModuleLines = [.. body.Where(IsModuleListingShaped)];
        List<string> expectedMotdLines = [.. body.Where(l => !IsModuleListingShaped(l))];

        Assert.Empty(expectedMotdLines);
        Assert.Equal(3, expectedModuleLines.Count);
        Assert.Equal(expectedModuleLines, result.ModuleLines);
        Assert.Empty(result.MotdLines);

        byte[] written = await transport.WrittenBytesAsync();
        byte[] captured = Capture("daemon31-modlist", "c2s.bin");
        Assert.Equal(23, captured.Length);
        Assert.Equal(captured, written);
    }

    [Fact]
    public async Task MotdList_SeparatesMotdFromTheSingleModuleLine()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        byte[] s2c = Capture("daemon31-motd-list", "s2c.bin");
        await using var transport = new ScriptedTransport(s2c);

        DaemonModuleListResult result = await DaemonPreamble.ListModulesAsync(
            transport, maxProtocol: 31, cancellationToken: cts.Token);

        List<string> allLines = DecodeLines(s2c);
        List<string> body = allLines[1..^1];
        List<string> expectedModuleLines = [.. body.Where(IsModuleListingShaped)];
        List<string> expectedMotdLines = [.. body.Where(l => !IsModuleListingShaped(l))];

        // Capture has 2 motd text lines + 1 blank separator line + 1 module line.
        Assert.Equal(3, expectedMotdLines.Count);
        Assert.Single(expectedModuleLines);
        Assert.Equal(expectedMotdLines, result.MotdLines);
        Assert.Equal(expectedModuleLines, result.ModuleLines);

        byte[] written = await transport.WrittenBytesAsync();
        byte[] captured = Capture("daemon31-motd-list", "c2s.bin");
        Assert.Equal(captured, written);
    }

    [Fact]
    public async Task AuthPull_ReplyLineByteIdentical_SessionCompletes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-daemon-auth-{Guid.NewGuid():N}");
        try
        {
            byte[] s2c = Capture("daemon31-auth-pull", "s2c.bin");
            string challenge = DaemonServerLine.Classify(
                DecodeLines(s2c).First(l => l.StartsWith("@RSYNCD: AUTHREQD ", StringComparison.Ordinal))).Text!;
            string expectedDigest = DaemonAuth.ComputeDigest("opensesame", challenge);
            string expectedReplyLine = DaemonAuth.FormatReply("alice", expectedDigest);

            var serverArgs = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["secret/"] };
            await using var transport = new ScriptedTransport(s2c);

            DaemonPreambleResult preamble = await DaemonPreamble.RunAsync(
                transport, "secret", serverArgs, user: "alice", password: "opensesame",
                maxProtocol: 31, cancellationToken: cts.Token);
            Assert.Equal(31, preamble.Protocol);

            PullSession.Result result = await PullSession.RunAsync(
                transport, serverArgs, dest, cts.Token, handshake: XxhFor31);

            Assert.Equal(7, result.TransferredFiles);
            Assert.Empty(result.FailedFiles);
            Assert.Equal(
                B002Sha256,
                Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(dest, "b002_64k.bin")))));

            byte[] written = await transport.WrittenBytesAsync();
            byte[] captured = Capture("daemon31-auth-pull", "c2s.bin");

            List<string> argvWords = [.. (serverArgs with { Protocol = preamble.Protocol }).Build().Skip(1)];
            byte[] argvBytes = DaemonArgWriter.Write(argvWords, preamble.Protocol);
            int prologueLength = TextualPrologueLength(
                [DaemonGreeting.FormatClient(preamble.Protocol), "secret\n", expectedReplyLine], argvBytes);

            // Byte-identity of just the auth reply line, independently computed above.
            int replyLineStart = Encoding.UTF8.GetByteCount(DaemonGreeting.FormatClient(preamble.Protocol))
                + Encoding.UTF8.GetByteCount("secret\n");
            int replyLineLength = Encoding.UTF8.GetByteCount(expectedReplyLine);
            Assert.Equal(
                captured[replyLineStart..(replyLineStart + replyLineLength)],
                written[replyLineStart..(replyLineStart + replyLineLength)]);

            Assert.Equal(captured[..prologueLength], written[..prologueLength]);

            int ourOffset = prologueLength + 1 + "xxh128".Length;
            int capturedOffset = prologueLength + 1 + "xxh128 xxh3 xxh64 md5 md4".Length;
            Assert.Equal(Demux(captured[capturedOffset..]), Demux(written[ourOffset..], strict: true));
        }
        finally
        {
            try { Directory.Delete(dest, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public async Task BadModule_ThrowsWithServerTextAndExitFive_NothingWrittenAfterModuleLine()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        byte[] s2c = Capture("daemon31-badmodule", "s2c.bin");
        var serverArgs = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["nonexistent/"] };
        await using var transport = new ScriptedTransport(s2c);

        ProtocolException ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            DaemonPreamble.RunAsync(transport, "nonexistent", serverArgs, maxProtocol: 31, cancellationToken: cts.Token));

        Assert.Equal(RsyncExitCode.StartClientServerError, ex.ExitCode); // exit 5
        Assert.Equal("Unknown module 'nonexistent'", ex.Message);

        byte[] written = await transport.WrittenBytesAsync();
        byte[] captured = Capture("daemon31-badmodule", "c2s.bin");
        Assert.Equal(captured, written); // greeting + module line only
    }

    [Fact]
    public async Task AuthFail_ReplyLineByteIdentical_ThenErrorExitFive()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        byte[] s2c = Capture("daemon31-auth-fail", "s2c.bin");
        string challenge = DaemonServerLine.Classify(
            DecodeLines(s2c).First(l => l.StartsWith("@RSYNCD: AUTHREQD ", StringComparison.Ordinal))).Text!;
        string expectedDigest = DaemonAuth.ComputeDigest("wrongpass", challenge);
        string expectedReplyLine = DaemonAuth.FormatReply("alice", expectedDigest);

        var serverArgs = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["secret/"] };
        await using var transport = new ScriptedTransport(s2c);

        ProtocolException ex = await Assert.ThrowsAsync<ProtocolException>(() =>
            DaemonPreamble.RunAsync(
                transport, "secret", serverArgs, user: "alice", password: "wrongpass",
                maxProtocol: 31, cancellationToken: cts.Token));

        Assert.Equal(RsyncExitCode.StartClientServerError, ex.ExitCode); // exit 5
        Assert.Equal("auth failed on module secret", ex.Message);

        byte[] written = await transport.WrittenBytesAsync();
        byte[] captured = Capture("daemon31-auth-fail", "c2s.bin");
        Assert.Equal(captured, written); // greeting + module line + our auth reply, nothing more

        int replyLineStart = Encoding.UTF8.GetByteCount(DaemonGreeting.FormatClient(31))
            + Encoding.UTF8.GetByteCount("secret\n");
        int replyLineLength = Encoding.UTF8.GetByteCount(expectedReplyLine);
        Assert.Equal(
            captured[replyLineStart..(replyLineStart + replyLineLength)],
            written[replyLineStart..(replyLineStart + replyLineLength)]);
    }

    [Fact]
    public async Task PushRt_PreambleAndSessionByteIdenticalToCapture()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string sourceDir = Path.Combine(Path.GetTempPath(), $"rsyncwin-daemon-push-{Guid.NewGuid():N}");
        try
        {
            byte[] b002;
            {
                string pullDest = Path.Combine(Path.GetTempPath(), $"rsyncwin-daemon-push-src-{Guid.NewGuid():N}");
                try
                {
                    await using var pullTransport = new ScriptedTransport(Capture("ssh31-pull-rt", "s2c.bin"));
                    await PullSession.RunAsync(
                        pullTransport, new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["/t/tree/"] },
                        pullDest, cts.Token, handshake: new HandshakeOptions { ChecksumOffer = "xxh128" });
                    b002 = await File.ReadAllBytesAsync(Path.Combine(pullDest, "b002_64k.bin"), cts.Token);
                }
                finally
                {
                    try { Directory.Delete(pullDest, recursive: true); }
                    catch (DirectoryNotFoundException) { }
                }
            }
            byte[] b003 = Capture("ssh31-push-delta", "result.bin");

            Directory.CreateDirectory(sourceDir);
            await CreateSourceTreeAsync(sourceDir, b002, b003, cts.Token);

            List<EnumeratedEntry> entries = ReorderByName(
                PatchDirectorySizes(FileEnumerator.Enumerate(sourceDir)), CapturedTreeReaddirOrder);

            byte[] s2c = Capture("daemon31-push-rt", "s2c.bin");
            var serverArgs = new ServerArgvBuilder { Sender = false, Recurse = true, Paths = ["push/"] };
            await using var transport = new ScriptedTransport(s2c);

            DaemonPreambleResult preamble = await DaemonPreamble.RunAsync(
                transport, "push", serverArgs, maxProtocol: 31, cancellationToken: cts.Token);
            Assert.Equal(31, preamble.Protocol);

            PushSession.Result result = await PushSession.RunAsync(
                transport, serverArgs, entries, cts.Token, handshake: XxhFor31);

            Assert.Equal(7, result.FilesSent);
            Assert.Equal(2, result.AttributeOnlyReplies);
            Assert.Empty(result.FailedFiles);

            byte[] written = await transport.WrittenBytesAsync();
            byte[] captured = Capture("daemon31-push-rt", "c2s.bin");

            List<string> argvWords = [.. (serverArgs with { Protocol = preamble.Protocol }).Build().Skip(1)];
            Assert.Equal(["--server", "-tre.LsfxCIvu", ".", "push/"], argvWords);
            byte[] argvBytes = DaemonArgWriter.Write(argvWords, preamble.Protocol);

            int prologueLength = TextualPrologueLength(
                [DaemonGreeting.FormatClient(preamble.Protocol), "push\n"], argvBytes);
            Assert.Equal(captured[..prologueLength], written[..prologueLength]);

            int ourOffset = prologueLength + 1 + "xxh128".Length;
            int capturedOffset = prologueLength + 1 + "xxh128 xxh3 xxh64 md5 md4".Length;
            Assert.Equal(Demux(captured[capturedOffset..]), Demux(written[ourOffset..], strict: true));
        }
        finally
        {
            try { Directory.Delete(sourceDir, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    [Fact]
    public async Task PushUpToDate_ZeroTransfers_ByteIdenticalToCapture()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string sourceDir = Path.Combine(Path.GetTempPath(), $"rsyncwin-daemon-push-uptodate-{Guid.NewGuid():N}");
        try
        {
            byte[] b002 = new byte[65536];
            byte[] b003 = new byte[300000];

            Directory.CreateDirectory(sourceDir);
            await CreateSourceTreeAsync(sourceDir, b002, b003, cts.Token);

            List<EnumeratedEntry> entries = ReorderByName(
                PatchDirectorySizes(FileEnumerator.Enumerate(sourceDir)), CapturedTreeReaddirOrder);

            byte[] s2c = Capture("daemon31-push-uptodate", "s2c.bin");
            var serverArgs = new ServerArgvBuilder { Sender = false, Recurse = true, Paths = ["push/"] };
            await using var transport = new ScriptedTransport(s2c);

            DaemonPreambleResult preamble = await DaemonPreamble.RunAsync(
                transport, "push", serverArgs, maxProtocol: 31, cancellationToken: cts.Token);
            Assert.Equal(31, preamble.Protocol);

            PushSession.Result result = await PushSession.RunAsync(
                transport, serverArgs, entries, cts.Token, handshake: XxhFor31);

            Assert.Equal(0, result.FilesSent);
            Assert.Equal(0, result.AttributeOnlyReplies);
            Assert.Empty(result.FailedFiles);

            byte[] written = await transport.WrittenBytesAsync();
            byte[] captured = Capture("daemon31-push-uptodate", "c2s.bin");

            List<string> argvWords = [.. (serverArgs with { Protocol = preamble.Protocol }).Build().Skip(1)];
            byte[] argvBytes = DaemonArgWriter.Write(argvWords, preamble.Protocol);
            int prologueLength = TextualPrologueLength(
                [DaemonGreeting.FormatClient(preamble.Protocol), "push\n"], argvBytes);
            Assert.Equal(captured[..prologueLength], written[..prologueLength]);

            int ourOffset = prologueLength + 1 + "xxh128".Length;
            int capturedOffset = prologueLength + 1 + "xxh128 xxh3 xxh64 md5 md4".Length;
            Assert.Equal(Demux(captured[capturedOffset..]), Demux(written[ourOffset..], strict: true));
        }
        finally
        {
            try { Directory.Delete(sourceDir, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    /// <summary>
    /// docs/daemon-spec.md §5: a push to a read-only module passes the textual preamble (OK, compat,
    /// seed) then errors in-mux — MSG_ERROR text frames followed by MSG_ERROR_EXIT carrying the real
    /// exit code (1). Confirms the error text and exit code surface through
    /// <see cref="PushSession.RunAsync"/> (via the <see cref="ProtocolException"/> enrichment in
    /// <c>PushSession.cs</c>) without needing to reproduce the client's own len-0 MSG_ERROR_EXIT echo:
    /// the captured <c>c2s.bin</c> for this vector ends at the flist (95 bytes shorter than the
    /// textual+offer prologue would otherwise predict for an echo byte), i.e. the real client's echo,
    /// if any, is not observable in this capture — so it is asserted structurally here (error text +
    /// exit code) rather than reproduced byte-for-byte.
    /// </summary>
    [Fact]
    public async Task PushReadOnly_SurfacesServerErrorTextAndExitCodeOne()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string sourceDir = Path.Combine(Path.GetTempPath(), $"rsyncwin-daemon-push-readonly-{Guid.NewGuid():N}");
        try
        {
            byte[] b002 = new byte[65536];
            byte[] b003 = new byte[300000];

            Directory.CreateDirectory(sourceDir);
            await CreateSourceTreeAsync(sourceDir, b002, b003, cts.Token);

            List<EnumeratedEntry> entries = ReorderByName(
                PatchDirectorySizes(FileEnumerator.Enumerate(sourceDir)), CapturedTreeReaddirOrder);

            byte[] s2c = Capture("daemon31-push-readonly", "s2c.bin");
            var serverArgs = new ServerArgvBuilder { Sender = false, Recurse = true, Paths = ["tree/"] };
            await using var transport = new ScriptedTransport(s2c);

            DaemonPreambleResult preamble = await DaemonPreamble.RunAsync(
                transport, "tree", serverArgs, maxProtocol: 31, cancellationToken: cts.Token);
            Assert.Equal(31, preamble.Protocol);

            ProtocolException ex = await Assert.ThrowsAsync<ProtocolException>(() =>
                PushSession.RunAsync(transport, serverArgs, entries, cts.Token, handshake: XxhFor31));

            Assert.Equal(RsyncExitCode.SyntaxError, ex.ExitCode); // carried exit code 1
            Assert.Contains("module is read only", ex.Message);

            byte[] written = await transport.WrittenBytesAsync();
            byte[] captured = Capture("daemon31-push-readonly", "c2s.bin");

            List<string> argvWords = [.. (serverArgs with { Protocol = preamble.Protocol }).Build().Skip(1)];
            Assert.Equal(["--server", "-tre.LsfxCIvu", ".", "tree/"], argvWords);
            byte[] argvBytes = DaemonArgWriter.Write(argvWords, preamble.Protocol);

            int prologueLength = TextualPrologueLength(
                [DaemonGreeting.FormatClient(preamble.Protocol), "tree\n"], argvBytes);
            Assert.Equal(captured[..prologueLength], written[..prologueLength]);

            // Whole flist, no further phase (the server errors before any generator request) — no
            // client-side echo is observable in this capture (see the method doc).
            int ourOffset = prologueLength + 1 + "xxh128".Length;
            int capturedOffset = prologueLength + 1 + "xxh128 xxh3 xxh64 md5 md4".Length;
            Assert.Equal(Demux(captured[capturedOffset..]), Demux(written[ourOffset..], strict: true));
        }
        finally
        {
            try { Directory.Delete(sourceDir, recursive: true); }
            catch (DirectoryNotFoundException) { }
        }
    }

    /// <summary>
    /// F4b (adversarial review): pin the protocol-30 daemon prologue (docs/daemon-spec.md §1-3 apply
    /// identically at 30; only the binary-phase compat/flist details, out of P8 scope, could differ).
    /// <c>daemon30-pull-rt</c> was captured with <c>--protocol=30</c> against a real 3.4.3 daemon (which
    /// still greets as its own "32.0"), so the negotiated protocol is <c>min(30, 32) = 30</c>. This
    /// deliberately stops at the prologue: <see cref="DaemonPreamble.RunAsync"/> plus
    /// <see cref="HandshakeRunner.RunClientAsync"/> for the compat/checksum-list/seed exchange, never a
    /// full <see cref="PullSession"/> replay — flist/mux differences at protocol 30 are out of scope
    /// here. Decoding the vector by hand (docs/wire-decode workflow) confirms the compat_flags varint,
    /// checksum-list vstring, and 4-byte LE seed sit at the same relative shape as the protocol-31
    /// vectors, landing the seed at exactly <c>0x6a523f9f</c>.
    /// </summary>
    [Fact]
    public async Task Daemon30_PrologueByteIdenticalToCapture_NegotiatesProtocol30WithCapturedSeed()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        byte[] s2c = Capture("daemon30-pull-rt", "s2c.bin");
        var serverArgs = new ServerArgvBuilder { Sender = true, Recurse = true, Paths = ["tree/"] };
        await using var transport = new ScriptedTransport(s2c);

        DaemonPreambleResult preamble = await DaemonPreamble.RunAsync(
            transport, "tree", serverArgs, maxProtocol: 30, cancellationToken: cts.Token);
        Assert.Equal(30, preamble.Protocol);

        SessionContext session = await HandshakeRunner.RunClientAsync(
            transport.Input, transport.Output,
            new HandshakeOptions { PreNegotiatedProtocolVersion = preamble.Protocol },
            cts.Token);

        Assert.Equal(30, session.Protocol);
        Assert.Equal(0x6a523f9f, session.ChecksumSeed);

        byte[] written = await transport.WrittenBytesAsync();
        byte[] captured = Capture("daemon30-pull-rt", "c2s.bin");

        List<string> argvWords = [.. (serverArgs with { Protocol = preamble.Protocol }).Build().Skip(1)];
        Assert.Equal(["--server", "--sender", "-tre.LsfxCIvu", ".", "tree/"], argvWords);
        byte[] argvBytes = DaemonArgWriter.Write(argvWords, preamble.Protocol);

        int prologueLength = TextualPrologueLength(
            [DaemonGreeting.FormatClient(preamble.Protocol), "tree\n"], argvBytes);
        Assert.Equal(captured[..prologueLength], written[..prologueLength]);
    }
}
