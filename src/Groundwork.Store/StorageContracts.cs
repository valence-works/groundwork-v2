using System.Collections.ObjectModel;
using System.Collections;
using System.Text.Json.Nodes;
using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Query.Model;

namespace Groundwork.Store;

/// <summary>Explicit access context for a testing session.</summary>
public sealed record StorageAccess
{
    private StorageAccess(ScopePolicy policy, StorageScope? scope)
    {
        Policy = policy;
        Scope = scope;
    }

    public static StorageAccess Global { get; } = new(ScopePolicy.Global, null);

    public ScopePolicy Policy { get; }

    public StorageScope? Scope { get; }

    public static StorageAccess Scoped(StorageScope scope) =>
        new(ScopePolicy.Scoped, scope ?? throw new ArgumentNullException(nameof(scope)));
}

/// <summary>A defensive snapshot of values belonging to one storage unit.</summary>
public sealed class StorageValues
{
    public StorageValues(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = Snapshot(values);
    }

    public IReadOnlyDictionary<string, object?> Values { get; }

    internal static IReadOnlyDictionary<string, object?> Snapshot(
        IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = values.ToDictionary(
            pair => pair.Key,
            pair => CloneValue(pair.Value),
            StringComparer.Ordinal);
        return new ReadOnlyDictionary<string, object?>(copy);
    }

    internal static object? CloneValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case byte[] bytes:
                return bytes.ToArray();
            case JsonNode node:
                return node.DeepClone();
            case JsonElement element:
                return element.Clone();
            case JsonDocument document:
                return document.RootElement.Clone();
            case IReadOnlyDictionary<string, object?> nested:
                return new ReadOnlyDictionary<string, object?>(nested.ToDictionary(
                    pair => pair.Key,
                    pair => CloneValue(pair.Value),
                    StringComparer.Ordinal));
            case IDictionary dictionary:
            {
                var copy = new Dictionary<object, object?>();
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is null)
                        throw new ArgumentException("Snapshot dictionaries cannot contain a null key.");
                    copy[entry.Key] = CloneValue(entry.Value);
                }

                return new ReadOnlyDictionary<object, object?>(copy);
            }
            case IEnumerable sequence when value is not string:
                return Array.AsReadOnly(sequence.Cast<object?>().Select(CloneValue).ToArray());
            default:
                if (value.GetType().IsValueType || value is string)
                    return value;
                throw new ArgumentException(
                    $"Cannot snapshot mutable value of type '{value.GetType().FullName}'.");
        }
    }
}

/// <summary>A defensive snapshot of a declared key.</summary>
public sealed class StorageKey
{
    public StorageKey(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = StorageValues.Snapshot(values);
    }

    public IReadOnlyDictionary<string, object?> Values { get; }
}

/// <summary>The explicit precondition attached to one row mutation.</summary>
public sealed record WritePrecondition
{
    private WritePrecondition(WritePreconditionKind kind, long? version)
    {
        Kind = kind;
        Version = version;
    }

    public static WritePrecondition Unconditional { get; } = new(WritePreconditionKind.Unconditional, null);

    public static WritePrecondition CreateOnly { get; } = new(WritePreconditionKind.CreateOnly, null);

    public static WritePrecondition IfVersion(long version)
    {
        if (version < 0)
            throw new ArgumentOutOfRangeException(nameof(version), "A version precondition cannot be negative.");
        return new(WritePreconditionKind.IfVersion, version);
    }

    public WritePreconditionKind Kind { get; }

    public long? Version { get; }

}

public enum WritePreconditionKind
{
    Unconditional,
    CreateOnly,
    IfVersion
}

public enum WriteOperation
{
    Insert,
    Update,
    Upsert,
    Delete,
    ConditionalUpsert
}

/// <summary>Validates operation/precondition combinations before provider I/O.</summary>
public static class WritePreconditionValidator
{
    public static void ValidateSystemOwnedValues(StorageUnit unit, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(values);
        if (unit.Concurrency.IsOptimistic &&
            unit.Concurrency.TokenColumn is { } token && values.ContainsKey(token))
        {
            throw new InvalidOperationException(
                $"GW-WRITE-CONCURRENCY-003: optimistic token column '{token}' is system-owned and cannot be supplied or mutated by application values.");
        }
    }

