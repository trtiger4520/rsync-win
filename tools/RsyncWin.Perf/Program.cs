using System.Text.Json;

namespace RsyncWin.Perf;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            CommandLine options = CommandLine.Parse(args);
            return options.Command switch
            {
                "help" => ShowHelp(),
                "generate" => await GenerateAsync(options, cancellation.Token),
                "correctness" => await CorrectnessAsync(options, cancellation.Token),
                "benchmark" => await BenchmarkAsync(options, cancellation.Token),
                "report" => await ReportAsync(options, cancellation.Token),
                _ => throw new ArgumentException($"Unknown command: {options.Command}"),
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled");
            return 30;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static int ShowHelp()
    {
        Console.WriteLine(
            """
            RsyncWin deterministic integrity and performance harness

            Commands:
              generate    Generate one fixed-seed dataset and its manifest
              correctness Regenerate each dataset twice and verify shape and determinism
              benchmark   Run one warm-up plus five measured alternating client iterations
              report      Rebuild JSON, CSV, and SVG output from benchmark JSON

            Common options:
              --profile smoke|full          Default: smoke
              --scenario <name>|all         small-files, large-files, mixed-tree, delta,
                                            compressible, incompressible
              --root <path>                 Default: artifacts/perf

            Benchmark options:
              --dry-run                     Exercise scheduling and reporting without clients
              --inject-dry-failure          Mark one measured dry cell failed for report validation
              --rsyncwin-command <template> Shell command with {flags}, {source}, {destination},
                                            {scenario}, and {operation} placeholders
              --rsync-command <template>    Stock rsync command template
              --clients <selection>         both, rsyncwin, or rsync; default: both
              --direct-executable <path>    Launch RsyncWin directly without cmd/sh
              --direct-endpoint <template>  Daemon source endpoint, supports {scenario}
              --warmups <n>                 Default: 1
              --iterations <n>              Default: 5
              --container <name>            Fixed client container name sampled every 100 ms
              --timeout-seconds <n>         Default: 60 smoke, 3600 full
              --keep-data                   Preserve generated scenario data
              --output <path>               Default: artifacts/perf/results

            Report options:
              --input <benchmark.json>      Required input benchmark document
              --output <path>               Output directory

            Full profile definitions use seed 0x5253594E4357494E and generate each scenario
            independently so the active dataset remains below the 30 GiB workspace ceiling
            """);
        return 0;
    }

    private static async Task<int> GenerateAsync(CommandLine options, CancellationToken cancellationToken)
    {
        string profile = options.Get("--profile", "smoke");
        string scenarioName = options.Get("--scenario", "small-files");
        string root = Path.GetFullPath(options.Get("--root", Path.Combine("artifacts", "perf")));
        IReadOnlyList<ScenarioDefinition> scenarios = ScenarioCatalog.Select(profile, scenarioName);
        if (scenarios.Count != 1)
            throw new ArgumentException("generate accepts one --scenario so datasets stay isolated; use correctness to cycle all scenarios");
        DatasetManifest manifest = await DatasetGenerator.GenerateAsync(scenarios[0], profile, root, cancellationToken);
        Console.WriteLine(
            $"Generated {manifest.Scenario}: {manifest.FileCount:N0} files, {manifest.LogicalBytes:N0} bytes, " +
            $"manifest {manifest.ContentManifestSha256}");
        return 0;
    }

    private static async Task<int> CorrectnessAsync(CommandLine options, CancellationToken cancellationToken)
    {
        string profile = options.Get("--profile", "smoke");
        string scenario = options.Get("--scenario", "all");
        string root = Path.GetFullPath(options.Get("--root", Path.Combine("artifacts", "perf", "correctness")));
        await CorrectnessRunner.RunAsync(profile, root, scenario, cancellationToken);
        return 0;
    }

    private static async Task<int> BenchmarkAsync(CommandLine options, CancellationToken cancellationToken)
    {
        BenchmarkDocument document = await BenchmarkRunner.RunAsync(options, cancellationToken);
        string output = Path.GetFullPath(options.Get("--output", Path.Combine("artifacts", "perf", "results")));
        await ReportWriter.WriteAsync(document, output, cancellationToken);
        Console.WriteLine($"Wrote {document.RawIterations.Count} raw iterations and {document.Summaries.Count} summaries to {output}");
        return document.RawIterations.Any(x => x.ExitCode != 0) ? 1 : 0;
    }

    private static async Task<int> ReportAsync(CommandLine options, CancellationToken cancellationToken)
    {
        string input = Path.GetFullPath(options.GetOptional("--input") ?? throw new ArgumentException("report requires --input <benchmark.json>"));
        string output = Path.GetFullPath(options.Get("--output", Path.Combine("artifacts", "perf", "report")));
        BenchmarkDocument source = await ReportWriter.ReadAsync(input, cancellationToken);
        BenchmarkDocument rebuilt = source with { Summaries = BenchmarkRunner.Summarize(source.RawIterations, source.Iterations) };
        await ReportWriter.WriteAsync(rebuilt, output, cancellationToken);
        Console.WriteLine($"Rebuilt report from {source.RawIterations.Count} raw iterations into {output}");
        return 0;
    }
}
