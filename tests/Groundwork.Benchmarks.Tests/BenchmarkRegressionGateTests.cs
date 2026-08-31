using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Groundwork.Benchmarks;
using Xunit;

namespace Groundwork.Benchmarks.Tests;

public sealed class BenchmarkRegressionGateTests
{
    private const string BaselineSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string CandidateSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string BenchmarkDotNetVersion = "0.15.8";

    [Fact]
    public void Gate_passes_at_each_mean_and_allocation_budget_boundary()
    {
        var baseline = CompleteEvidence();
        var candidate = CompleteEvidence();
        foreach (var method in GroundworkMethods)
            SetMetrics(candidate, method, mean: 110, allocatedBytes: 80);

        var result = RunGate(
            CompletePolicy(maxMeanRatio: 1.10, maxAllocatedRatio: 1.25),
            baseline,
            candidate);

        Assert.Equal(0, result.ExitCode);
        Assert.All(GroundworkMethods, method =>
            Assert.Contains($"PASS {method}", result.Output, StringComparison.Ordinal));
        Assert.Empty(result.Error);
    }

    [Fact]
    public void Gate_fails_and_names_every_exceeded_budget()
    {
        var baseline = CompleteEvidence();
        var candidate = CompleteEvidence();
        SetMetrics(candidate, GroundworkMethods[0], mean: 110.01, allocatedBytes: 80.01);

        var result = RunGate(
            CompletePolicy(maxMeanRatio: 1.10, maxAllocatedRatio: 1.25),
            baseline,
            candidate);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"FAIL {GroundworkMethods[0]}", result.Output, StringComparison.Ordinal);
        Assert.Contains("mean ratio", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allocated ratio", result.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.Error);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("unexpected")]
    public void Gate_refuses_any_case_set_drift(string mutation)
    {
        var candidate = CompleteEvidence();
        var benchmarks = candidate["Benchmarks"]!.AsArray();
        switch (mutation)
        {
            case "missing":
                benchmarks.RemoveAt(0);
                break;
            case "duplicate":
                benchmarks.Add(benchmarks[0]!.DeepClone());
                break;
            case "unexpected":
                benchmarks.Add(Benchmark("Unexpected_Groundwork", mean: 100, allocatedBytes: 64));
                break;
        }

        var result = RunGate(CompletePolicy(), CompleteEvidence(), candidate);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(mutation, result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("null-statistics")]
    [InlineData("missing-statistics")]
    [InlineData("missing-mean")]
    [InlineData("string-mean")]
    [InlineData("overflow-mean")]
    [InlineData("zero-mean")]
    [InlineData("negative-mean")]
    [InlineData("missing-memory")]
    [InlineData("missing-allocation")]
    [InlineData("negative-allocation")]
    public void Gate_refuses_invalid_metrics(string mutation)
    {
        var candidate = CompleteEvidence();
        var benchmark = FindBenchmark(candidate, GroundworkMethods[0]);
        switch (mutation)
        {
            case "null-statistics":
                benchmark["Statistics"] = null;
                break;
            case "missing-statistics":
                benchmark.Remove("Statistics");
                break;
            case "missing-mean":
                benchmark["Statistics"]!.AsObject().Remove("Mean");
                break;
            case "string-mean":
                benchmark["Statistics"]!["Mean"] = "100";
                break;
            case "overflow-mean":
                benchmark["Statistics"]!["Mean"] = JsonNode.Parse("1e999");
                break;
            case "zero-mean":
                benchmark["Statistics"]!["Mean"] = 0;
                break;
            case "negative-mean":
                benchmark["Statistics"]!["Mean"] = -1;
                break;
            case "missing-memory":
                benchmark.Remove("Memory");
                break;
            case "missing-allocation":
                benchmark["Memory"]!.AsObject().Remove("BytesAllocatedPerOperation");
                break;
            case "negative-allocation":
                benchmark["Memory"]!["BytesAllocatedPerOperation"] = -1;
                break;
        }

        var result = RunGate(CompletePolicy(), CompleteEvidence(), candidate);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(GroundworkMethods[0], result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("baseline-sha")]
    [InlineData("candidate-sha")]
    [InlineData("fingerprint")]
    [InlineData("benchmarkdotnet-version")]
    [InlineData("environment")]
    [InlineData("environment-type")]
    [InlineData("hardware-intrinsics")]
    [InlineData("host")]
    [InlineData("idle-confirmation")]
    [InlineData("result-hash")]
    [InlineData("baseline-policy-hash")]
    [InlineData("missing-result-binding")]
    [InlineData("duplicate-result-binding")]
    [InlineData("path-traversal")]
    public void Gate_refuses_provenance_mismatches(string mutation)
    {
        var policy = CompletePolicy();
        Func<string, string>? baselineManifestMutator = null;
        Func<string, string>? candidateManifestMutator = null;
        var baseline = CompleteEvidence();
        var candidate = CompleteEvidence();
        switch (mutation)
        {
            case "baseline-sha":
                baselineManifestMutator = manifest => ReplaceManifestValue(
                    manifest,
                    "commit",
                    "cccccccccccccccccccccccccccccccccccccccc");
                break;
            case "candidate-sha":
                candidateManifestMutator = manifest => ReplaceManifestValue(
                    manifest,
                    "commit",
                    "cccccccccccccccccccccccccccccccccccccccc");
                break;
            case "fingerprint":
                candidateManifestMutator = manifest => ReplaceManifestValue(
                    manifest,
                    "schema_fingerprint",
                    "different-fingerprint");
                break;
            case "benchmarkdotnet-version":
                candidate["HostEnvironmentInfo"]!["BenchmarkDotNetVersion"] = "0.16.0";
                break;
            case "environment":
                candidate["HostEnvironmentInfo"]!["ProcessorName"] = "Different CPU";
                break;
            case "environment-type":
                candidate["HostEnvironmentInfo"]!["PhysicalCoreCount"] = "8";
                break;
            case "hardware-intrinsics":
                FindBenchmark(candidate, GroundworkMethods[0])["HardwareIntrinsics"] = "Different intrinsics";
                break;
            case "host":
                candidateManifestMutator = manifest => ReplaceManifestValue(manifest, "host", "different-host");
                break;
            case "idle-confirmation":
                candidateManifestMutator = manifest => ReplaceManifestValue(
                    manifest,
                    "host_idle_confirmation",
                    "false");
                break;
            case "result-hash":
                candidateManifestMutator = manifest => ReplaceManifestValue(
                    manifest,
                    "benchmark_result_sha256",
                    new string('0', 64));
                break;
            case "baseline-policy-hash":
                policy["baselineResultSha256"] = new string('0', 64);
                break;
            case "missing-result-binding":
                candidateManifestMutator = manifest => RemoveManifestValue(manifest, "benchmark_result");
                break;
            case "duplicate-result-binding":
                candidateManifestMutator = manifest => manifest + "benchmark_result=candidate.json\n";
                break;
            case "path-traversal":
                candidateManifestMutator = manifest => ReplaceManifestValue(
                    manifest,
                    "benchmark_result",
                    "../candidate.json");
                break;
        }

        var result = RunGate(
            policy,
            baseline,
            candidate,
            baselineManifestMutator,
            candidateManifestMutator);

        Assert.Equal(2, result.ExitCode);
        var expectedDiagnostic = mutation switch
        {
            "path-traversal" => "bundle",
            "hardware-intrinsics" => "HardwareIntrinsics",
            "idle-confirmation" => "host_idle_confirmation",
            _ => mutation.Split('-')[0]
        };
        Assert.Contains(expectedDiagnostic, result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gate_requires_exactly_one_valid_budget_for_each_groundwork_case()
    {
        var missing = CompletePolicy();
        missing["budgets"]!.AsArray().RemoveAt(0);
        var duplicate = CompletePolicy();
        duplicate["budgets"]!.AsArray().Add(duplicate["budgets"]![0]!.DeepClone());
        var invalid = CompletePolicy();
        invalid["budgets"]![0]!["maxMeanRatio"] = 0.99;
        var nullBudgets = CompletePolicy();
        nullBudgets["budgets"] = null;

        var missingResult = RunGate(missing, CompleteEvidence(), CompleteEvidence());
        var duplicateResult = RunGate(duplicate, CompleteEvidence(), CompleteEvidence());
        var invalidResult = RunGate(invalid, CompleteEvidence(), CompleteEvidence());
        var nullBudgetsResult = RunGate(nullBudgets, CompleteEvidence(), CompleteEvidence());

        Assert.Equal(2, missingResult.ExitCode);
        Assert.Contains("missing", missingResult.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, duplicateResult.ExitCode);
        Assert.Contains("duplicate", duplicateResult.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, invalidResult.ExitCode);
        Assert.Contains("maxMeanRatio", invalidResult.Error, StringComparison.Ordinal);
        Assert.Equal(2, nullBudgetsResult.ExitCode);
        Assert.Contains("budgets", nullBudgetsResult.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Zero_allocation_baseline_allows_only_zero_candidate_allocation()
    {
        var baseline = CompleteEvidence();
        var passingCandidate = CompleteEvidence();
        var failingCandidate = CompleteEvidence();
        SetMetrics(baseline, GroundworkMethods[0], mean: 100, allocatedBytes: 0);
        SetMetrics(passingCandidate, GroundworkMethods[0], mean: 100, allocatedBytes: 0);
        SetMetrics(failingCandidate, GroundworkMethods[0], mean: 100, allocatedBytes: 1);

        var passing = RunGate(CompletePolicy(), baseline, passingCandidate);
        var failing = RunGate(CompletePolicy(), baseline, failingCandidate);

        Assert.Equal(0, passing.ExitCode);
        Assert.Equal(1, failing.ExitCode);
        Assert.Contains("allocated ratio", failing.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gate_fails_when_mean_ratio_is_one_representable_value_over_budget()
    {
        var candidate = CompleteEvidence();
        SetMetrics(candidate, GroundworkMethods[0], mean: Math.BitIncrement(1.10d) * 100, allocatedBytes: 64);

        var result = RunGate(CompletePolicy(maxMeanRatio: 1.10), CompleteEvidence(), candidate);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"FAIL {GroundworkMethods[0]}", result.Output, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> AllMethods { get; } = BenchmarkMethodology.Cases
        .Select(item => $"{item.Workload}_{item.Stack}")
        .ToArray();

    private static IReadOnlyList<string> GroundworkMethods { get; } = BenchmarkMethodology.Cases
        .Where(item => item.Stack == "Groundwork")
        .Select(item => $"{item.Workload}_{item.Stack}")
        .ToArray();

    private static GateRun RunGate(
        JsonObject policy,
        JsonObject baseline,
        JsonObject candidate,
        Func<string, string>? baselineManifestMutator = null,
        Func<string, string>? candidateManifestMutator = null)
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundwork-performance-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var policyPath = Path.Combine(directory, "policy.json");
            var baselineManifestPath = Path.Combine(directory, "baseline-manifest.txt");
            var baselineResultPath = Path.Combine(directory, "baseline.json");
            var candidateManifestPath = Path.Combine(directory, "candidate-manifest.txt");
            var candidateResultPath = Path.Combine(directory, "candidate.json");
            File.WriteAllText(baselineResultPath, baseline.ToJsonString());
            File.WriteAllText(candidateResultPath, candidate.ToJsonString());
            if (policy["baselineResultSha256"]!.GetValue<string>() == "<auto>")
                policy["baselineResultSha256"] = Sha256(baselineResultPath);
            File.WriteAllText(policyPath, policy.ToJsonString());
            var baselineManifest = Manifest(BaselineSha, baselineResultPath);
            var candidateManifest = Manifest(CandidateSha, candidateResultPath);
            File.WriteAllText(
                baselineManifestPath,
                baselineManifestMutator?.Invoke(baselineManifest) ?? baselineManifest);
            File.WriteAllText(
                candidateManifestPath,
                candidateManifestMutator?.Invoke(candidateManifest) ?? candidateManifest);

            using var output = new StringWriter(CultureInfo.InvariantCulture);
            using var error = new StringWriter(CultureInfo.InvariantCulture);
            var exitCode = BenchmarkRegressionGate.Run(
                [
                    "--policy", policyPath,
                    "--baseline-manifest", baselineManifestPath,
                    "--baseline-result", baselineResultPath,
                    "--candidate-sha", CandidateSha,
                    "--candidate-manifest", candidateManifestPath,
                    "--candidate-result", candidateResultPath
                ],
                output,
                error);
            return new GateRun(exitCode, output.ToString(), error.ToString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static JsonObject CompletePolicy(
        double maxMeanRatio = 1.10,
        double maxAllocatedRatio = 1.25) => new()
        {
            ["schemaVersion"] = 1,
            ["baselineSha"] = BaselineSha,
            ["baselineResultSha256"] = "<auto>",
            ["schemaFingerprint"] = BenchmarkMethodology.SchemaFingerprint,
            ["benchmarkDotNetVersion"] = BenchmarkDotNetVersion,
            ["budgets"] = new JsonArray(GroundworkMethods.Select(method => (JsonNode)new JsonObject
            {
                ["method"] = method,
                ["maxMeanRatio"] = maxMeanRatio,
                ["maxAllocatedRatio"] = maxAllocatedRatio
            }).ToArray())
        };

    private static JsonObject CompleteEvidence() => new()
    {
        ["Title"] = "Groundwork performance gate fixture",
        ["HostEnvironmentInfo"] = Environment(),
        ["Benchmarks"] = new JsonArray(AllMethods.Select(method =>
            (JsonNode)Benchmark(method, mean: 100, allocatedBytes: 64)).ToArray())
    };

    private static JsonObject Benchmark(string method, double mean, double allocatedBytes) => new()
    {
        ["Namespace"] = "Groundwork.Benchmarks",
        ["Type"] = "StorageBenchmarks",
        ["Method"] = method,
        ["HardwareIntrinsics"] = "AdvSimd, Aes, ArmBase",
        ["Statistics"] = new JsonObject { ["Mean"] = mean },
        ["Memory"] = new JsonObject { ["BytesAllocatedPerOperation"] = allocatedBytes }
    };

    private static JsonObject Environment() => new()
    {
        ["BenchmarkDotNetVersion"] = BenchmarkDotNetVersion,
        ["OsVersion"] = "Groundwork Test OS",
        ["ProcessorName"] = "Groundwork Test CPU",
        ["PhysicalProcessorCount"] = 1,
        ["PhysicalCoreCount"] = 8,
        ["LogicalCoreCount"] = 8,
        ["RuntimeVersion"] = ".NET 10.0.8",
        ["Architecture"] = "Arm64",
        ["HasAttachedDebugger"] = false,
        ["HasRyuJit"] = true,
        ["Configuration"] = "RELEASE",
        ["DotNetCliVersion"] = "10.0.300",
        ["ChronometerFrequency"] = 24_000_000,
        ["HardwareTimerKind"] = "MachAbsoluteTime"
    };

    private static string Manifest(string sha, string resultPath) =>
        $"commit={sha}\n" +
        $"schema_fingerprint={BenchmarkMethodology.SchemaFingerprint}\n" +
        "host=groundwork-controlled-host\n" +
        "host_idle_confirmation=true\n" +
        $"benchmark_result={Path.GetFileName(resultPath)}\n" +
        $"benchmark_result_sha256={Sha256(resultPath)}\n";

    private static string ReplaceManifestValue(string manifest, string key, string value) => string.Join(
        '\n',
        manifest.Split('\n').Select(line => line.StartsWith(key + "=", StringComparison.Ordinal)
            ? $"{key}={value}"
            : line));

    private static string RemoveManifestValue(string manifest, string key) => string.Join(
        '\n',
        manifest.Split('\n').Where(line => !line.StartsWith(key + "=", StringComparison.Ordinal)));

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static JsonObject FindBenchmark(JsonObject evidence, string method) => evidence["Benchmarks"]!
        .AsArray()
        .Select(node => node!.AsObject())
        .Single(node => node["Method"]!.GetValue<string>() == method);

    private static void SetMetrics(JsonObject evidence, string method, double mean, double allocatedBytes)
    {
        var benchmark = FindBenchmark(evidence, method);
        benchmark["Statistics"]!["Mean"] = mean;
        benchmark["Memory"]!["BytesAllocatedPerOperation"] = allocatedBytes;
    }

    private sealed record GateRun(int ExitCode, string Output, string Error);
}
