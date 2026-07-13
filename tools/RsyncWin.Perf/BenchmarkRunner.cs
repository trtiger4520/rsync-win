using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace RsyncWin.Perf;

internal static partial class BenchmarkRunner
{
    private static readonly string[] Operations = ["full-copy", "up-to-date", "delta", "checksum", "compression", "delete"];

    public static async Task<BenchmarkDocument> RunAsync(CommandLine options, CancellationToken cancellationToken)
    {
        string profile = options.Get("--profile", "smoke");
        string scenarioName = options.Get("--scenario", profile == "smoke" ? "small-files" : "all");
        string root = Path.GetFullPath(options.Get("--root", Path.Combine("artifacts", "perf")));
        int warmups = options.GetInt("--warmups", 1);
        int iterations = options.GetInt("--iterations", 5);
        bool dryRun = options.Has("--dry-run");
        bool injectDryFailure = options.Has("--inject-dry-failure");
        string? rsyncWinCommand = options.GetOptional("--rsyncwin-command");
        string? rsyncCommand = options.GetOptional("--rsync-command");
        string clientsOption = options.Get("--clients", "both").ToLowerInvariant();
        string? directExecutable = options.GetOptional("--direct-executable");
        string? directEndpoint = options.GetOptional("--direct-endpoint");
        string container = options.Get("--container", string.Empty);
        int timeoutSeconds = options.GetInt("--timeout-seconds", profile == "smoke" ? 60 : 3600);

        if (warmups < 0 || iterations <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "--warmups must be non-negative and --iterations must be positive");
        string[] selectedClients = clientsOption switch
        {
            "both" => ["rsyncwin", "rsync"],
            "rsyncwin" => ["rsyncwin"],
            "rsync" => ["rsync"],
            _ => throw new ArgumentException("--clients must be both, rsyncwin, or rsync"),
        };
        if ((directExecutable is null) != (directEndpoint is null))
            throw new ArgumentException("--direct-executable and --direct-endpoint must be supplied together");
        if (!dryRun && selectedClients.Contains("rsyncwin") && directExecutable is null && rsyncWinCommand is null)
            throw new ArgumentException("rsyncwin requires --rsyncwin-command or direct executable/endpoint options");
        if (!dryRun && selectedClients.Contains("rsync") && rsyncCommand is null)
            throw new ArgumentException("rsync requires --rsync-command");

        var raw = new List<IterationResult>();
        foreach (ScenarioDefinition scenario in ScenarioCatalog.Select(profile, scenarioName))
        {
            DatasetManifest dataset = dryRun
                ? SyntheticManifest(scenario, profile)
                : await DatasetGenerator.GenerateAsync(scenario, profile, root, cancellationToken);
            try
            {
                foreach (string operation in Operations)
                {
                    for (int round = -warmups; round < iterations; round++)
                    {
                        string[] orderedClients = round % 2 == 0 ? ["rsyncwin", "rsync"] : ["rsync", "rsyncwin"];
                        string[] clients = orderedClients
                            .Where(selectedClients.Contains)
                            .ToArray();
                        foreach (string client in clients)
                        {
                            string command = client == "rsyncwin" ? rsyncWinCommand ?? string.Empty : rsyncCommand ?? string.Empty;
                            raw.Add(dryRun
                                ? DryIteration(scenario, dataset, client, operation, round, injectDryFailure)
                                : await ExecuteAsync(
                                    scenario, dataset, client, operation, round, command, root, container,
                                    timeoutSeconds, directExecutable, directEndpoint, cancellationToken));
                        }
                    }
                }
            }
            finally
            {
                if (!dryRun && !options.Has("--keep-data"))
                {
                    DatasetGenerator.Cleanup(root, scenario.Name);
                    DeleteDirectoryIfExists(Path.Combine(root, "results"));
                }
            }
        }

        IReadOnlyList<BenchmarkSummary> summaries = Summarize(raw, iterations);
        IReadOnlyList<MicrobenchmarkResult> micro = await MicrobenchmarkRunner.RunAsync(dryRun ? "smoke" : profile, cancellationToken);
        return new BenchmarkDocument(DateTimeOffset.UtcNow, profile, PerfConstants.Seed, warmups, iterations, dryRun, raw, summaries, micro);
    }

