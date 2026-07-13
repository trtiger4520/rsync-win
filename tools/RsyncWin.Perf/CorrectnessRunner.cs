namespace RsyncWin.Perf;

internal static class CorrectnessRunner
{
    public static async Task RunAsync(string profile, string root, string scenario, CancellationToken cancellationToken)
    {
        foreach (ScenarioDefinition definition in ScenarioCatalog.Select(profile, scenario))
        {
            DatasetManifest first = await DatasetGenerator.GenerateAsync(definition, profile, root, cancellationToken);
            ValidateShape(definition, first);
            string firstDigest = first.ContentManifestSha256;

            DatasetManifest second = await DatasetGenerator.GenerateAsync(definition, profile, root, cancellationToken);
            ValidateShape(definition, second);
            if (!firstDigest.Equals(second.ContentManifestSha256, StringComparison.Ordinal))
                throw new InvalidDataException($"{definition.Name}: fixed-seed generation was not deterministic");

            Console.WriteLine($"PASS {definition.Name}: {second.FileCount:N0} files, {second.LogicalBytes:N0} bytes, {second.ContentManifestSha256}");
            DatasetGenerator.Cleanup(root, definition.Name);
        }
    }

    private static void ValidateShape(ScenarioDefinition definition, DatasetManifest manifest)
    {
        if (manifest.FileCount != definition.FileCount)
            throw new InvalidDataException($"{definition.Name}: expected {definition.FileCount} files, got {manifest.FileCount}");
        if (manifest.LogicalBytes != definition.LogicalBytes)
            throw new InvalidDataException($"{definition.Name}: expected {definition.LogicalBytes} bytes, got {manifest.LogicalBytes}");
    }
}
