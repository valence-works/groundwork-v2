using System.Collections.Concurrent;
using System.Diagnostics;
using Groundwork.Kernel;

namespace Groundwork.Testing;

/// <summary>The concurrency behavior exercised by the provider-neutral write harness.</summary>
public enum ConcurrencyKind
{
    None,
    Optimistic
}

/// <summary>Options for one deterministic concurrency scenario.</summary>
public sealed record ConcurrencyProbeOptions
{
    public int WriterCount { get; init; } = 32;

    public int KeyCount { get; init; } = 1;

    public int RepeatCount { get; init; } = 2;

    public int MaxAttemptsPerWrite { get; init; } = 128;

    public int Seed { get; init; } = 245;

    public ConcurrencyKind Concurrency { get; init; } = ConcurrencyKind.Optimistic;

    public bool IncludePartialUniqueIndex { get; init; }
}

/// <summary>One deterministic logical write submitted by the harness.</summary>
public sealed record ConcurrencyWriteRequest(
    string Key,
    string? Value,
    DateTimeOffset CreatedAt,
    long? ExpectedVersion);

public enum ConcurrencyWriteOutcomeStatus
{
    Inserted,
    Updated,
    ConcurrencyConflict
}

/// <summary>The exact outcome returned by a concurrency adapter.</summary>
public sealed record ConcurrencyWriteOutcome(
    ConcurrencyWriteOutcomeStatus Status,
    long Version);

/// <summary>The observable row state used by the invariant checks.</summary>
public sealed record ConcurrencyStoredRow(
    string Key,
    string? Value,
    DateTimeOffset CreatedAt,
    long Version);

/// <summary>
/// Provider-neutral adapter seam for the single conditional-upsert operation. Provider packages
/// implement this seam when their native write path is available; the storage-provider bridge
/// below covers the shipped reference and Mongo adapters.
/// </summary>
public interface IConcurrencyProviderFactory
{
    string ProviderName { get; }

    IConcurrencyProviderConnection Create(
        string connectionString,
        StorageUnit declaration);
}

public interface IConcurrencyProviderConnection : IDisposable
{
    void ApplySchema();

    IConcurrencyProviderSession OpenSession();
}

public interface IConcurrencyProviderSession : IDisposable
{
    ConcurrencyWriteOutcome ConditionalUpsert(ConcurrencyWriteRequest request);

    ConcurrencyStoredRow? Read(string key);
}

/// <summary>One named invariant result, retained individually for diagnostics and negative tests.</summary>
public sealed record ConcurrencyInvariantResult(
    string Name,
    bool Passed,
    string Detail);

/// <summary>Machine-load evidence captured around one scenario.</summary>
public sealed record MachineLoadSnapshot(
    int ProcessorCount,
    double ProcessCpuPercent,
    long ManagedMemoryBytes)
{
    internal static MachineLoadMeasurement Start() => new(
        Process.GetCurrentProcess().TotalProcessorTime,
        Stopwatch.GetTimestamp());

    internal static MachineLoadSnapshot Stop(MachineLoadMeasurement start)
    {
        using var process = Process.GetCurrentProcess();
        var elapsedTicks = Stopwatch.GetTimestamp() - start.Timestamp;
        var elapsed = elapsedTicks / (double)Stopwatch.Frequency;
        var cpu = elapsed <= 0
            ? 0
            : (process.TotalProcessorTime - start.Cpu).TotalSeconds / elapsed /
              Math.Max(1, Environment.ProcessorCount) * 100;
        return new(
            Environment.ProcessorCount,
            Math.Round(cpu, 2, MidpointRounding.AwayFromZero),
            GC.GetTotalMemory(forceFullCollection: false));
    }
}

internal readonly record struct MachineLoadMeasurement(TimeSpan Cpu, long Timestamp);

public sealed record ConcurrencyAcceptedWrite(
    string Key,
    string? Value,
    DateTimeOffset CreatedAt,
    long Version,
    ConcurrencyWriteOutcomeStatus Status,
    int Writer);

