using System.Text.Json.Serialization;

namespace RsyncWin.Perf;

internal static class PerfConstants
{
    public const ulong Seed = 0x5253594E4357494E;
    public const long Mebibyte = 1024 * 1024;
    public const long Gibibyte = 1024 * Mebibyte;
}

internal sealed record ScenarioDefinition(
    string Name,
    string Description,
    int FileCount,
    long LogicalBytes,
    DatasetKind Kind);

internal enum DatasetKind
{
    SmallFiles,
    LargeFiles,
    MixedFiles,
    Delta,
    Compressible,
    Incompressible,
}

internal sealed record ManifestEntry(string Path, long Size, string Sha256, DateTimeOffset LastWriteUtc);

internal sealed record DatasetManifest(
    string Scenario,
    string Profile,
    ulong Seed,
    int FileCount,
    long LogicalBytes,
    string ContentManifestSha256,
    IReadOnlyList<ManifestEntry> Files);

internal sealed record CgroupMetrics(
    double? PeakCpuPercent,
    double? CpuTimeMilliseconds,
    long? PeakMemoryBytes,
    string? BlockIo,
    string? Reason);

internal sealed record IterationResult(
    string Scenario,
    string Client,
    string Operation,
    int Iteration,
    bool Warmup,
    double ElapsedMilliseconds,
    double LogicalMibPerSecond,
    double CpuMilliseconds,
    long PeakWorkingSetBytes,
    double HostCpuMilliseconds,
    long HostPeakWorkingSetBytes,
    int ExitCode,
    long? LiteralBytes,
    long? MatchedBytes,
    string? ResultManifestSha256,
    CgroupMetrics Container,
    string? Error);

internal sealed record MetricSummary(double? Median, double? P95);

internal sealed record BenchmarkSummary(
    string Scenario,
    string Client,
    string Operation,
    MetricSummary ElapsedMilliseconds,
    MetricSummary LogicalMibPerSecond,
    MetricSummary CpuMilliseconds,
    MetricSummary PeakWorkingSetBytes,
    bool HasResult,
    string? FailureReason,
    int SuccessfulIterations,
    int TotalIterations);

internal sealed record MicrobenchmarkResult(
    string Name,
    int Operations,
    double ElapsedMilliseconds,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    ulong CheckValue);

internal sealed record BenchmarkDocument(
    DateTimeOffset CreatedUtc,
    string Profile,
    ulong Seed,
    int Warmups,
    int Iterations,
    bool DryRun,
    IReadOnlyList<IterationResult> RawIterations,
    IReadOnlyList<BenchmarkSummary> Summaries,
    IReadOnlyList<MicrobenchmarkResult> Microbenchmarks);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DatasetManifest))]
[JsonSerializable(typeof(BenchmarkDocument))]
internal partial class PerfJsonContext : JsonSerializerContext;
