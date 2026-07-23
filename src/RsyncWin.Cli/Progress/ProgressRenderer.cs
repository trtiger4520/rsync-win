using System.Globalization;
using RsyncWin.Fs;

namespace RsyncWin.Cli;

/// <summary>Which progress layout to render (see <c>docs/progress-spec.md</c>).</summary>
internal enum ProgressMode
{
    /// <summary><c>--progress</c>: a filename line plus a progress line per transferred file.</summary>
    PerFile,

    /// <summary><c>--info=progress2</c>: one progress line for the whole transfer.</summary>
    WholeTransfer,
}

/// <summary>
/// Renders rsync-style <c>--progress</c> / <c>--info=progress2</c> output to a text writer (stderr in
/// production). Client-local only — exchanges no wire bytes (<c>docs/progress-spec.md</c>). The pure
/// formatting helpers are <c>internal static</c> so the unit tests can assert them directly without a
/// clock; the sliding-window rate/ETA math is driven by an injectable <see cref="TimeProvider"/>.
/// </summary>
internal sealed class ProgressRenderer : ITransferProgressSink
{
    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(2);

    private readonly TextWriter _out;
    private readonly bool _animate;
    private readonly ProgressMode _mode;
    private readonly TimeProvider _time;

    private long _totalBytes;
    private int _totalFiles;
    private int _filesTransferred;

    private long _beginTs;
    private long _cumulativeBytes;

    private long _currentSize;
    private long _currentBytes;
    private long _fileStartTs;

    private long _lastRenderTs;
    private int _lastLineWidth;
    private bool _lineOpen;   // an in-place progress line is on screen without a terminating newline
    private bool _broken;     // a write threw (e.g. broken stderr pipe): display is best-effort, so stop

    // (timestamp, cumulative-bytes) samples inside the trailing RateWindow — the instantaneous rate
    // is the slope across the window rather than the whole-transfer average.
    private readonly List<(long Ts, long Bytes)> _samples = [];

    public ProgressRenderer(TextWriter output, bool animate, ProgressMode mode, TimeProvider? timeProvider = null)
    {
        _out = output;
        _animate = animate;
        _mode = mode;
        _time = timeProvider ?? TimeProvider.System;
    }

    public void Begin(long totalBytes, int totalFiles)
    {
        // Reset all per-run state so a reused renderer instance can't carry stale counters or samples
        // into the next transfer.
        _totalBytes = totalBytes;
        _totalFiles = totalFiles;
        _filesTransferred = 0;
        _cumulativeBytes = 0;
        _currentBytes = 0;
        _currentSize = 0;
        _lastLineWidth = 0;
        _lineOpen = false;
        _samples.Clear();
        _beginTs = _time.GetTimestamp();
        _lastRenderTs = _beginTs;
        _samples.Add((_beginTs, 0));
    }

    public void BeginFile(string name, long size)
    {
        _currentSize = size;
        _currentBytes = 0;
        _fileStartTs = _time.GetTimestamp();

        if (_mode == ProgressMode.PerFile)
        {
            // Explicit LF, never WriteLine: on Windows WriteLine emits CRLF, which would both diverge
            // from rsync's LF-terminated lines and inject a stray '\r' into the animated stream.
            WriteRaw(name);
            WriteRaw('\n');
        }
    }

    public void Advance(long bytes)
    {
        _currentBytes += bytes;
        _cumulativeBytes += bytes;

        long now = _time.GetTimestamp();
        RecordSample(now);
        if (_time.GetElapsedTime(_lastRenderTs, now) >= RenderInterval)
            Render(now, final: false);
    }

    public void EndFile()
    {
        _filesTransferred++;
        if (_mode == ProgressMode.PerFile)
        {
            // Final line for this file: 100% + its own elapsed + the xfr/to-chk suffix, then a newline
            // so the next file starts fresh.
            Render(_time.GetTimestamp(), final: true);
            WriteRaw('\n');
            _lastLineWidth = 0;
            _lineOpen = false;
        }
    }

    public void End()
    {
        if (_mode == ProgressMode.WholeTransfer)
        {
            Render(_time.GetTimestamp(), final: true);
            WriteRaw('\n');
            _lastLineWidth = 0;
            _lineOpen = false;
        }
        else if (_lineOpen)
        {
            // Per-file mode aborted mid-file (EndFile never ran): terminate the dangling in-place
            // line so the caller's summary doesn't print onto it.
            WriteRaw('\n');
            _lineOpen = false;
        }
    }

    private void RecordSample(long now)
    {
        _samples.Add((now, _cumulativeBytes));
        // Drop samples that have fallen out of the trailing window, but always keep at least two so a
        // rate is still computable.
        while (_samples.Count > 2 && _time.GetElapsedTime(_samples[0].Ts, now) > RateWindow)
            _samples.RemoveAt(0);
    }

