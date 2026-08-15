using System.Collections;
using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Query.Model;
using Groundwork.Testing;

namespace Groundwork.MongoDb;

/// <summary>Access context used by the provider-native MongoDB session contract.</summary>
public sealed record MongoStorageAccess
{
    private MongoStorageAccess(ScopePolicy policy, StorageScope? scope)
    {
        Policy = policy;
        Scope = scope;
    }

    public static MongoStorageAccess Global { get; } = new(ScopePolicy.Global, null);

    public ScopePolicy Policy { get; }

    public StorageScope? Scope { get; }

    public static MongoStorageAccess Scoped(StorageScope scope) =>
        new(ScopePolicy.Scoped, scope ?? throw new ArgumentNullException(nameof(scope)));
}

/// <summary>A defensive value snapshot for one MongoDB storage operation.</summary>
public sealed class MongoStorageValues
{
    public MongoStorageValues(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = Snapshot(values);
    }

    public IReadOnlyDictionary<string, object?> Values { get; }

    internal static IReadOnlyDictionary<string, object?> Snapshot(
        IReadOnlyDictionary<string, object?> values)
    {
        var copy = values.ToDictionary(
            pair => pair.Key,
            pair => CloneValue(pair.Value),
            StringComparer.Ordinal);
        return new ReadOnlyDictionary<string, object?>(copy);
    }

    internal static object? CloneValue(object? value) => value switch
    {
        null => null,
        byte[] bytes => bytes.ToArray(),
        JsonNode node => node.DeepClone(),
        JsonElement element => element.Clone(),
        JsonDocument document => document.RootElement.Clone(),
        IReadOnlyDictionary<string, object?> nested => new ReadOnlyDictionary<string, object?>(
            nested.ToDictionary(pair => pair.Key, pair => CloneValue(pair.Value), StringComparer.Ordinal)),
        IDictionary dictionary => CloneDictionary(dictionary),
        IEnumerable sequence when value is not string => Array.AsReadOnly(
            sequence.Cast<object?>().Select(CloneValue).ToArray()),
        _ when value.GetType().IsValueType || value is string => value,
        _ => throw new ArgumentException(
            $"Cannot snapshot mutable value of type '{value.GetType().FullName}'.")
    };

    private static IReadOnlyDictionary<object, object?> CloneDictionary(IDictionary source)
    {
        var copy = new Dictionary<object, object?>();
        foreach (DictionaryEntry entry in source)
        {
            if (entry.Key is null)
                throw new ArgumentException("Snapshot dictionaries cannot contain a null key.");
            copy[entry.Key] = CloneValue(entry.Value);
        }

        return new ReadOnlyDictionary<object, object?>(copy);
    }
}

/// <summary>A defensive key snapshot for one MongoDB operation.</summary>
public sealed class MongoStorageKey
{
    public MongoStorageKey(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = MongoStorageValues.Snapshot(values);
    }

    public IReadOnlyDictionary<string, object?> Values { get; }
}

public sealed record MongoWriteOptions
{
    private WritePrecondition precondition = WritePrecondition.Unconditional;

    public WritePrecondition Precondition
    {
        get => precondition;
        init => precondition = value ?? throw new ArgumentNullException(nameof(value));
    }

    public IWritePathObserver? Observer { get; init; }

    public static MongoWriteOptions Unconditional { get; } = new();

    public static MongoWriteOptions CreateOnly { get; } = new() { Precondition = WritePrecondition.CreateOnly };

    public static MongoWriteOptions IfVersion(long expectedVersion) =>
        new() { Precondition = WritePrecondition.IfVersion(expectedVersion) };
}

public enum MongoWriteOutcomeStatus
{
    Inserted,
    Updated,
    Upserted,
    Deleted,
    NotFound,
    UniqueViolation,
    ConcurrencyConflict
}

public sealed record MongoWriteOutcome
{
    public MongoWriteOutcome(
        MongoWriteOutcomeStatus status,
        long? version = null,
        string? uniqueIndexName = null,
        IReadOnlyDictionary<string, object?>? generatedValues = null)
    {
        Status = status;
        Version = version;
        UniqueIndexName = uniqueIndexName;
        GeneratedValues = new ReadOnlyDictionary<string, object?>(
            (generatedValues ?? new Dictionary<string, object?>())
                .ToDictionary(pair => pair.Key, pair => MongoStorageValues.CloneValue(pair.Value), StringComparer.Ordinal));
    }

    public MongoWriteOutcomeStatus Status { get; }

    public long? Version { get; }

    public string? UniqueIndexName { get; }

    public IReadOnlyDictionary<string, object?> GeneratedValues { get; } =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.Ordinal));

    public T GeneratedValue<T>(string column)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        if (!GeneratedValues.TryGetValue(column, out var value))
            throw new KeyNotFoundException($"Generated column '{column}' was not returned by this write.");
        return value is T typed
            ? typed
            : throw new InvalidCastException($"Generated column '{column}' returned '{value?.GetType().Name ?? "null"}', not '{typeof(T).Name}'.");
    }

    public bool Succeeded => Status is MongoWriteOutcomeStatus.Inserted or
        MongoWriteOutcomeStatus.Updated or
        MongoWriteOutcomeStatus.Upserted or
        MongoWriteOutcomeStatus.Deleted;
}

public sealed class MongoStoredEntry
{
    public MongoStoredEntry(MongoStorageValues values, long? version)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = new MongoStorageValues(values.Values);
        Version = version;
    }

    public MongoStorageValues Values { get; }

    public long? Version { get; }
}

public sealed record MongoProviderIndexColumn(string Column, SortDirection Direction);