    public static void Validate(StorageUnit unit, WriteOperation operation, WriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var precondition = options?.Precondition ?? WritePrecondition.Unconditional;
        var allowed = operation switch
        {
            WriteOperation.Insert => precondition.Kind is WritePreconditionKind.Unconditional or WritePreconditionKind.CreateOnly,
            WriteOperation.Update => precondition.Kind is WritePreconditionKind.Unconditional or WritePreconditionKind.IfVersion,
            WriteOperation.Delete => precondition.Kind is WritePreconditionKind.Unconditional or WritePreconditionKind.IfVersion,
            WriteOperation.Upsert or WriteOperation.ConditionalUpsert => precondition.Kind is WritePreconditionKind.Unconditional or WritePreconditionKind.CreateOnly or WritePreconditionKind.IfVersion,
            _ => false
        };
        if (!allowed)
        {
            throw new InvalidOperationException(
                $"GW-WRITE-CONCURRENCY-002: precondition '{precondition.Kind}' is not valid for {operation}.");
        }

        if (unit.Concurrency.IsNone && precondition.Kind != WritePreconditionKind.Unconditional)
        {
            throw new InvalidOperationException(
                $"GW-WRITE-CONCURRENCY-001: storage unit '{unit.Name}' declares no concurrency token; " +
                $"precondition '{precondition.Kind}' is not allowed.");
        }
    }
}

/// <summary>Explicit optimistic-concurrency precondition for a mutation.</summary>
public sealed record WriteOptions
{
    private WritePrecondition precondition = WritePrecondition.Unconditional;

    public WritePrecondition Precondition
    {
        get => precondition;
        init => precondition = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Optional observer used by write-path proofs to count provider commands.</summary>
    public IWritePathObserver? Observer { get; init; }

    public static WriteOptions Unconditional { get; } = new();

    public static WriteOptions CreateOnly { get; } = new() { Precondition = WritePrecondition.CreateOnly };

    public static WriteOptions IfVersion(long expectedVersion) =>
        new() { Precondition = WritePrecondition.IfVersion(expectedVersion) };
}

public enum WriteOutcomeStatus
{
    Inserted,
    Updated,
    Upserted,
    Deleted,
    /// <summary>The operation nonce was accepted previously within its replay window.</summary>
    Replayed,
    NotFound,
    UniqueViolation,
    ConcurrencyConflict,
    /// <summary>The staged input was superseded by a later write to the same key.</summary>
    Superseded
}

/// <summary>
/// Result of a storage write. <see cref="Status"/> is returned immediately; for a
/// conservative conditional-upsert conflict, <see cref="Detail"/> performs at most
/// one cached disambiguating read.
/// </summary>
public sealed record WriteOutcome
{
    private readonly Lazy<WriteOutcomeDetail> detail;

    public WriteOutcome(
        WriteOutcomeStatus status,
        long? version = null,
        string? uniqueIndexName = null,
        IReadOnlyDictionary<string, object?>? generatedValues = null)
    {
        Status = status;
        Version = version;
        GeneratedValues = SnapshotGeneratedValues(generatedValues);
        detail = new(() => new WriteOutcomeDetail(status, version, uniqueIndexName, GeneratedValues: GeneratedValues));
    }

    private WriteOutcome(
        WriteOutcomeStatus status,
        long? version,
        Func<WriteOutcomeDetail> resolveDetail)
    {
        Status = status;
        Version = version;
        GeneratedValues = ImmutableGeneratedValues.Empty;
        detail = new(resolveDetail ?? throw new ArgumentNullException(nameof(resolveDetail)));
    }

    /// <summary>
    /// Creates an outcome whose immediate status is conservative. The optional disambiguating
    /// probe is run once, only when <see cref="Detail"/> is inspected.
    /// </summary>
    public static WriteOutcome Deferred(
        WriteOutcomeStatus provisionalStatus,
        long? version,
        Func<WriteOutcomeDetail> resolveDetail) =>
        new(provisionalStatus, version, resolveDetail);

    /// <summary>Immediate/provisional status of the provider-native write.</summary>
    public WriteOutcomeStatus Status { get; }

    public long? Version { get; }

    /// <summary>Provider-assigned values returned by a successful write, keyed by column name.</summary>
    public IReadOnlyDictionary<string, object?> GeneratedValues { get; }

    public T GeneratedValue<T>(string column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        if (!GeneratedValues.TryGetValue(column, out var value))
            throw new KeyNotFoundException($"Generated column '{column}' was not returned by this write.");
        return value is T typed
            ? typed
            : throw new InvalidCastException($"Generated column '{column}' returned '{value?.GetType().Name ?? "null"}', not '{typeof(T).Name}'.");
    }