    private static IterationResult DryIteration(
        ScenarioDefinition scenario,
        DatasetManifest dataset,
        string client,
        string operation,
        int round,
        bool injectFailure)
    {
        double clientFactor = client == "rsyncwin" ? 1.12 : 1.0;
        double operationFactor = 1 + Array.IndexOf(Operations, operation) * 0.08;
        double elapsed = Math.Max(1, scenario.LogicalBytes / (250d * PerfConstants.Mebibyte) * 1000 * clientFactor * operationFactor + Math.Max(round, 0));
        bool failed = injectFailure && round >= 0 && client == "rsyncwin" && operation == "checksum";
        return new IterationResult(
            scenario.Name,
            client,
            operation,
            Math.Max(round, 0),
            round < 0,
            elapsed,
            scenario.LogicalBytes / (double)PerfConstants.Mebibyte / (elapsed / 1000),
            elapsed * 0.7,
            64 * PerfConstants.Mebibyte,
            elapsed * 0.7,
            64 * PerfConstants.Mebibyte,
            failed ? 23 : 0,
            operation == "up-to-date" ? 0 : scenario.LogicalBytes,
            operation is "up-to-date" or "delta" ? scenario.LogicalBytes : 0,
            failed ? null : dataset.ContentManifestSha256,
            new CgroupMetrics(null, null, null, null, "dry-run: container metrics were not sampled"),
            failed ? "injected dry-run validation failure" : null);
    }