public sealed class ConcurrencyScenarioReport
{
    internal ConcurrencyScenarioReport(
        int seed,
        ConcurrencyProbeOptions options,
        IReadOnlyList<ConcurrencyWriteOutcome> outcomes,
        IReadOnlyList<ConcurrencyAcceptedWrite> acceptedWrites,
        IReadOnlyDictionary<string, ConcurrencyStoredRow?> finalRows,
        IReadOnlyList<ConcurrencyInvariantResult> invariants,
        MachineLoadSnapshot machineLoad,
        IReadOnlyList<string> errors)
    {
        Seed = seed;
        Options = options;
        Outcomes = outcomes;
        AcceptedWrites = acceptedWrites;
        FinalRows = finalRows;
        Invariants = invariants;
        MachineLoad = machineLoad;
        Errors = errors;
    }

    public int Seed { get; }

    public ConcurrencyProbeOptions Options { get; }

    public IReadOnlyList<ConcurrencyWriteOutcome> Outcomes { get; }

    public IReadOnlyList<ConcurrencyAcceptedWrite> AcceptedWrites { get; }

    public IReadOnlyDictionary<string, ConcurrencyStoredRow?> FinalRows { get; }

    public IReadOnlyList<ConcurrencyInvariantResult> Invariants { get; }

    public MachineLoadSnapshot MachineLoad { get; }

    public IReadOnlyList<string> Errors { get; }

    public bool Passed => Errors.Count == 0 && Invariants.All(invariant => invariant.Passed);
}

public sealed class ConcurrencyHarnessReport
{
    internal ConcurrencyHarnessReport(
        string providerName,
        IReadOnlyList<ConcurrencyScenarioReport> scenarios)
    {
        ProviderName = providerName;
        Scenarios = scenarios;
    }

    public string ProviderName { get; }

    public IReadOnlyList<ConcurrencyScenarioReport> Scenarios { get; }

    public bool Passed => Scenarios.Count != 0 && Scenarios.All(scenario => scenario.Passed);

    public IReadOnlyList<ConcurrencyInvariantResult> Failures =>
        Scenarios.SelectMany(scenario => scenario.Invariants)
            .Where(invariant => !invariant.Passed)
            .ToArray();

    public override string ToString() =>
        $"{ProviderName}: {Scenarios.Count} scenario(s), " +
        $"{Scenarios.Sum(scenario => scenario.Outcomes.Count)} outcomes, " +
        $"passed={Passed}; " +
        string.Join("; ", Scenarios.Select(scenario =>
            $"seed={scenario.Seed},writers={scenario.Options.WriterCount},keys={scenario.Options.KeyCount}," +
            $"load={scenario.MachineLoad.ProcessCpuPercent:F2}%/{scenario.MachineLoad.ProcessorCount}cpu"));
}

/// <summary>Runs deterministic concurrent conditional-upsert scenarios and named invariant checks.</summary>
public static class ConcurrencyHarness
{
    public static ConcurrencyHarnessReport Run(
        IConcurrencyProviderFactory factory,
        string connectionString,
        ConcurrencyProbeOptions options)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        var scenarios = new List<ConcurrencyScenarioReport>(options.RepeatCount);
        for (var repeat = 0; repeat < options.RepeatCount; repeat++)
        {
            var seed = checked(options.Seed + repeat);
            var declaration = CreateDeclaration(factory.ProviderName, seed, options);
            using var connection = factory.Create(connectionString, declaration);
            connection.ApplySchema();
            scenarios.Add(RunScenario(connection, declaration, options, seed));
        }