    /// <summary>
    /// Resolves failure detail lazily and caches the result. Successful outcomes already
    /// have complete detail and do not issue a read.
    /// </summary>
    public WriteOutcomeDetail Detail => detail.Value;

    public string? UniqueIndexName => Detail.UniqueIndexName;

    public bool Succeeded => Status is WriteOutcomeStatus.Inserted or
        WriteOutcomeStatus.Updated or
        WriteOutcomeStatus.Upserted or
        WriteOutcomeStatus.Deleted;

    public bool Replayed => Status == WriteOutcomeStatus.Replayed;

    private static IReadOnlyDictionary<string, object?> SnapshotGeneratedValues(
        IReadOnlyDictionary<string, object?>? values) =>
        values is null || values.Count == 0
            ? ImmutableGeneratedValues.Empty
            : new ReadOnlyDictionary<string, object?>(values.ToDictionary(
                pair => pair.Key,
                pair => StorageValues.CloneValue(pair.Value),
                StringComparer.Ordinal));
}

/// <summary>Resolved write result detail, including lazy failure disambiguation.</summary>
public sealed record WriteOutcomeDetail(
    WriteOutcomeStatus Status,
    long? Version = null,
    string? UniqueIndexName = null,
    string? Message = null,
    IReadOnlyDictionary<string, object?>? GeneratedValues = null);

internal static class ImmutableGeneratedValues
{
    internal static IReadOnlyDictionary<string, object?> Empty { get; } =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal));
}

/// <summary>Thread-safe command observer used by provider-neutral write-path proofs.</summary>
public sealed class WritePathObserver : IWritePathObserver
{
    private readonly object gate = new();
    private readonly List<WritePathEvent> commands = [];

    public int RoundTrips
    {
        get
        {
            lock (gate) return commands.Count;
        }
    }

    public IReadOnlyList<WritePathEvent> Commands
    {
        get
        {
            lock (gate) return Array.AsReadOnly(commands.ToArray());
        }
    }

    public void Observe(WritePathEvent command)
    {
        if (string.IsNullOrWhiteSpace(command.Operation))
            throw new ArgumentException("An observed operation must have a name.", nameof(command));
        lock (gate) commands.Add(command);
    }
}

public sealed class StoredEntry
{
    public StoredEntry(StorageValues values, long? version)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = new StorageValues(values.Values);
        Version = version;
    }

    public StorageValues Values { get; }

    /// <summary>Null is intentional when the declared unit has no version machinery.</summary>
    public long? Version { get; }
}

public sealed record ProviderIndexColumn(string Column, SortDirection Direction);

/// <summary>Information read from a provider's native catalog.</summary>
public sealed class ProviderIndex
{
    public ProviderIndex(
        string name,
        IReadOnlyList<ProviderIndexColumn> columns,
        bool isUnique,
        MissingValueBehavior missingValues,
        int schemaVersion = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        Name = name;
        Columns = Array.AsReadOnly(columns.ToArray());
        IsUnique = isUnique;
        MissingValues = missingValues;
        SchemaVersion = schemaVersion;
    }

    public string Name { get; }

    public IReadOnlyList<ProviderIndexColumn> Columns { get; }

    public bool IsUnique { get; }

    public MissingValueBehavior MissingValues { get; }

    public int SchemaVersion { get; }
}

public enum SchemaChangeKind
{
    CreateStorageUnit,
    AddColumn,
    CreateIndex,
    AddDerivedColumn,
    RebuildIndex,
    UpdateAggregationProfile
}

public sealed record SchemaChange(SchemaChangeKind Kind, string Identity);

public sealed class SchemaDiff
{
    public SchemaDiff(IReadOnlyList<SchemaChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        Changes = Array.AsReadOnly(changes.ToArray());
    }

    public IReadOnlyList<SchemaChange> Changes { get; }

    public bool IsEmpty => Changes.Count == 0;
}

public sealed record SchemaApplyResult(SchemaDiff Diff, bool Applied)
{
    public bool IsNoOp => Diff.IsEmpty;
}

public interface IProviderCatalog
{
    IReadOnlyList<ProviderIndex> ReadIndexes(StorageUnitId storageUnitId);
}

public interface ISchemaCoordinator
{
    SchemaDiff Diff(StorageUnit desired);

    SchemaApplyResult Apply(StorageUnit desired);
}

