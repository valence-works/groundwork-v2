using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Groundwork.Benchmarks;

internal static class BenchmarkRegressionGate
{
    private const string SupportedBenchmarkDotNetVersion = "0.15.8";
    private const int InvalidInputExitCode = 2;

    private static readonly string[] RequiredOptions =
    [
        "--policy",
        "--baseline-manifest",
        "--baseline-result",
        "--candidate-sha",
        "--candidate-manifest",
        "--candidate-result"
    ];

    private static readonly string[] EnvironmentProperties =
    [
        "BenchmarkDotNetVersion",
        "OsVersion",
        "ProcessorName",
        "PhysicalProcessorCount",
        "PhysicalCoreCount",
        "LogicalCoreCount",
        "RuntimeVersion",
        "Architecture",
        "HasAttachedDebugger",
        "HasRyuJit",
        "Configuration",
        "DotNetCliVersion",
        "ChronometerFrequency",
        "HardwareTimerKind"
    ];

    private static readonly JsonSerializerOptions PolicyJsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            var options = ParseOptions(args);
            var policy = ReadPolicy(options["--policy"]);
            var baselineManifest = ValidateManifest(
                options["--baseline-manifest"],
                "baseline",
                policy.BaselineSha,
                policy.SchemaFingerprint,
                options["--baseline-result"],
                policy.BaselineResultSha256);
            var candidateManifest = ValidateManifest(
                options["--candidate-manifest"],
                "candidate",
                options["--candidate-sha"],
                policy.SchemaFingerprint,
                options["--candidate-result"],
                expectedResultHash: null);
            if (!string.Equals(baselineManifest.Host, candidateManifest.Host, StringComparison.Ordinal))
            {
                throw new GateInputException(
                    $"Controlled host mismatch: baseline='{baselineManifest.Host}', " +
                    $"candidate='{candidateManifest.Host}'.");
            }

            var baseline = ReadEvidence(options["--baseline-result"], "baseline", policy.BenchmarkDotNetVersion);
            var candidate = ReadEvidence(options["--candidate-result"], "candidate", policy.BenchmarkDotNetVersion);
            ValidateEnvironment(baseline.Environment, candidate.Environment);

