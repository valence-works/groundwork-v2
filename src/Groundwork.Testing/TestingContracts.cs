using System.Collections.ObjectModel;
using Groundwork.Kernel;

namespace Groundwork.Testing;

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

    internal static object? CloneValue(object? value) => value switch
    {
        byte[] bytes => bytes.ToArray(),
        IReadOnlyDictionary<string, object?> nested =>
            new ReadOnlyDictionary<string, object?>(nested.ToDictionary(
                pair => pair.Key,
                pair => CloneValue(pair.Value),
                StringComparer.Ordinal)),
        _ => value
    };
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

/// <summary>Optional optimistic-concurrency precondition for a mutation.</summary>
public sealed record WriteOptions
{
    public long? ExpectedVersion { get; init; }

    public static WriteOptions Unconditional { get; } = new();

    public static WriteOptions ForVersion(long expectedVersion) =>
        new() { ExpectedVersion = expectedVersion };
}

public enum WriteOutcomeStatus
{
    Inserted,
    Updated,
    Upserted,
    Deleted,
    NotFound,
    UniqueViolation,
    ConcurrencyConflict
}

public sealed record WriteOutcome(WriteOutcomeStatus Status, long? Version = null)
{
    public bool Succeeded => Status is WriteOutcomeStatus.Inserted or
        WriteOutcomeStatus.Updated or
        WriteOutcomeStatus.Upserted or
        WriteOutcomeStatus.Deleted;
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
        MissingValueBehavior missingValues)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(columns);
        Name = name;
        Columns = Array.AsReadOnly(columns.ToArray());
        IsUnique = isUnique;
        MissingValues = missingValues;
    }

    public string Name { get; }

    public IReadOnlyList<ProviderIndexColumn> Columns { get; }

    public bool IsUnique { get; }

    public MissingValueBehavior MissingValues { get; }
}

public enum SchemaChangeKind
{
    CreateStorageUnit,
    AddColumn,
    CreateIndex,
    AddDerivedColumn
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

    WriteOutcome Insert(StorageValues values, WriteOptions? options = null);

    WriteOutcome Update(StorageValues values, WriteOptions? options = null);

    WriteOutcome Upsert(StorageValues values, WriteOptions? options = null);

    WriteOutcome Delete(StorageKey key, WriteOptions? options = null);
}

public interface IUnitOfWork : IDisposable
{
    IStorageSession OpenSession(StorageUnit unit);

    void Commit();

    void Rollback();
}

public interface IStorageProviderConnection : IDisposable
{
    IProviderCatalog Catalog { get; }

    ISchemaCoordinator Schema { get; }

    IStorageSession OpenSession(StorageUnit unit, StorageAccess access);

    IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units);
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