        return new ConcurrencyHarnessReport(factory.ProviderName, scenarios);
    }

    private static ConcurrencyScenarioReport RunScenario(
        IConcurrencyProviderConnection connection,
        StorageUnit declaration,
        ConcurrencyProbeOptions options,
        int seed)
    {
        var measurement = MachineLoadSnapshot.Start();
        var outcomes = new ConcurrentBag<ConcurrencyWriteOutcome>();
        var accepted = new ConcurrentBag<ConcurrencyAcceptedWrite>();
        var firstAccepted = new ConcurrentDictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var errors = new ConcurrentBag<string>();
        using var ready = new CountdownEvent(options.WriterCount);
        using var start = new ManualResetEventSlim(false);
        var workers = Enumerable.Range(0, options.WriterCount)
            .Select(writer => Task.Run(() => RunWriter(
                connection,
                options,
                seed,
                writer,
                ready,
                start,
                outcomes,
                accepted,
                firstAccepted,
                errors)))
            .ToArray();

        ready.Wait();
        start.Set();
        try
        {
            Task.WaitAll(workers);
        }
        catch (AggregateException exception)
        {
            foreach (var inner in exception.Flatten().InnerExceptions)
                errors.Add(inner.Message);
        }

        var finalRows = new Dictionary<string, ConcurrencyStoredRow?>(StringComparer.Ordinal);
        using (var reader = connection.OpenSession())
        {
            for (var key = 0; key < options.KeyCount; key++)
            {
                var keyName = Key(seed, key);
                finalRows[keyName] = reader.Read(keyName);
            }
        }

        var invariantResults = EvaluateInvariants(
            options,
            outcomes.ToArray(),
            accepted.ToArray(),
            firstAccepted,
            finalRows,
            errors);
        return new ConcurrencyScenarioReport(
            seed,
            options,
            outcomes.OrderBy(outcome => outcome.Status).ToArray(),
            accepted.OrderBy(write => write.Key, StringComparer.Ordinal).ThenBy(write => write.Version).ToArray(),
            finalRows,
            invariantResults,
            MachineLoadSnapshot.Stop(measurement),
            errors.OrderBy(error => error, StringComparer.Ordinal).ToArray());
    }

    private static void RunWriter(
        IConcurrencyProviderConnection connection,
        ConcurrencyProbeOptions options,
        int seed,
        int writer,
        CountdownEvent ready,
        ManualResetEventSlim start,
        ConcurrentBag<ConcurrencyWriteOutcome> outcomes,
        ConcurrentBag<ConcurrencyAcceptedWrite> accepted,
        ConcurrentDictionary<string, DateTimeOffset> firstAccepted,
        ConcurrentBag<string> errors)
    {
        using var session = connection.OpenSession();
        ready.Signal();
        start.Wait();
        for (var keyIndex = 0; keyIndex < options.KeyCount; keyIndex++)
        {
            var key = Key(seed, keyIndex);
            var value = $"writer-{writer}-key-{keyIndex}";
            var createdAt = Timestamp(seed, writer, keyIndex);
            long? expected = null;
            var acceptedForKey = false;
            for (var attempt = 0; attempt < options.MaxAttemptsPerWrite; attempt++)
            {
                if (attempt > 0)
                {
                    var current = session.Read(key);
                    expected = options.Concurrency == ConcurrencyKind.Optimistic
                        ? current?.Version
                        : null;
                }

                var request = new ConcurrencyWriteRequest(key, value, createdAt, expected);
                var result = session.ConditionalUpsert(request);
                outcomes.Add(result);
                if (result.Status == ConcurrencyWriteOutcomeStatus.ConcurrencyConflict)
                    continue;

                if (result.Status is not (ConcurrencyWriteOutcomeStatus.Inserted or
                    ConcurrencyWriteOutcomeStatus.Updated))
                {
                    errors.Add($"Unexpected outcome '{result.Status}' for key '{key}'.");
                    break;
                }

                if (result.Status == ConcurrencyWriteOutcomeStatus.Inserted)
                {
                    firstAccepted.TryAdd(key, createdAt);
                }
                accepted.Add(new ConcurrencyAcceptedWrite(
                    key, value, createdAt, result.Version, result.Status, writer));
                acceptedForKey = true;
                break;
            }

            if (!acceptedForKey)
                errors.Add($"Writer {writer} exhausted attempts for key '{key}'.");
        }
    }

    private static IReadOnlyList<ConcurrencyInvariantResult> EvaluateInvariants(
        ConcurrencyProbeOptions options,
        IReadOnlyList<ConcurrencyWriteOutcome> outcomes,
        IReadOnlyList<ConcurrencyAcceptedWrite> accepted,
        IReadOnlyDictionary<string, DateTimeOffset> firstAccepted,
        IReadOnlyDictionary<string, ConcurrencyStoredRow?> finalRows,
        IReadOnlyCollection<string> errors)
    {
        var results = new List<ConcurrencyInvariantResult>();
        Add(results, "outcomes-are-exactly-inserted-updated-or-conflict",
            errors.Count == 0 && outcomes.Count > 0 && outcomes.All(outcome =>
                outcome.Status is ConcurrencyWriteOutcomeStatus.Inserted or
                ConcurrencyWriteOutcomeStatus.Updated or
                ConcurrencyWriteOutcomeStatus.ConcurrencyConflict),
            errors.Count == 0 ? "Every recorded outcome has one allowed status." : string.Join("; ", errors));

        var inserts = accepted.Count(write => write.Status == ConcurrencyWriteOutcomeStatus.Inserted);
        var distinctKeys = accepted.Select(write => write.Key).Distinct(StringComparer.Ordinal).Count();
        Add(results, "inserted-count-equals-distinct-keys",
            inserts == distinctKeys && distinctKeys == options.KeyCount,
            $"inserted={inserts}, distinctKeys={distinctKeys}, expectedKeys={options.KeyCount}");

        var versionFailures = new List<string>();
        foreach (var group in accepted.GroupBy(write => write.Key, StringComparer.Ordinal))
        {
            if (!finalRows.TryGetValue(group.Key, out var row) || row is null ||
                row.Version != group.Count())
            {
                versionFailures.Add($"{group.Key}: final={row?.Version.ToString() ?? "missing"}, accepted={group.Count()}");
            }
        }
        Add(results, "final-version-equals-accepted-writes", versionFailures.Count == 0,
            versionFailures.Count == 0 ? "Every key's final version equals its accepted-write count." :
            string.Join("; ", versionFailures));

        var createdAtFailures = firstAccepted
            .Where(pair => !finalRows.TryGetValue(pair.Key, out var row) || row is null || row.CreatedAt != pair.Value)
            .Select(pair => $"{pair.Key}: expected={pair.Value:O}, actual={finalRows[pair.Key]?.CreatedAt:O}")
            .ToArray();
        Add(results, "created-at-equals-first-accepted-write", createdAtFailures.Length == 0,
            createdAtFailures.Length == 0 ? "Every key preserved its first accepted timestamp." :
            string.Join("; ", createdAtFailures));

        var invalidOutcome = accepted
            .Where(write => write.Status is not (ConcurrencyWriteOutcomeStatus.Inserted or
                ConcurrencyWriteOutcomeStatus.Updated))
            .Select(write => $"{write.Key}:{write.Status}")
            .ToArray();
        Add(results, "no-row-is-observed-as-both-inserted-and-updated", invalidOutcome.Length == 0,
            invalidOutcome.Length == 0
                ? "Each accepted write has exactly one of the two mutually exclusive accepted statuses."
                : string.Join(", ", invalidOutcome));

        var lost = new List<string>();
        foreach (var group in accepted.GroupBy(write => write.Key, StringComparer.Ordinal))
        {
            if (!finalRows.TryGetValue(group.Key, out var row) || row is null)
            {
                lost.Add($"{group.Key}: final row missing");
                continue;
            }

            if (!group.Any(write => write.Value == row.Value))
            {
                lost.Add($"{group.Key}: final value '{row.Value}' was not accepted");
                continue;
            }

            if (options.Concurrency == ConcurrencyKind.Optimistic)
            {
                var matchingWrites = group.Where(write =>
                    write.Version == row.Version && write.Value == row.Value).ToArray();
                if (matchingWrites.Length != 1)
                    lost.Add($"{group.Key}: final ({row.Value}, v{row.Version}) matched " +
                        $"{matchingWrites.Length} accepted writes");
            }
        }
        Add(results, "no-accepted-write-is-lost", lost.Count == 0,
            lost.Count == 0
                ? options.Concurrency == ConcurrencyKind.Optimistic
                    ? "Every final value belongs to the uniquely identified accepted write."
                    : "Every final value belongs to an accepted write; None mode has no provider version token."
                :
            string.Join("; ", lost));
        return results;
    }

    private static void Add(
        ICollection<ConcurrencyInvariantResult> results,
        string name,
        bool passed,
        string detail) => results.Add(new ConcurrencyInvariantResult(name, passed, detail));

    private static StorageUnit CreateDeclaration(
        string providerName,
        int seed,
        ConcurrencyProbeOptions options) => new()
        {
            Id = new StorageUnitId($"w2-{providerName}-{seed}"),
            Name = $"w2_{providerName}_{seed}",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
                new ColumnDefinition { Name = "value", Type = PortableType.String, MaxLength = 256 },
                new ColumnDefinition { Name = "createdAt", Type = PortableType.DateTimeOffset, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Concurrency = options.Concurrency == ConcurrencyKind.Optimistic
                ? ConcurrencyDeclaration.Optimistic
                : ConcurrencyDeclaration.None,
            Indexes = options.IncludePartialUniqueIndex
                ?
                [
                    new IndexDefinition
                    {
                        Name = "ux_value_present",
                        Columns = [new IndexColumn("value")],
                        IsUnique = true,
                        MissingValues = MissingValueBehavior.Excluded
                    }
                ]
                : []
        };

    private static string Key(int seed, int key) => $"seed-{seed}-key-{key}";

    private static DateTimeOffset Timestamp(int seed, int writer, int key) =>
        DateTimeOffset.UnixEpoch.AddTicks(checked((long)seed * 10_000_000 + writer * 100_000L + key));

    private static void ValidateOptions(ConcurrencyProbeOptions options)
    {
        if (options.WriterCount <= 0 || options.KeyCount <= 0 || options.RepeatCount <= 0 ||
            options.MaxAttemptsPerWrite <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options),
                "WriterCount, KeyCount, RepeatCount, and MaxAttemptsPerWrite must be positive.");
        }
    }
}