    private void Render(long now, bool final)
    {
        if (!_animate && !final)
            return; // redirected output: only the completed line is emitted

        _lastRenderTs = now;

        (long bytes, long size) = _mode == ProgressMode.WholeTransfer
            ? (_cumulativeBytes, _totalBytes)
            : (_currentBytes, _currentSize);

        double rate = CurrentRate(now);
        long startTs = _mode == ProgressMode.WholeTransfer ? _beginTs : _fileStartTs;
        TimeSpan time = final
            ? _time.GetElapsedTime(startTs, now)                 // completion: elapsed
            : EstimateRemaining(bytes, size, rate);              // in-flight: ETA

        string suffix = final ? FormatXfrSuffix(_filesTransferred, _totalFiles) : null!;
        string line = BuildLine(bytes, size, rate, time, suffix);

        if (_animate)
        {
            // Overwrite in place; pad with spaces to clear any leftover from a previous longer line.
            string padded = line.Length < _lastLineWidth ? line.PadRight(_lastLineWidth) : line;
            WriteRaw('\r');
            WriteRaw(padded);
            _lastLineWidth = line.Length;
        }
        else
        {
            WriteRaw(line); // redirected + final: caller appends the newline
        }
        _lineOpen = true;
    }

    // Display is best-effort: a broken stderr pipe (IOException) or a closed stream
    // (ObjectDisposedException) must never abort the transfer, so writes are guarded and a first
    // failure latches _broken to stop further attempts (see docs/progress-spec.md).
    private void WriteRaw(string text)
    {
        if (_broken)
            return;
        try { _out.Write(text); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { _broken = true; }
    }

    private void WriteRaw(char value)
    {
        if (_broken)
            return;
        try { _out.Write(value); }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { _broken = true; }
    }

    private double CurrentRate(long now)
    {
        if (_samples.Count < 2)
            return 0;
        (long ts0, long bytes0) = _samples[0];
        double seconds = _time.GetElapsedTime(ts0, now).TotalSeconds;
        return seconds > 0 ? (_cumulativeBytes - bytes0) / seconds : 0;
    }

    private static TimeSpan EstimateRemaining(long bytes, long size, double rate)
    {
        if (rate <= 0 || size <= 0 || bytes >= size)
            return TimeSpan.Zero;
        return TimeSpan.FromSeconds((size - bytes) / rate);
    }

    // ---- pure formatting helpers (unit-tested directly) --------------------------------------

    /// <summary>Assembles one progress line: right-aligned grouped bytes, percent, rate, time, and an
    /// optional <c>(xfr#…, to-chk=…)</c> suffix. See <c>docs/progress-spec.md</c>.</summary>
    internal static string BuildLine(long bytes, long size, double bytesPerSecond, TimeSpan time, string? xfrSuffix)
    {
        int percent = size <= 0 ? 100 : (int)Math.Min(100, 100 * bytes / size);
        string b = FormatBytes(bytes).PadLeft(15);
        string p = percent.ToString(CultureInfo.InvariantCulture).PadLeft(3);
        string r = FormatRate(bytesPerSecond).PadLeft(10);
        string t = FormatTime(time);
        string line = $"{b} {p}% {r} {t}";
        return xfrSuffix is null ? line : $"{line} {xfrSuffix}";
    }

    /// <summary>Byte count grouped with invariant thousands separators, e.g. <c>1,234,567</c>.</summary>
    internal static string FormatBytes(long bytes) => bytes.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>Transfer rate as <c>{value:F2}{unit}</c> with a 1024-scaled unit, e.g. <c>47.68MB/s</c>.</summary>
    internal static string FormatRate(double bytesPerSecond)
    {
        string[] units = ["B/s", "kB/s", "MB/s", "GB/s", "TB/s"];
        double value = bytesPerSecond < 0 ? 0 : bytesPerSecond;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{value:F2}{units[unit]}");
    }

    /// <summary>Elapsed/remaining time as <c>H:MM:SS</c> (hours un-padded), e.g. <c>0:00:02</c>,
    /// <c>1:02:03</c>. Negative spans clamp to zero.</summary>
    internal static string FormatTime(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
            span = TimeSpan.Zero;
        long totalHours = (long)span.TotalHours;
        return string.Create(CultureInfo.InvariantCulture, $"{totalHours}:{span.Minutes:D2}:{span.Seconds:D2}");
    }

    /// <summary>The trailing <c>(xfr#N, to-chk=REM/TOTAL)</c> counter (rsync approximation, see spec).</summary>
    internal static string FormatXfrSuffix(int filesTransferred, int totalFiles)
    {
        int remaining = Math.Max(0, totalFiles - filesTransferred);
        return string.Create(CultureInfo.InvariantCulture, $"(xfr#{filesTransferred}, to-chk={remaining}/{totalFiles})");
    }
}