public sealed class MongoProviderIndex
{
    public MongoProviderIndex(
        string name,
        IReadOnlyList<MongoProviderIndexColumn> columns,
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

    public IReadOnlyList<MongoProviderIndexColumn> Columns { get; }

    public bool IsUnique { get; }

    public MissingValueBehavior MissingValues { get; }

    public int SchemaVersion { get; }
}

public enum MongoSchemaChangeKind
{
    CreateStorageUnit,
    AddColumn,
    CreateIndex,
    AddDerivedColumn,
    RebuildIndex,
    UpdateAggregationProfile
}

public sealed record MongoSchemaChange(MongoSchemaChangeKind Kind, string Identity);

public sealed class MongoSchemaDiff
{
    public MongoSchemaDiff(IReadOnlyList<MongoSchemaChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        Changes = Array.AsReadOnly(changes.ToArray());
    }

    public IReadOnlyList<MongoSchemaChange> Changes { get; }

    public bool IsEmpty => Changes.Count == 0;
}

public sealed record MongoSchemaApplyResult(MongoSchemaDiff Diff, bool Applied)
{
    public bool IsNoOp => Diff.IsEmpty;
}

/// <summary>
/// Native MongoDB admission evidence. Column drift prevents a store from opening; index drift
/// remains observable so a query coverage gate can refuse only shapes that require that index.
/// Extra native indexes are intentionally absent from <see cref="IndexDrift"/>.
/// </summary>
public sealed class MongoSchemaAdmissionReport
{
    public MongoSchemaAdmissionReport(
        StorageUnitId subjectId,
        IEnumerable<SchemaRefusal>? columnDrift,
        IEnumerable<SchemaRefusal>? indexDrift)
    {
        SubjectId = subjectId;
        ColumnDrift = Array.AsReadOnly((columnDrift ?? []).ToArray());
        IndexDrift = Array.AsReadOnly((indexDrift ?? []).ToArray());
    }

    public StorageUnitId SubjectId { get; }

    public IReadOnlyList<SchemaRefusal> ColumnDrift { get; }

    public IReadOnlyList<SchemaRefusal> IndexDrift { get; }

    public IReadOnlyList<SchemaRefusal> Refusals =>
        Array.AsReadOnly(ColumnDrift.Concat(IndexDrift).ToArray());

    public bool IsProcessReady => ColumnDrift.Count == 0;
}

public interface IMongoProviderCatalog
{
    IReadOnlyList<MongoProviderIndex> ReadIndexes(StorageUnitId storageUnitId);
}

public interface IMongoSchemaCoordinator
{
    MongoSchemaDiff Diff(StorageUnit desired);

    MongoSchemaApplyResult Apply(StorageUnit desired);
}

public interface IMongoStorageSession
{
    StorageUnit Unit { get; }

    MongoStorageAccess Access { get; }

    MongoStoredEntry? Read(MongoStorageKey key);

    QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null);

    /// <summary>Executes one named, declared aggregation profile through the native provider.</summary>
    AggregationResult Aggregate(AggregationQuery query);

    /// <summary>
    /// Inserts a row. A ProviderSequence key must be omitted and is returned through
    /// <see cref="MongoWriteOutcome.GeneratedValues"/>.
    /// </summary>
    MongoWriteOutcome Insert(MongoStorageValues values, MongoWriteOptions? options = null);

    /// <summary>A ProviderSequence key is accepted only as the immutable row locator.</summary>
    MongoWriteOutcome Update(MongoStorageValues values, MongoWriteOptions? options = null);

    /// <summary>
    /// With ProviderSequence, an omitted key inserts a generated row. A supplied key is
    /// an immutable locator: it updates an existing row or returns NotFound, never inserts it.
    /// </summary>
    MongoWriteOutcome Upsert(MongoStorageValues values, MongoWriteOptions? options = null);

    /// <summary>
    /// Executes the provider-native conditional upsert as one MongoDB update command.
    /// A <see cref="ColumnGeneration.ProviderSequence"/> column is refused because
    /// allocating that value requires a separate sequence command and transaction.
    /// </summary>
    MongoWriteOutcome ConditionalUpsert(MongoStorageValues values, MongoWriteOptions? options = null);

    MongoWriteOutcome Delete(MongoStorageKey key, MongoWriteOptions? options = null);
}

public interface IMongoUnitOfWork : IDisposable
{
    IMongoStorageSession OpenSession(StorageUnit unit);

    void Commit();

    void Rollback();
}

public interface IMongoProviderConnection : IDisposable
{
    IMongoProviderCatalog Catalog { get; }

    IMongoSchemaCoordinator Schema { get; }

    /// <summary>
    /// Reports whether this deployment can provide transactional sequence allocation.
    /// Standalone MongoDB returns <see cref="ProviderFit.Unsupported"/> rather than
    /// advertising a capability that will fail later during schema application.
    /// </summary>
    ProviderFit ProviderSequenceFit { get; }

    /// <summary>Reads native admission evidence without applying or repairing schema.</summary>
    MongoSchemaAdmissionReport InspectSchema(StorageUnit unit, MongoStorageAccess access);

    IMongoStorageSession OpenSession(StorageUnit unit, MongoStorageAccess access);

    IMongoUnitOfWork BeginUnitOfWork(MongoStorageAccess access, params StorageUnit[] units);
}

public interface IMongoProviderFactory
{
    IMongoProviderConnection Create(string connectionString);
}

public sealed class MongoSchemaConflictException : InvalidOperationException
{
    public MongoSchemaConflictException(string message) : base(message)
    {
    }
}