public interface IStorageSession
{
    StorageUnit Unit { get; }

    StorageAccess Access { get; }

    StoredEntry? Read(StorageKey key);

    QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null);

    /// <summary>Executes one named, closed aggregation profile with its declared budgets.</summary>
    AggregationResult Aggregate(AggregationQuery query);

    /// <summary>
    /// Inserts a row. A ProviderSequence key must be omitted and is returned through
    /// <see cref="WriteOutcome.GeneratedValues"/>.
    /// </summary>
    WriteOutcome Insert(StorageValues values, WriteOptions? options = null);

    /// <summary>A ProviderSequence key is accepted only as the immutable row locator.</summary>
    WriteOutcome Update(StorageValues values, WriteOptions? options = null);

    /// <summary>
    /// With ProviderSequence, an omitted key inserts a generated row. A supplied key is
    /// an immutable locator: it updates an existing row or returns NotFound, never inserts it.
    /// </summary>
    WriteOutcome Upsert(StorageValues values, WriteOptions? options = null);

    WriteOutcome Delete(StorageKey key, WriteOptions? options = null);

    /// <summary>
    /// Appends a batch under one caller-supplied operation identity. The provider commits the
    /// durable operation ledger entry and all payload rows atomically.
    /// </summary>
    WriteOutcome Append(OperationId operationId, IReadOnlyList<StorageValues> values);

    /// <summary>Convenience overload for a one-row append.</summary>
    WriteOutcome Append(OperationId operationId, StorageValues value) =>
        Append(operationId, new[] { value });

    /// <summary>Convenience overload for a caller-supplied append batch.</summary>
    WriteOutcome Append(OperationId operationId, params StorageValues[] values) =>
        Append(operationId, (IReadOnlyList<StorageValues>)values);

    /// <summary>Alias emphasizing that the operation carries a batch payload.</summary>
    WriteOutcome AppendBatch(OperationId operationId, IReadOnlyList<StorageValues> values) =>
        Append(operationId, values);
}

public interface IUnitOfWork : IDisposable
{
    IStorageSession OpenSession(StorageUnit unit);

    /// <summary>Stages a row write for the next provider batch.</summary>
    void Stage(RowWrite write);

    /// <summary>Commits staged writes and returns aggregate success counts.</summary>
    BatchWriteSummary Commit();

    /// <summary>Commits an exact-mode unit and returns one outcome for every staged write.</summary>
    BatchWriteReport CommitWithOutcomes();

    /// <summary>Asynchronously commits an exact-mode unit and returns per-row outcomes.</summary>
    ValueTask<BatchWriteReport> CommitWithOutcomesAsync(CancellationToken cancellationToken = default);

    /// <summary>Asynchronously commits staged writes and returns aggregate evidence.</summary>
    ValueTask<BatchWriteSummary> CommitAsync(CancellationToken cancellationToken = default);

    void Rollback();
}

public interface IStorageProviderConnection : IDisposable
{
    IProviderCatalog Catalog { get; }

    ISchemaCoordinator Schema { get; }

    /// <summary>Provider capabilities relevant to staged writes and their outcome contract.</summary>
    IReadOnlyList<CapabilityDescriptor> Capabilities { get; }

    IStorageSession OpenSession(StorageUnit unit, StorageAccess access);

    IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units);

    IUnitOfWork BeginUnitOfWork(
        StorageAccess access,
        BatchWriteOptions options,
        params StorageUnit[] units);
}

/// <summary>
/// The sole provider discovery seam. A provider author supplies a factory whose connection
/// implements the provider-neutral connection contract.
/// </summary>
public interface IStorageProviderFactory
{
    IStorageProviderConnection Create(string connectionString);
}

public sealed record ConformanceCheck(string Name, bool Passed, string? Failure = null);

public sealed class ConformanceReport
{
    public ConformanceReport(IReadOnlyList<ConformanceCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        Checks = Array.AsReadOnly(checks.ToArray());
    }

    public IReadOnlyList<ConformanceCheck> Checks { get; }

    public bool Passed => Checks.All(check => check.Passed);

    public IReadOnlyList<ConformanceCheck> Failures =>
        Array.AsReadOnly(Checks.Where(check => !check.Passed).ToArray());
}

public sealed class ConformanceFailureException : Exception
{
    public ConformanceFailureException(string checkName, string message)
        : base($"Conformance check '{checkName}' failed: {message}")
    {
        CheckName = checkName;
    }

    public string CheckName { get; }
}