/// <summary>
/// Bridges the shipped provider factory contract to the concurrency adapter seam. Providers with
/// a native conditional-upsert implementation can expose it through IConcurrencyStorageSession.
/// </summary>
public sealed class StorageProviderConcurrencyFactory : IConcurrencyProviderFactory
{
    private readonly IStorageProviderFactory provider;

    public StorageProviderConcurrencyFactory(string providerName, IStorageProviderFactory provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        ProviderName = providerName;
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public string ProviderName { get; }

    public IConcurrencyProviderConnection Create(string connectionString, StorageUnit declaration) =>
        new StorageProviderConcurrencyConnection(provider.Create(connectionString), declaration);
}

/// <summary>Optional extension implemented by a provider's testing adapter.</summary>
public interface IConcurrencyStorageSession
{
    WriteOutcome ConditionalUpsert(StorageValues values, WriteOptions? options = null);
}

internal sealed class StorageProviderConcurrencyConnection : IConcurrencyProviderConnection
{
    private readonly IStorageProviderConnection connection;
    private readonly StorageUnit declaration;
    private readonly ConcurrentDictionary<string, long> logicalVersions = new(StringComparer.Ordinal);

    internal StorageProviderConcurrencyConnection(
        IStorageProviderConnection connection,
        StorageUnit declaration)
    {
        this.connection = connection;
        this.declaration = declaration;
    }

