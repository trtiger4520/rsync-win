using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RsyncWin.Perf;

internal static class ReportWriter
{
    public static async Task WriteAsync(BenchmarkDocument document, string outputDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        string jsonPath = Path.Combine(outputDirectory, "benchmark.json");
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(document, PerfJsonContext.Default.BenchmarkDocument),
            cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "iterations.csv"), RawCsv(document.RawIterations), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "summary.csv"), SummaryCsv(document.Summaries), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "throughput.svg"), ThroughputSvg(document.Summaries), cancellationToken);
    }

    public static async Task<BenchmarkDocument> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        return await JsonSerializer.DeserializeAsync(stream, PerfJsonContext.Default.BenchmarkDocument, cancellationToken)
            ?? throw new InvalidDataException($"Could not read benchmark document: {path}");
    }

    private static string RawCsv(IEnumerable<IterationResult> rows)
    {
        var result = new StringBuilder("scenario,client,operation,iteration,warmup,elapsed_ms,mib_per_second,cpu_ms,peak_working_set_bytes,host_cpu_ms,host_peak_working_set_bytes,exit_code,literal_bytes,matched_bytes,manifest_sha256,cgroup_peak_cpu_percent,cgroup_cpu_time_ms,cgroup_peak_memory_bytes,cgroup_block_io,cgroup_reason,error\n");
        foreach (IterationResult x in rows)
        {
            AppendCsv(result,
                x.Scenario, x.Client, x.Operation, x.Iteration, x.Warmup,
                x.ElapsedMilliseconds, x.LogicalMibPerSecond, x.CpuMilliseconds,
                x.PeakWorkingSetBytes, x.HostCpuMilliseconds, x.HostPeakWorkingSetBytes,
                x.ExitCode, x.LiteralBytes, x.MatchedBytes,
                x.ResultManifestSha256, x.Container.PeakCpuPercent, x.Container.CpuTimeMilliseconds, x.Container.PeakMemoryBytes,
                x.Container.BlockIo, x.Container.Reason, x.Error);
        }
        return result.ToString();
    }

    private static string SummaryCsv(IEnumerable<BenchmarkSummary> rows)
    {
        var result = new StringBuilder("scenario,client,operation,has_result,failure_reason,elapsed_median_ms,elapsed_p95_ms,throughput_median_mibps,throughput_p95_mibps,cpu_median_ms,cpu_p95_ms,peak_ws_median_bytes,peak_ws_p95_bytes,successful_iterations,total_iterations\n");
        foreach (BenchmarkSummary x in rows)
        {
            AppendCsv(result,
                x.Scenario, x.Client, x.Operation, x.HasResult, x.FailureReason,
                x.ElapsedMilliseconds.Median, x.ElapsedMilliseconds.P95,
                x.LogicalMibPerSecond.Median, x.LogicalMibPerSecond.P95,
                x.CpuMilliseconds.Median, x.CpuMilliseconds.P95,
                x.PeakWorkingSetBytes.Median, x.PeakWorkingSetBytes.P95,
                x.SuccessfulIterations, x.TotalIterations);
        }
        return result.ToString();
    }

    private static void AppendCsv(StringBuilder builder, params object?[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0)
                builder.Append(',');
            string text = values[i] switch
            {
                null => string.Empty,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                var value => value.ToString() ?? string.Empty,
            };
            builder.Append('"').Append(text.Replace("\"", "\"\"")).Append('"');
        }
        builder.AppendLine();
    }

    private static string ThroughputSvg(IReadOnlyList<BenchmarkSummary> summaries)
    {
        const int width = 1200;
        const int left = 360;
        const int rowHeight = 24;
        BenchmarkSummary[] valid = summaries.Where(x => x.HasResult && x.LogicalMibPerSecond.Median is not null).ToArray();
        int height = Math.Max(100, 50 + valid.Length * rowHeight);
        double max = Math.Max(1, valid.Select(x => x.LogicalMibPerSecond.Median!.Value).DefaultIfEmpty(1).Max());
        var svg = new StringBuilder();
        svg.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width}\" height=\"{height}\" viewBox=\"0 0 {width} {height}\">");
        svg.AppendLine("<rect width=\"100%\" height=\"100%\" fill=\"white\"/><style>text{font:12px sans-serif}.title{font:bold 16px sans-serif}</style>");
        svg.AppendLine("<text x=\"12\" y=\"24\" class=\"title\">Median logical throughput (MiB/s)</text>");
        for (int i = 0; i < valid.Length; i++)
        {
            BenchmarkSummary item = valid[i];
            int y = 42 + i * rowHeight;
            double throughput = item.LogicalMibPerSecond.Median!.Value;
            double barWidth = (width - left - 70) * throughput / max;
            string label = XmlEscape($"{item.Scenario} / {item.Operation} / {item.Client}");
            string color = item.Client == "rsyncwin" ? "#2563eb" : "#16a34a";
            svg.AppendLine($"<text x=\"12\" y=\"{y + 12}\">{label}</text>");
            svg.AppendLine($"<rect x=\"{left}\" y=\"{y}\" width=\"{barWidth.ToString("F2", CultureInfo.InvariantCulture)}\" height=\"16\" fill=\"{color}\"/>");
            svg.AppendLine($"<text x=\"{left + barWidth + 6}\" y=\"{y + 12}\">{throughput.ToString("F2", CultureInfo.InvariantCulture)}</text>");
        }
        svg.AppendLine("</svg>");
        return svg.ToString();
    }

    private static string XmlEscape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);
}
