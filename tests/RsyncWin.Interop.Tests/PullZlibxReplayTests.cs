using System.Security.Cryptography;
using RsyncWin.Engine;
using RsyncWin.Protocol.Delta;
using RsyncWin.Protocol.Session;

namespace RsyncWin.Interop.Tests;

/// <summary>
/// Hermetic (not trait-gated): replays real rsync 3.4.3 <c>-z --compress-choice=zlibx</c> pull
/// captures through <see cref="PullSession"/> and pins that the zlibx token decoder
/// (<see cref="FileReceiver"/> + <see cref="ZlibxTokenCodec"/>) reconstructs every file byte-exact.
/// A full transfer (all DEFLATED_DATA literals) and a delta (matched blocks interleaved with
/// compressed literals) are both covered — a decode bug fails these, not just live interop.
/// </summary>
public class PullZlibxReplayTests
{
    private static readonly HandshakeOptions Zlibx = new()
    {
        ChecksumOffer = "xxh128",
        Compression = CompressionMethod.Zlibx,
    };

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

    private static byte[] Capture(string vector, string file) =>
        File.ReadAllBytes(Path.Combine(FindVectors(), vector, file));

    private static string Sha256Hex(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    [Fact]
    public async Task ZlibxFullPull_ReconstructsEveryFileByteExact()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-z-full-{Guid.NewGuid():N}");
        try
        {
            await using var transport = new ScriptedTransport(Capture("ssh31-pull-z-zlibx", "s2c.bin"));
            PullSession.Result result = await PullSession.RunAsync(
                transport,
                new ServerArgvBuilder { Sender = true, Recurse = true, Compress = true, Paths = ["/t/ztree/"] },
                dest, cts.Token, handshake: Zlibx);

            Assert.Empty(result.FailedFiles);

            // Ground truth: the source-tree sha256 manifest captured alongside the vector.
            foreach (string line in await File.ReadAllLinesAsync(
                Path.Combine(FindVectors(), "ssh31-pull-z-zlibx", "src-tree.sha256"), cts.Token))
            {
                if (line.Length == 0)
                    continue;
                string expected = line[..64];
                string relative = line[64..].Trim().TrimStart('.', '/');
                byte[] pulled = await File.ReadAllBytesAsync(Path.Combine(dest, relative), cts.Token);
                Assert.Equal(expected, Sha256Hex(pulled));
            }
        }
        finally
        {
            TryDeleteDir(dest);
        }
    }

    [Fact]
    public async Task ZlibxDeltaPull_ReconstructsAgainstBasis_ByteExact()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-z-delta-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dest);
            // The capture is a single-file delta pull: the local basis differs from the source at
            // offsets 1000 and 150000 (capture.sh). Seed the destination with that same stale basis
            // so the matched-block references resolve exactly as they did on the wire.
            byte[] source = Capture("ssh31-pull-z-delta", "result.bin"); // the post-pull (== source) content
            byte[] basis = (byte[])source.Clone();
            "XXXXXXXX"u8.CopyTo(basis.AsSpan(1000));
            "YYYY"u8.CopyTo(basis.AsSpan(150000));
            await File.WriteAllBytesAsync(Path.Combine(dest, "b003_300k.bin"), basis, cts.Token);
            // A stale mtime so the generator does not fast-path-skip it.
            File.SetLastWriteTimeUtc(Path.Combine(dest, "b003_300k.bin"), new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            await using var transport = new ScriptedTransport(Capture("ssh31-pull-z-delta", "s2c.bin"));
            PullSession.Result result = await PullSession.RunAsync(
                transport,
                new ServerArgvBuilder { Sender = true, Compress = true, Paths = ["/t/tree/b003_300k.bin"] },
                dest, cts.Token, handshake: Zlibx);

            Assert.Empty(result.FailedFiles);
            Assert.Equal(1, result.TransferredFiles);
            // Delta efficiency: most bytes came from the basis, only the two edited regions as literals.
            Assert.True(result.MatchedBytes > 250_000, $"expected mostly-matched, got {result.MatchedBytes}");

            byte[] reconstructed = await File.ReadAllBytesAsync(Path.Combine(dest, "b003_300k.bin"), cts.Token);
            Assert.Equal(Sha256Hex(source), Sha256Hex(reconstructed));
        }
        finally
        {
            TryDeleteDir(dest);
        }
    }

    /// <summary>
    /// Decisive experiment for the protocol-reviewer's cross-run concern: two literal runs that SHARE
    /// a distinctive 256-byte marker, planted far apart in the file (matched blocks between them) but
    /// close in DEFLATE-input distance (zlibx excludes matched blocks from the window). If rsync's
    /// continuous zlibx sender back-references run A from run B, a per-run-standalone decoder would
    /// throw. This pins that our decoder reconstructs the real rsync output byte-exact.
    /// </summary>
    [Fact]
    public async Task ZlibxCrossRunSharedMarker_ReconstructsByteExact()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string dest = Path.Combine(Path.GetTempPath(), $"rsyncwin-z-crossrun-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dest);
            // The exact pre-pull basis the capture's generator signed (pristine detbytes without the
            // two planted markers), saved alongside the capture.
            byte[] basis = Capture("ssh31-pull-z-crossrun", "basis.bin");
            await File.WriteAllBytesAsync(Path.Combine(dest, "src.bin"), basis, cts.Token);
            File.SetLastWriteTimeUtc(Path.Combine(dest, "src.bin"), new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            await using var transport = new ScriptedTransport(Capture("ssh31-pull-z-crossrun", "s2c.bin"));
            PullSession.Result result = await PullSession.RunAsync(
                transport,
                new ServerArgvBuilder { Sender = true, Compress = true, Paths = ["/t/src.bin"] },
                dest, cts.Token, handshake: Zlibx);

            Assert.Empty(result.FailedFiles);
            string expected = (await File.ReadAllTextAsync(
                Path.Combine(FindVectors(), "ssh31-pull-z-crossrun", "source.sha256"), cts.Token)).Trim();
            byte[] reconstructed = await File.ReadAllBytesAsync(Path.Combine(dest, "src.bin"), cts.Token);
            Assert.Equal(expected, Sha256Hex(reconstructed));
        }
        finally
        {
            TryDeleteDir(dest);
        }
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