    public void ApplySchema() => connection.Schema.Apply(declaration);

    public IConcurrencyProviderSession OpenSession() =>
        new StorageProviderConcurrencySession(
            connection.OpenSession(declaration, StorageAccess.Global),
            declaration,
            logicalVersions);

    public void Dispose() => connection.Dispose();
}

internal sealed class StorageProviderConcurrencySession : IConcurrencyProviderSession
{
    private readonly IStorageSession session;
    private readonly StorageUnit declaration;
    private readonly ConcurrentDictionary<string, long> logicalVersions;
    private bool disposed;

    internal StorageProviderConcurrencySession(
        IStorageSession session,
        StorageUnit declaration,
        ConcurrentDictionary<string, long> logicalVersions)
    {
        this.session = session;
        this.declaration = declaration;
        this.logicalVersions = logicalVersions;
    }

    public ConcurrencyWriteOutcome ConditionalUpsert(ConcurrencyWriteRequest request)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        if (session is not IConcurrencyStorageSession concurrencySession)
        {
            throw new NotSupportedException(
                $"Provider session '{session.GetType().FullName}' does not implement the W2 conditional-upsert adapter.");
        }

        var existing = session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = request.Key }));
        var createdAt = existing?.Values.Values.TryGetValue("createdAt", out var prior) == true &&
            prior is DateTimeOffset priorTimestamp
            ? priorTimestamp
            : request.CreatedAt;
        var values = new StorageValues(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = request.Key,
            ["value"] = request.Value,
            ["createdAt"] = createdAt
        });
        var options = declaration.Concurrency == ConcurrencyDeclaration.Optimistic
            ? new WriteOptions { ExpectedVersion = request.ExpectedVersion }
            : null;
        var result = concurrencySession.ConditionalUpsert(values, options);
        if (result.Status == WriteOutcomeStatus.ConcurrencyConflict)
            return new ConcurrencyWriteOutcome(ConcurrencyWriteOutcomeStatus.ConcurrencyConflict,
                result.Version ?? existing?.Version ?? 0);
        if (result.Status is not (WriteOutcomeStatus.Inserted or WriteOutcomeStatus.Updated))
        {
            throw new InvalidOperationException(
                $"Provider returned '{result.Status}' for the W2 conditional upsert.");
        }

        var version = declaration.Concurrency == ConcurrencyDeclaration.Optimistic
            ? result.Version ?? throw new InvalidOperationException(
                "An optimistic conditional upsert must return a version.")
            : logicalVersions.AddOrUpdate(request.Key, 1, (_, current) => checked(current + 1));
        return new ConcurrencyWriteOutcome(
            result.Status == WriteOutcomeStatus.Inserted
                ? ConcurrencyWriteOutcomeStatus.Inserted
                : ConcurrencyWriteOutcomeStatus.Updated,
            version);
    }

    public ConcurrencyStoredRow? Read(string key)
    {
        ThrowIfDisposed();
        var entry = session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = key }));
        if (entry is null)
            return null;
        if (!entry.Values.Values.TryGetValue("value", out var value) ||
            !entry.Values.Values.TryGetValue("createdAt", out var createdAt) ||
            createdAt is not DateTimeOffset timestamp)
        {
            throw new InvalidOperationException($"Provider returned an incomplete W2 row for key '{key}'.");
        }

        var version = declaration.Concurrency == ConcurrencyDeclaration.Optimistic
            ? entry.Version ?? throw new InvalidOperationException(
                "An optimistic W2 row must return a version.")
            : logicalVersions.TryGetValue(key, out var logical) ? logical : 0;
        return new ConcurrencyStoredRow(key, value as string, timestamp, version);
    }

    public void Dispose() => disposed = true;

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(StorageProviderConcurrencySession));
    }
}