            var failed = false;
            foreach (var method in ExpectedGroundworkMethods)
            {
                var budget = policy.BudgetsByMethod[method];
                var baselineMetrics = baseline.Benchmarks[method];
                var candidateMetrics = candidate.Benchmarks[method];
                var meanRatio = candidateMetrics.Mean / baselineMetrics.Mean;
                var allocatedRatio = baselineMetrics.AllocatedBytes == 0
                    ? candidateMetrics.AllocatedBytes == 0 ? 1 : double.PositiveInfinity
                    : candidateMetrics.AllocatedBytes / baselineMetrics.AllocatedBytes;
                var meanExceeded = Exceeds(meanRatio, budget.MaxMeanRatio);
                var allocationExceeded = Exceeds(allocatedRatio, budget.MaxAllocatedRatio);
                failed |= meanExceeded || allocationExceeded;

                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{(meanExceeded || allocationExceeded ? "FAIL" : "PASS")} {method}: " +
                    $"mean ratio {FormatRatio(meanRatio)} <= {budget.MaxMeanRatio:F4}; " +
                    $"allocated ratio {FormatRatio(allocatedRatio)} <= {budget.MaxAllocatedRatio:F4}"));
            }

            return failed ? 1 : 0;
        }
        catch (Exception exception) when (
            exception is GateInputException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            error.WriteLine($"Invalid performance evidence: {exception.Message}");
            return InvalidInputExitCode;
        }
    }

    private static IReadOnlyDictionary<string, string> ParseOptions(string[] args)
    {
        if (args.Length % 2 != 0)
            throw new GateInputException("Every performance-gate option requires one value.");

        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            var name = args[index];
            if (!RequiredOptions.Contains(name, StringComparer.Ordinal))
                throw new GateInputException($"Unknown performance-gate option '{name}'.");
            if (!options.TryAdd(name, args[index + 1]))
                throw new GateInputException($"Duplicate performance-gate option '{name}'.");
            if (string.IsNullOrWhiteSpace(args[index + 1]))
                throw new GateInputException($"Performance-gate option '{name}' cannot be empty.");
        }

        foreach (var required in RequiredOptions)
        {
            if (!options.ContainsKey(required))
                throw new GateInputException($"Missing performance-gate option '{required}'.");
        }
        ValidateSha(options["--candidate-sha"], "candidate SHA");
        return options;
    }

    private static ValidatedPolicy ReadPolicy(string path)
    {
        var policy = JsonSerializer.Deserialize<GatePolicy>(File.ReadAllText(path), PolicyJsonOptions)
            ?? throw new GateInputException("The performance gate policy is empty.");
        if (policy.SchemaVersion != 1)
            throw new GateInputException($"Unsupported policy schemaVersion '{policy.SchemaVersion}'.");
        if (string.IsNullOrWhiteSpace(policy.BaselineSha))
            throw new GateInputException("Policy baselineSha is missing.");
        ValidateSha(policy.BaselineSha, "policy baselineSha");
        ValidateSha256(policy.BaselineResultSha256, "policy baselineResultSha256");
        if (!string.Equals(policy.SchemaFingerprint, BenchmarkMethodology.SchemaFingerprint, StringComparison.Ordinal))
        {
            throw new GateInputException(
                $"Policy schemaFingerprint '{policy.SchemaFingerprint}' does not match the current benchmark schema " +
                $"'{BenchmarkMethodology.SchemaFingerprint}'.");
        }
        if (!string.Equals(policy.BenchmarkDotNetVersion, SupportedBenchmarkDotNetVersion, StringComparison.Ordinal))
        {
            throw new GateInputException(
                $"Policy benchmarkDotNetVersion must be '{SupportedBenchmarkDotNetVersion}', " +
                $"not '{policy.BenchmarkDotNetVersion}'.");
        }

        if (policy.Budgets is null)
            throw new GateInputException("Policy budgets are missing.");
        var budgets = new Dictionary<string, GateBudget>(StringComparer.Ordinal);
        foreach (var budget in policy.Budgets)
        {
            if (budget is null)
                throw new GateInputException("Policy contains a null budget.");
            if (string.IsNullOrWhiteSpace(budget.Method))
                throw new GateInputException("A performance budget has an empty method.");
            if (!budgets.TryAdd(budget.Method, budget))
                throw new GateInputException($"Policy has a duplicate budget for '{budget.Method}'.");
            if (!double.IsFinite(budget.MaxMeanRatio) || budget.MaxMeanRatio < 1)
                throw new GateInputException($"Budget '{budget.Method}' has invalid maxMeanRatio.");
            if (!double.IsFinite(budget.MaxAllocatedRatio) || budget.MaxAllocatedRatio < 1)
                throw new GateInputException($"Budget '{budget.Method}' has invalid maxAllocatedRatio.");
        }
        ValidateExactSet(budgets.Keys, ExpectedGroundworkMethods, "policy budget");
        return new ValidatedPolicy(
            policy.BaselineSha,
            policy.BaselineResultSha256,
            policy.SchemaFingerprint,
            policy.BenchmarkDotNetVersion,
            budgets);
    }

    private static ValidatedManifest ValidateManifest(
        string path,
        string label,
        string expectedSha,
        string expectedFingerprint,
        string resultPath,
        string? expectedResultHash)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;
            var key = line[..separator];
            if (key is not (
                "commit" or
                "schema_fingerprint" or
                "host" or
                "host_idle_confirmation" or
                "benchmark_result" or
                "benchmark_result_sha256"))
                continue;
            if (!values.TryGetValue(key, out var occurrences))
            {
                occurrences = [];
                values.Add(key, occurrences);
            }
            occurrences.Add(line[(separator + 1)..]);
        }

        var commit = ExactlyOne(values, "commit", label);
        ValidateSha(commit, $"{label} manifest commit");
        if (!string.Equals(commit, expectedSha, StringComparison.Ordinal))
            throw new GateInputException($"{label} manifest SHA '{commit}' does not match expected '{expectedSha}'.");
        var fingerprint = ExactlyOne(values, "schema_fingerprint", label);
        if (!string.Equals(fingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            throw new GateInputException(
                $"{label} manifest fingerprint '{fingerprint}' does not match expected '{expectedFingerprint}'.");
        }
        var host = ExactlyOne(values, "host", label);
        if (string.IsNullOrWhiteSpace(host))
            throw new GateInputException($"{label} manifest host is empty.");
        var idleConfirmation = ExactlyOne(values, "host_idle_confirmation", label);
        if (!string.Equals(idleConfirmation, "true", StringComparison.Ordinal))
            throw new GateInputException($"{label} manifest host_idle_confirmation must be 'true'.");

        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new GateInputException($"{label} manifest has no parent directory.");
        var relativeResultPath = Path.GetRelativePath(manifestDirectory, Path.GetFullPath(resultPath))
            .Replace('\\', '/');
        if (Path.IsPathRooted(relativeResultPath) ||
            relativeResultPath.Equals("..", StringComparison.Ordinal) ||
            relativeResultPath.StartsWith("../", StringComparison.Ordinal))
        {
            throw new GateInputException($"{label} benchmark result is outside its manifest bundle.");
        }
        var declaredResultPath = ExactlyOne(values, "benchmark_result", label);
        if (!IsBundleRelativePath(declaredResultPath))
            throw new GateInputException($"{label} manifest benchmark_result is outside its manifest bundle.");
        if (!string.Equals(declaredResultPath, relativeResultPath, StringComparison.Ordinal))
        {
            throw new GateInputException(
                $"{label} manifest benchmark_result '{declaredResultPath}' does not match '{relativeResultPath}'.");
        }
        var declaredResultHash = ExactlyOne(values, "benchmark_result_sha256", label);
        ValidateSha256(declaredResultHash, $"{label} manifest benchmark_result_sha256");
        if (expectedResultHash is not null &&
            !string.Equals(declaredResultHash, expectedResultHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new GateInputException(
                $"{label} manifest benchmark result hash '{declaredResultHash}' does not match the " +
                $"policy baselineResultSha256 '{expectedResultHash}'.");
        }
        var actualResultHash = Sha256(resultPath);
        if (!string.Equals(declaredResultHash, actualResultHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new GateInputException(
                $"{label} manifest benchmark result hash '{declaredResultHash}' does not match '{actualResultHash}'.");
        }
        return new ValidatedManifest(host);
    }

    private static bool IsBundleRelativePath(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !Path.IsPathRooted(path) &&
        !path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .Contains("..", StringComparer.Ordinal);

    private static string ExactlyOne(
        IReadOnlyDictionary<string, List<string>> values,
        string key,
        string label)
    {
        if (!values.TryGetValue(key, out var occurrences) || occurrences.Count == 0)
            throw new GateInputException($"{label} manifest is missing '{key}'.");
        if (occurrences.Count != 1)
            throw new GateInputException($"{label} manifest contains duplicate '{key}' values.");
        return occurrences[0];
    }

    private static Evidence ReadEvidence(string path, string label, string expectedBenchmarkDotNetVersion)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = RequireObject(document.RootElement, label, "root");
        var environment = RequireObject(RequireProperty(root, "HostEnvironmentInfo", label), label, "HostEnvironmentInfo");
        var environmentValues = ReadEnvironment(environment, label);
        var version = RequireString(environment, "BenchmarkDotNetVersion", label, "HostEnvironmentInfo");
        if (!string.Equals(version, expectedBenchmarkDotNetVersion, StringComparison.Ordinal))
        {
            throw new GateInputException(
                $"{label} benchmarkdotnet-version '{version}' does not match policy '{expectedBenchmarkDotNetVersion}'.");
        }
        if (RequireBoolean(environment, "HasAttachedDebugger", label, "HostEnvironmentInfo"))
            throw new GateInputException($"{label} evidence was captured with an attached debugger.");
        var configuration = RequireString(environment, "Configuration", label, "HostEnvironmentInfo");
        if (!string.Equals(configuration, "RELEASE", StringComparison.OrdinalIgnoreCase))
            throw new GateInputException($"{label} evidence configuration '{configuration}' is not Release.");

        var benchmarkArray = RequireProperty(root, "Benchmarks", label);
        if (benchmarkArray.ValueKind != JsonValueKind.Array)
            throw new GateInputException($"{label} Benchmarks is not an array.");
        var benchmarks = new Dictionary<string, Metrics>(StringComparer.Ordinal);
        string? hardwareIntrinsics = null;
        foreach (var element in benchmarkArray.EnumerateArray())
        {
            var benchmark = RequireObject(element, label, "Benchmarks item");
            var benchmarkNamespace = RequireString(benchmark, "Namespace", label, "benchmark");
            var type = RequireString(benchmark, "Type", label, "benchmark");
            var method = RequireString(benchmark, "Method", label, "benchmark");
            if (!string.Equals(benchmarkNamespace, "Groundwork.Benchmarks", StringComparison.Ordinal) ||
                !string.Equals(type, nameof(StorageBenchmarks), StringComparison.Ordinal))
            {
                throw new GateInputException(
                    $"{label} contains unexpected benchmark '{benchmarkNamespace}.{type}.{method}'.");
            }
            if (benchmarks.ContainsKey(method))
                throw new GateInputException($"{label} contains duplicate benchmark '{method}'.");

            var benchmarkHardwareIntrinsics = RequireString(benchmark, "HardwareIntrinsics", label, method);
            if (string.IsNullOrWhiteSpace(benchmarkHardwareIntrinsics))
                throw new GateInputException($"{label} benchmark '{method}' HardwareIntrinsics is empty.");
            hardwareIntrinsics ??= benchmarkHardwareIntrinsics;
            if (!string.Equals(hardwareIntrinsics, benchmarkHardwareIntrinsics, StringComparison.Ordinal))
            {
                throw new GateInputException(
                    $"{label} benchmark '{method}' HardwareIntrinsics differs within the report.");
            }

            if (!benchmark.TryGetProperty("Statistics", out var statisticsProperty))
                throw new GateInputException($"{label} benchmark '{method}' is missing Statistics.");
            var statistics = RequireObject(statisticsProperty, label, $"{method}.Statistics");
            var mean = RequireFiniteNumber(statistics, "Mean", label, method);
            if (mean <= 0)
                throw new GateInputException($"{label} benchmark '{method}' Statistics.Mean must be positive.");
            if (!benchmark.TryGetProperty("Memory", out var memoryProperty))
                throw new GateInputException($"{label} benchmark '{method}' is missing Memory.");
            var memory = RequireObject(memoryProperty, label, $"{method}.Memory");
            var allocatedBytes = RequireFiniteNumber(memory, "BytesAllocatedPerOperation", label, method);
            if (allocatedBytes < 0)
            {
                throw new GateInputException(
                    $"{label} benchmark '{method}' Memory.BytesAllocatedPerOperation cannot be negative.");
            }
            benchmarks.Add(method, new Metrics(mean, allocatedBytes));
        }
        ValidateExactSet(benchmarks.Keys, ExpectedMethods, $"{label} benchmark");
        environmentValues.Add(
            "HardwareIntrinsics",
            hardwareIntrinsics ?? throw new GateInputException($"{label} has no benchmark HardwareIntrinsics."));
        return new Evidence(environmentValues, benchmarks);
    }

    private static Dictionary<string, string> ReadEnvironment(JsonElement environment, string label)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var propertyName in EnvironmentProperties)
        {
            var value = RequireProperty(environment, propertyName, label);
            var isValid = propertyName switch
            {
                "PhysicalProcessorCount" or "PhysicalCoreCount" or "LogicalCoreCount" or "ChronometerFrequency" =>
                    value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) && number > 0,
                "HasAttachedDebugger" or "HasRyuJit" =>
                    value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                _ => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            };
            if (!isValid)
                throw new GateInputException($"{label} HostEnvironmentInfo.{propertyName} has an invalid value.");
            values.Add(propertyName, value.GetRawText());
        }
        return values;
    }

    private static void ValidateEnvironment(
        IReadOnlyDictionary<string, string> baseline,
        IReadOnlyDictionary<string, string> candidate)
    {
        foreach (var propertyName in baseline.Keys.OrderBy(item => item, StringComparer.Ordinal))
        {
            if (!string.Equals(baseline[propertyName], candidate[propertyName], StringComparison.Ordinal))
            {
                throw new GateInputException(
                    $"Measured environment mismatch for {propertyName}: baseline={baseline[propertyName]}, " +
                    $"candidate={candidate[propertyName]}.");
            }
        }
    }

    private static void ValidateExactSet(
        IEnumerable<string> actualValues,
        IReadOnlyList<string> expectedValues,
        string label)
    {
        var actual = actualValues.ToHashSet(StringComparer.Ordinal);
        var missing = expectedValues.Where(item => !actual.Contains(item)).ToArray();
        if (missing.Length > 0)
            throw new GateInputException($"{label} set is missing: {string.Join(", ", missing)}.");
        var expected = expectedValues.ToHashSet(StringComparer.Ordinal);
        var unexpected = actual.Where(item => !expected.Contains(item)).OrderBy(item => item, StringComparer.Ordinal).ToArray();
        if (unexpected.Length > 0)
            throw new GateInputException($"{label} set contains unexpected values: {string.Join(", ", unexpected)}.");
    }

    private static JsonElement RequireProperty(JsonElement value, string propertyName, string label)
    {
        if (!value.TryGetProperty(propertyName, out var property))
            throw new GateInputException($"{label} JSON is missing '{propertyName}'.");
        return property;
    }

    private static JsonElement RequireObject(JsonElement value, string label, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new GateInputException($"{label} {name} is not an object.");
        return value;
    }

    private static string RequireString(JsonElement value, string propertyName, string label, string owner)
    {
        var property = RequireProperty(value, propertyName, label);
        if (property.ValueKind != JsonValueKind.String)
            throw new GateInputException($"{label} {owner}.{propertyName} is not a string.");
        return property.GetString()!;
    }

    private static bool RequireBoolean(JsonElement value, string propertyName, string label, string owner)
    {
        var property = RequireProperty(value, propertyName, label);
        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new GateInputException($"{label} {owner}.{propertyName} is not a boolean.");
        return property.GetBoolean();
    }

    private static double RequireFiniteNumber(JsonElement owner, string propertyName, string label, string method)
    {
        if (!owner.TryGetProperty(propertyName, out var property))
            throw new GateInputException($"{label} benchmark '{method}' is missing {ownerName(propertyName)}.");
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var value) || !double.IsFinite(value))
            throw new GateInputException($"{label} benchmark '{method}' {ownerName(propertyName)} is not finite.");
        return value;

        static string ownerName(string name) => name == "Mean"
            ? "Statistics.Mean"
            : "Memory.BytesAllocatedPerOperation";
    }

    private static void ValidateSha(string value, string label)
    {
        if (value.Length != 40 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new GateInputException($"{label} must be an exact 40-character hexadecimal commit SHA.");
    }

    private static void ValidateSha256(string? value, string label)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new GateInputException($"{label} must be an exact 64-character hexadecimal SHA-256 digest.");
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool Exceeds(double actual, double limit) => actual > limit;

    private static string FormatRatio(double value) => double.IsPositiveInfinity(value)
        ? "infinity"
        : value.ToString("F4", CultureInfo.InvariantCulture);

    private static IReadOnlyList<string> ExpectedMethods { get; } = BenchmarkMethodology.Cases
        .Select(item => $"{item.Workload}_{item.Stack}")
        .ToArray();

    private static IReadOnlyList<string> ExpectedGroundworkMethods { get; } = BenchmarkMethodology.Cases
        .Where(item => item.Stack == "Groundwork")
        .Select(item => $"{item.Workload}_{item.Stack}")
        .ToArray();

    private sealed record GatePolicy
    {
        public required int SchemaVersion { get; init; }
        public required string BaselineSha { get; init; }
        public required string BaselineResultSha256 { get; init; }
        public required string SchemaFingerprint { get; init; }
        public required string BenchmarkDotNetVersion { get; init; }
        public required GateBudget[] Budgets { get; init; }
    }

    private sealed record GateBudget
    {
        public required string Method { get; init; }
        public required double MaxMeanRatio { get; init; }
        public required double MaxAllocatedRatio { get; init; }
    }

    private sealed record ValidatedPolicy(
        string BaselineSha,
        string BaselineResultSha256,
        string SchemaFingerprint,
        string BenchmarkDotNetVersion,
        IReadOnlyDictionary<string, GateBudget> BudgetsByMethod);

    private sealed record ValidatedManifest(string Host);

    private sealed record Metrics(double Mean, double AllocatedBytes);

    private sealed record Evidence(
        IReadOnlyDictionary<string, string> Environment,
        IReadOnlyDictionary<string, Metrics> Benchmarks);

    private sealed class GateInputException(string message) : Exception(message);
}
