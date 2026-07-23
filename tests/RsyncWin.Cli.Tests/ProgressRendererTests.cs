using RsyncWin.Cli;

namespace RsyncWin.Cli.Tests;

/// <summary>Pure-format coverage for <see cref="ProgressRenderer"/>'s rsync-style output plus one
/// clock-driven end-to-end render, using an injected <see cref="TimeProvider"/> for a deterministic
/// rate and elapsed time (no wall-clock, no real console). See <c>docs/progress-spec.md</c>.</summary>
public class ProgressRendererTests
{
    [Theory]
    [InlineData(0, "0")]
    [InlineData(1234567, "1,234,567")]
    [InlineData(104857600, "104,857,600")]
    public void FormatBytes_GroupsWithInvariantThousands(long bytes, string expected) =>
        Assert.Equal(expected, ProgressRenderer.FormatBytes(bytes));

    [Theory]
    [InlineData(0, "0.00B/s")]
    [InlineData(512, "512.00B/s")]
    [InlineData(1024, "1.00kB/s")]
    [InlineData(1536, "1.50kB/s")]
    [InlineData(50 * 1024 * 1024, "50.00MB/s")]
    public void FormatRate_Scales1024AndAppendsUnit(double bytesPerSecond, string expected) =>
        Assert.Equal(expected, ProgressRenderer.FormatRate(bytesPerSecond));

    [Fact]
    public void FormatTime_HmmssWithUnpaddedHours()
    {
        Assert.Equal("0:00:00", ProgressRenderer.FormatTime(TimeSpan.Zero));
        Assert.Equal("0:00:02", ProgressRenderer.FormatTime(TimeSpan.FromSeconds(2)));
        Assert.Equal("1:02:03", ProgressRenderer.FormatTime(new TimeSpan(1, 2, 3)));
        Assert.Equal("25:00:00", ProgressRenderer.FormatTime(TimeSpan.FromHours(25)));
        Assert.Equal("0:00:00", ProgressRenderer.FormatTime(TimeSpan.FromSeconds(-5)));
    }

    [Theory]
    [InlineData(1, 1, "(xfr#1, to-chk=0/1)")]
    [InlineData(2, 5, "(xfr#2, to-chk=3/5)")]
    [InlineData(3, 3, "(xfr#3, to-chk=0/3)")]
    public void FormatXfrSuffix_CountsRemaining(int transferred, int total, string expected) =>
        Assert.Equal(expected, ProgressRenderer.FormatXfrSuffix(transferred, total));

    [Fact]
    public void BuildLine_ZeroSizeReportsHundredPercent() =>
        // A zero-length file must not divide by zero — it is reported complete.
        Assert.StartsWith("              0 100%", ProgressRenderer.BuildLine(0, 0, 0, TimeSpan.Zero, null));

    [Fact]
    public void BuildLine_ClampsPercentAtHundred() =>
        Assert.Contains("100%", ProgressRenderer.BuildLine(200, 100, 0, TimeSpan.Zero, null));

    [Fact]
    public void WholeTransfer_FinalLine_HasDeterministicRateElapsedAndXfr()
    {
        var clock = new FakeTime();
        var writer = new StringWriter();
        var renderer = new ProgressRenderer(writer, animate: false, ProgressMode.WholeTransfer, clock);

        clock.NowNanos = 0;
        renderer.Begin(totalBytes: 1000, totalFiles: 1);
        renderer.BeginFile("payload.bin", 1000);

        clock.NowNanos = 2_000_000_000; // +2s
        renderer.Advance(1000);
        renderer.EndFile();
        renderer.End();

        // 1000 bytes over 2s → 500 B/s; elapsed 2s; one file transferred. Tie the expectation to the
        // pure helpers so this asserts the renderer wired the right VALUES, not hand-copied spacing.
        string expected = ProgressRenderer.BuildLine(
            1000, 1000, 500, TimeSpan.FromSeconds(2), ProgressRenderer.FormatXfrSuffix(1, 1)) + "\n";
        Assert.Equal(expected, writer.ToString());
    }

    [Fact]
    public void PerFile_RedirectedOutput_PrintsNameThenOneCompletedLinePerFile()
    {
        var clock = new FakeTime();
        var writer = new StringWriter();
        var renderer = new ProgressRenderer(writer, animate: false, ProgressMode.PerFile, clock);

        clock.NowNanos = 0;
        renderer.Begin(totalBytes: 400, totalFiles: 1);
        renderer.BeginFile("dir/file.txt", 400);
        clock.NowNanos = 1_000_000_000; // +1s
        renderer.Advance(400);
        renderer.EndFile();
        renderer.End();

        // Redirected (animate:false): the filename line, then exactly one completed progress line with
        // no carriage returns. 400 bytes / 1s = 400 B/s.
        string expectedLine = ProgressRenderer.BuildLine(
            400, 400, 400, TimeSpan.FromSeconds(1), ProgressRenderer.FormatXfrSuffix(1, 1));
        Assert.Equal($"dir/file.txt\n{expectedLine}\n", writer.ToString());
        Assert.DoesNotContain('\r', writer.ToString());
    }

    /// <summary>A hand-driven <see cref="TimeProvider"/>: 1 tick == 1 nanosecond, timestamp set by the
    /// test so rate/elapsed math is fully deterministic.</summary>
    private sealed class FakeTime : TimeProvider
    {
        public long NowNanos;
        public override long GetTimestamp() => NowNanos;
        public override long TimestampFrequency => 1_000_000_000;
    }
}