    private static DatasetManifest SyntheticManifest(ScenarioDefinition scenario, string profile)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"dry-run\0{profile}\0{scenario.Name}\0{scenario.FileCount}\0{scenario.LogicalBytes}"));
        return new DatasetManifest(
            scenario.Name,
            profile,
            PerfConstants.Seed,
            scenario.FileCount,
            scenario.LogicalBytes,
            Convert.ToHexString(digest).ToLowerInvariant(),
            []);
    }

    private static async Task<IterationResult> ExecuteAsync(
        ScenarioDefinition scenario,
        DatasetManifest dataset,
        string client,
        string operation,
        int round,
        string commandTemplate,
        string root,
        string container,
        int timeoutSeconds,
        string? directExecutable,
        string? directEndpoint,
        CancellationToken cancellationToken)
    {
        string source = Path.Combine(root, scenario.Name);
        string destination = Path.Combine(root, "results", client, scenario.Name);
        using PreparedOperation prepared = await OperationPreparer.PrepareAsync(
            operation, source, destination, dataset, cancellationToken);
        await DockerMetrics.RemoveContainerAsync(container, cancellationToken);
        string flags = FlagsFor(client, operation);
        string command = commandTemplate
            .Replace("{flags}", flags, StringComparison.Ordinal)
            .Replace("{source}", Quote(source), StringComparison.Ordinal)
            .Replace("{destination}", Quote(destination), StringComparison.Ordinal)
            .Replace("{scenario}", scenario.Name, StringComparison.Ordinal)
            .Replace("{operation}", operation, StringComparison.Ordinal);

        using var process = client == "rsyncwin" && directExecutable is not null
            ? CreateDirectProcess(directExecutable, directEndpoint!, flags, scenario.Name, destination)
            : CreateShellProcess(command);
        var stopwatch = Stopwatch.StartNew();
        process.Start();
        Task<CgroupMetrics> cgroupTask = DockerMetrics.SampleWhileRunningAsync(container, process, cancellationToken);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        string? error = null;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            error = $"timeout after {timeoutSeconds} seconds";
            await process.WaitForExitAsync(CancellationToken.None);
        }
        stopwatch.Stop();
        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        TimeSpan hostCpu = TryGetCpuTime(process);
        long hostPeak = TryGetPeakWorkingSet(process);
        int exitCode = error is null ? process.ExitCode : 30;
        CgroupMetrics cgroup = await cgroupTask;
        double cpuMilliseconds = cgroup.CpuTimeMilliseconds ?? hostCpu.TotalMilliseconds;
        long peakWorkingSetBytes = cgroup.PeakMemoryBytes ?? hostPeak;
        await DockerMetrics.RemoveContainerAsync(container, CancellationToken.None);
        string? manifest = null;
        if (exitCode == 0 && Directory.Exists(destination))
            manifest = (await ManifestBuilder.BuildAsync(scenario.Name, "result", destination, cancellationToken)).ContentManifestSha256;
        if (exitCode == 0 && !string.Equals(manifest, prepared.ExpectedManifest.ContentManifestSha256, StringComparison.Ordinal))
        {
            exitCode = 23;
            error = $"result manifest mismatch: expected {prepared.ExpectedManifest.ContentManifestSha256}, got {manifest ?? "<missing>"}";
        }

        return new IterationResult(
            scenario.Name,
            client,
            operation,
            Math.Max(round, 0),
            round < 0,
            stopwatch.Elapsed.TotalMilliseconds,
            scenario.LogicalBytes / (double)PerfConstants.Mebibyte / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.000001),
            cpuMilliseconds,
            peakWorkingSetBytes,
            hostCpu.TotalMilliseconds,
            hostPeak,
            exitCode,
            ParseCounter($"{stdout}\n{stderr}", "literal"),
            ParseCounter($"{stdout}\n{stderr}", "matched"),
            manifest,
            cgroup,
            error ?? (exitCode == 0 ? null : stderr.Trim()));
    }

    private static string FlagsFor(string client, string operation)
    {
        string flags = operation switch
        {
            "checksum" => "-rc",
            "compression" => "-rz",
            "delete" => "-r --delete",
            _ => "-r",
        };
        return client == "rsync" ? $"{flags} --stats" : flags;
    }

    private static TimeSpan TryGetCpuTime(Process process)
    {
        try { return process.TotalProcessorTime; }
        catch (InvalidOperationException) { return TimeSpan.Zero; }
    }

    private static long TryGetPeakWorkingSet(Process process)
    {
        try { return process.PeakWorkingSet64; }
        catch (InvalidOperationException) { return 0; }
    }

    internal static IReadOnlyList<BenchmarkSummary> Summarize(IEnumerable<IterationResult> iterations, int requiredIterations) =>
        iterations.Where(x => !x.Warmup)
            .GroupBy(x => (x.Scenario, x.Client, x.Operation))
            .Select(group => CreateSummary(group, requiredIterations))
            .OrderBy(x => x.Scenario, StringComparer.Ordinal)
            .ThenBy(x => x.Operation, StringComparer.Ordinal)
            .ThenBy(x => x.Client, StringComparer.Ordinal)
            .ToArray();

    private static BenchmarkSummary CreateSummary(
        IGrouping<(string Scenario, string Client, string Operation), IterationResult> group,
        int requiredIterations)
    {
        IterationResult[] valid = group
            .Where(x => x.ExitCode == 0 && x.ResultManifestSha256 is not null)
            .ToArray();
        bool hasResult = valid.Length >= requiredIterations;
        string? failureReason = hasResult
            ? null
            : $"only {valid.Length} of {requiredIterations} required successful manifest-verified iterations completed";
        return new BenchmarkSummary(
            group.Key.Scenario,
            group.Key.Client,
            group.Key.Operation,
            hasResult ? Summary(valid.Select(x => x.ElapsedMilliseconds)) : EmptySummary(),
            hasResult ? Summary(valid.Select(x => x.LogicalMibPerSecond)) : EmptySummary(),
            hasResult ? Summary(valid.Select(x => x.CpuMilliseconds)) : EmptySummary(),
            hasResult ? Summary(valid.Select(x => (double)x.PeakWorkingSetBytes)) : EmptySummary(),
            hasResult,
            failureReason,
            valid.Length,
            group.Count());
    }

    private static MetricSummary Summary(IEnumerable<double> source)
    {
        double[] values = source.Order().ToArray();
        if (values.Length == 0)
            return EmptySummary();
        double median = values.Length % 2 == 0 ? (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2 : values[values.Length / 2];
        int p95Index = Math.Clamp((int)Math.Ceiling(values.Length * 0.95) - 1, 0, values.Length - 1);
        return new MetricSummary(median, values[p95Index]);
    }

    private static MetricSummary EmptySummary() => new(null, null);

    private static Process CreateShellProcess(string command)
    {
        var info = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (OperatingSystem.IsWindows())
        {
            info.ArgumentList.Add("/d");
            info.ArgumentList.Add("/s");
            info.ArgumentList.Add("/c");
        }
        else
        {
            info.ArgumentList.Add("-c");
        }
        info.ArgumentList.Add(command);
        return new Process { StartInfo = info };
    }

    private static Process CreateDirectProcess(
        string executable,
        string endpointTemplate,
        string flags,
        string scenario,
        string destination)
    {
        var info = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string flag in flags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            info.ArgumentList.Add(flag);
        info.ArgumentList.Add(endpointTemplate.Replace("{scenario}", scenario, StringComparison.Ordinal));
        info.ArgumentList.Add(destination);
        return new Process { StartInfo = info };
    }

    private static string Quote(string value) => OperatingSystem.IsWindows() ? $"\"{value.Replace("\"", "\"\"")}\"" : $"'{value.Replace("'", "'\\''")}'";

    private static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
            return;
        foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    private static long? ParseCounter(string output, string name)
    {
        Match match = CounterRegex().Match(output);
        while (match.Success)
        {
            if (match.Groups["name"].Value.StartsWith(name, StringComparison.OrdinalIgnoreCase)
                && long.TryParse(match.Groups["value"].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
                return value;
            match = match.NextMatch();
        }
        return null;
    }

    [GeneratedRegex(@"(?<name>literal(?: data)?|matched(?: data)?)(?: bytes)?\s*[:=]\s*(?<value>[\d,]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CounterRegex();
}

internal static partial class DockerMetrics
{
    public static async Task RemoveContainerAsync(string container, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(container))
            return;
        try
        {
            ProcessStartInfo info = DockerInfo("rm", "-f", container);
            using var process = new Process { StartInfo = info };
            process.Start();
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Sampling will record the actionable Docker error; cleanup is best-effort
        }
    }

    public static async Task<CgroupMetrics> SampleWhileRunningAsync(
        string container,
        Process measuredProcess,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(container))
            return new CgroupMetrics(null, null, null, null, "no --container was supplied");

        double? peakCpu = null;
        long? cpuUsageUsec = null;
        long? peakMemory = null;
        string? blockIo = null;
        string? lastError = null;
        do
        {
            try
            {
                ProcessStartInfo info = DockerInfo("stats", "--no-stream", "--format", "{{.CPUPerc}}|{{.MemUsage}}|{{.BlockIO}}", container);
                using var process = new Process { StartInfo = info };
                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                string error = await process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);
                string[] fields = output.Trim().Split('|');
                if (process.ExitCode == 0 && fields.Length == 3)
                {
                    if (TryPercent(fields[0], out double cpu))
                        peakCpu = Math.Max(peakCpu ?? 0, cpu);
                    blockIo = fields[2];
                }

                (string? cpuStat, string? cpuError) = await DockerExecCatAsync(container, "/sys/fs/cgroup/cpu.stat", cancellationToken);
                if (cpuStat is not null)
                {
                    Match usage = CpuUsageRegex().Match(cpuStat);
                    if (usage.Success && long.TryParse(usage.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long usec))
                        cpuUsageUsec = Math.Max(cpuUsageUsec ?? 0, usec);
                }
                else if (cpuError is not null)
                {
                    lastError = cpuError;
                }

                (string? memoryPeak, string? memoryError) = await DockerExecCatAsync(container, "/sys/fs/cgroup/memory.peak", cancellationToken);
                if (memoryPeak is not null && long.TryParse(memoryPeak.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long memoryBytes))
                    peakMemory = Math.Max(peakMemory ?? 0, memoryBytes);
                else if (memoryError is not null)
                    lastError = memoryError;
                else if (!string.IsNullOrWhiteSpace(error))
                {
                    lastError = error.Trim();
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                lastError = ex.Message;
            }

            if (!measuredProcess.HasExited)
                await Task.Delay(100, cancellationToken);
        } while (!measuredProcess.HasExited);

        var missing = new List<string>();
        if (cpuUsageUsec is null)
            missing.Add("cpu.stat usage_usec unavailable; host CPU time used");
        if (peakMemory is null)
            missing.Add("memory.peak unavailable; host peak working set used");
        if (peakCpu is null)
            missing.Add("docker stats CPU percent unavailable");
        string? reason = missing.Count == 0 ? null : string.Join("; ", missing) + (lastError is null ? string.Empty : $"; last error: {lastError}");
        return new CgroupMetrics(peakCpu, cpuUsageUsec / 1000d, peakMemory, blockIo, reason);
    }

    private static async Task<(string? Output, string? Error)> DockerExecCatAsync(
        string container,
        string path,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo info = DockerInfo("exec", container, "cat", path);
        using var process = new Process { StartInfo = info };
        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        string error = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0
            ? (output, null)
            : (null, string.IsNullOrWhiteSpace(error) ? $"docker exec cat {path} exited {process.ExitCode}" : error.Trim());
    }

    private static ProcessStartInfo DockerInfo(params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    private static bool TryPercent(string value, out double percent) =>
        double.TryParse(value.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out percent);

    [GeneratedRegex(@"(?:^|\n)usage_usec\s+(?<value>\d+)(?:\r?$|\n)", RegexOptions.CultureInvariant)]
    private static partial Regex CpuUsageRegex();
}
