namespace RsyncWin.Perf;

internal static class ScenarioCatalog
{
    public static IReadOnlyList<ScenarioDefinition> ForProfile(string profile) => profile.ToLowerInvariant() switch
    {
        "smoke" =>
        [
            new("small-files", "128 x 4 KiB files", 128, 128 * 4096L, DatasetKind.SmallFiles),
            new("large-files", "2 x 8 MiB files", 2, 16 * PerfConstants.Mebibyte, DatasetKind.LargeFiles),
            new("mixed-tree", "64 files totalling 32 MiB", 64, 32 * PerfConstants.Mebibyte, DatasetKind.MixedFiles),
            new("delta", "16 MiB basis with deterministic 1% block edits", 2, 32 * PerfConstants.Mebibyte, DatasetKind.Delta),
            new("compressible", "16 MiB compressible file", 1, 16 * PerfConstants.Mebibyte, DatasetKind.Compressible),
            new("incompressible", "16 MiB incompressible file", 1, 16 * PerfConstants.Mebibyte, DatasetKind.Incompressible),
        ],
        "full" =>
        [
            new("small-files", "100,000 x 4 KiB files", 100_000, 100_000 * 4096L, DatasetKind.SmallFiles),
            new("large-files", "8 x 1 GiB files", 8, 8 * PerfConstants.Gibibyte, DatasetKind.LargeFiles),
            new("mixed-tree", "20,000 files totalling approximately 8 GiB", 20_000, 8 * PerfConstants.Gibibyte, DatasetKind.MixedFiles),
            new("delta", "1 GiB basis with deterministic 1% block edits", 2, 2 * PerfConstants.Gibibyte, DatasetKind.Delta),
            new("compressible", "4 x 512 MiB compressible files", 4, 2 * PerfConstants.Gibibyte, DatasetKind.Compressible),
            new("incompressible", "4 x 512 MiB incompressible files", 4, 2 * PerfConstants.Gibibyte, DatasetKind.Incompressible),
        ],
        _ => throw new ArgumentException($"Unknown profile '{profile}', expected smoke or full"),
    };

    public static IReadOnlyList<ScenarioDefinition> Select(string profile, string scenario)
    {
        IReadOnlyList<ScenarioDefinition> all = ForProfile(profile);
        if (scenario.Equals("all", StringComparison.OrdinalIgnoreCase))
            return all;
        return [all.Single(x => x.Name.Equals(scenario, StringComparison.OrdinalIgnoreCase))];
    }
}
