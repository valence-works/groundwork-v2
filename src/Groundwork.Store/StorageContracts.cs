using System.Runtime.ExceptionServices;
using System.Collections.ObjectModel;
using System.Collections;
using System.Text.Json.Nodes;
using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Query.Model;

namespace Groundwork.Store;

/// <summary>Explicit access context for a storage session.</summary>
public sealed record StorageAccess
{
    private StorageAccess(
        StorageAccessKind kind,
        ScopePolicy policy,
        StorageScope? scope,
        StorageAccessAudit? audit)
    {
        Kind = kind;
        Policy = policy;
        Scope = scope;
        Audit = audit;
    }

    public static StorageAccess Global { get; } =
        new(StorageAccessKind.Global, ScopePolicy.Global, null, null);

    public StorageAccessKind Kind { get; }

    public ScopePolicy Policy { get; }

    public StorageScope? Scope { get; }

    public StorageAccessAudit? Audit { get; }

    public bool IsPrivilegedAcrossScopes => Kind == StorageAccessKind.PrivilegedAcrossScopes;

    public static StorageAccess Scoped(StorageScope scope) =>
        new(StorageAccessKind.Scoped, ScopePolicy.Scoped,
            scope ?? throw new ArgumentNullException(nameof(scope)), null);

    /// <summary>
    /// Opens an audited, query-only view across every scope of one scoped unit. Point operations
    /// remain ambiguous and are refused; open an ordinary scoped session for those operations.
    /// </summary>
    public static StorageAccess PrivilegedAcrossScopes(StorageAccessAudit audit) =>
        new(StorageAccessKind.PrivilegedAcrossScopes, ScopePolicy.Scoped, null,
            audit ?? throw new ArgumentNullException(nameof(audit)));
}

public enum StorageAccessKind
{
    Global,
    Scoped,
    PrivilegedAcrossScopes
}

/// <summary>Required operator identity and purpose attached to privileged cross-scope access.</summary>
public sealed record StorageAccessAudit
{
    public const int MaxIdentityLength = 128;
    public const int MaxPurposeLength = 256;

    public StorageAccessAudit(
        string identity,
        string purpose,
        IStorageAccessObserver? observer = null)
    {
        Identity = Validate(identity, MaxIdentityLength, nameof(identity), "audit identity");
        Purpose = Validate(purpose, MaxPurposeLength, nameof(purpose), "access purpose");
        Observer = observer;
    }

    public string Identity { get; }

    public string Purpose { get; }

    public IStorageAccessObserver? Observer { get; }

    private static string Validate(string value, int maxLength, string parameter, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameter);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
            throw new ArgumentException($"The {description} cannot have leading or trailing whitespace.", parameter);
        if (value.Length > maxLength)
            throw new ArgumentException($"The {description} cannot exceed {maxLength} UTF-16 code units.", parameter);
        if (value.IndexOf('\0') >= 0)
            throw new ArgumentException($"The {description} cannot contain NUL characters.", parameter);
        ValidateWellFormedUnicode(value, parameter, description);
        return value;
    }

    private static void ValidateWellFormedUnicode(string value, string parameter, string description)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                    throw new ArgumentException($"The {description} must contain well-formed UTF-16.", parameter);
                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                throw new ArgumentException($"The {description} must contain well-formed UTF-16.", parameter);
            }
        }
    }
}

/// <summary>One auditable use of privileged storage access.</summary>
public sealed record StorageAccessEvent(
    StorageUnitId Unit,
    string Operation,
    string Identity,
    string Purpose);

/// <summary>Receives privileged-access evidence before provider work begins.</summary>
public interface IStorageAccessObserver
{
    void Observe(StorageAccessEvent accessEvent);
}

/// <summary>Shared fail-closed checks for operations performed through an access-bound session.</summary>
public static class StorageAccessValidation
{
    public static void EnsureOrdinaryQuery(StorageAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (access.IsPrivilegedAcrossScopes)
        {
            throw new InvalidOperationException(
                "GW-ACCESS-004: privileged cross-scope sessions must use QueryAcrossScopes so every row retains its scope.");
        }
    }

    public static void EnsurePointOperation(StorageAccess access, string operation)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (access.IsPrivilegedAcrossScopes)
        {
            throw new InvalidOperationException(
                $"GW-ACCESS-003: privileged cross-scope access is query-only; '{operation}' requires an ordinary session with an explicit scope.");
        }
    }

    public static void EnsureUnitOfWork(StorageAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (access.IsPrivilegedAcrossScopes)
        {
            throw new InvalidOperationException(
                "GW-ACCESS-003: privileged cross-scope access is query-only and cannot begin a unit of work.");
        }
    }

    public static void ObservePrivilegedQuery(StorageAccess access, StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(unit);
        var audit = access.Audit ?? throw new InvalidOperationException(
            "GW-ACCESS-001: cross-scope queries require audit metadata.");
        audit.Observer?.Observe(new StorageAccessEvent(
            unit.Id,
            "query-across-scopes",
            audit.Identity,
            audit.Purpose));
    }
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
    ConditionalUpsert,
    CompareAndDelete
}

/// <summary>Validates operation/precondition combinations before provider I/O.</summary>
public static class WritePreconditionValidator
{
    /// <summary>
    /// Applies the declaration-level rules a written values dictionary must satisfy before any
    /// provider sees it: system-owned columns stay system-owned, and a value must fall inside the
    /// storable domain of its declared portable type. Every provider funnels its write paths
    /// through here, so the rules hold identically on all of them.
    /// </summary>
    public static void ValidateWrittenValues(StorageUnit unit, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(values);
        if (unit.Concurrency.IsOptimistic &&
            unit.Concurrency.TokenColumn is { } token && values.ContainsKey(token))
        {
            throw new InvalidOperationException(
                $"GW-WRITE-CONCURRENCY-003: optimistic token column '{token}' is system-owned and cannot be supplied or mutated by application values.");
        }

        // Indexed rather than LINQ: this runs on every write of every unit, and the overwhelmingly
        // common case is a declaration with no Double column at all.
        for (var index = 0; index < unit.Columns.Count; index++)
        {
            var column = unit.Columns[index];
            if (column.Type != PortableType.Double)
                continue;
            if (values.TryGetValue(column.Name, out var value) && value is double number &&
                !PortableDouble.IsStorable(number))
                throw new ArgumentException(PortableDouble.RefusalMessage(column.Name, number), nameof(values));
        }
    }

    /// <summary>
    /// Refuses a write that would open a second provider transaction on a session already inside
    /// one. A provider connection carries one transaction at a time, so a nested write can neither
    /// join nor isolate itself from the transaction it is nested in.
    /// </summary>
    public static void EnsureNoNestedTransaction(object? activeTransaction)
    {
        if (activeTransaction is not null)
        {
            throw new InvalidOperationException(
                "GW-WRITE-NESTED-001: this storage session is already inside a provider write " +
                "transaction; open a unit of work and stage the writes instead of nesting them.");
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
            WriteOperation.CompareAndDelete => precondition.Kind is WritePreconditionKind.Unconditional or WritePreconditionKind.IfVersion,
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

/// <summary>
/// Runs a cleanup step while a write failure is already in flight. A rollback or a lifecycle close
/// can itself fail, and the failure the caller has to act on is the original one, so a cleanup
/// exception is recorded against the original under <see cref="CleanupFailureKey"/> rather than
/// thrown in its place.
/// </summary>
public static class WriteFailureCleanup
{
    /// <summary>Key under which a cleanup exception is attached to the original failure's data.</summary>
    public const string CleanupFailureKey = "Groundwork.CleanupFailure";

    public static void Run(Exception failure, Action cleanup)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(cleanup);
        try
        {
            cleanup();
        }
        catch (Exception cleanupFailure)
        {
            Record(failure, cleanupFailure);
        }
    }

    public static async ValueTask Run(Exception failure, Func<ValueTask> cleanup)
    {
        ArgumentNullException.ThrowIfNull(failure);
        ArgumentNullException.ThrowIfNull(cleanup);
        try
        {
            await cleanup().ConfigureAwait(false);
        }
        catch (Exception cleanupFailure)
        {
            Record(failure, cleanupFailure);
        }
    }

    /// <summary>
    /// Runs every step in order, and runs the later ones even when an earlier one throws. Releasing
    /// a connection is not optional because disposing its transaction failed: a connection abandoned
    /// mid-transaction goes back to the driver's pool carrying that state, and the next caller to
    /// open it meets a refusal that has nothing to do with what it asked for.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Run(Exception, Action)"/> there is no original failure to defer to, so the
    /// first step failure is thrown once every step has run — a disposal that fails on an otherwise
    /// successful commit is a signal, not noise. Later failures are recorded against it under
    /// <see cref="CleanupFailureKey"/>. Composes with <c>Run</c>: called inside a cleanup that is
    /// already handling a write failure, the exception this throws is recorded rather than raised.
    /// </remarks>
    public static void RunAll(params Action[] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        Exception? first = null;
        List<Exception>? rest = null;
        foreach (var step in steps)
        {
            ArgumentNullException.ThrowIfNull(step);
            try
            {
                step();
            }
            catch (Exception stepFailure)
            {
                if (first is null)
                    first = stepFailure;
                else
                    (rest ??= []).Add(stepFailure);
            }
        }
        if (first is null)
            return;
        if (rest is not null)
            Record(first, rest);
        // Rethrow rather than `throw first`, which would reset the stack trace to this line and
        // lose where the disposal actually failed.
        ExceptionDispatchInfo.Capture(first).Throw();
    }

    private static void Record(Exception failure, Exception cleanupFailure) =>
        Record(failure, [cleanupFailure]);

    private static void Record(Exception failure, IReadOnlyList<Exception> cleanupFailures)
    {
        try
        {
            // One key, however many steps failed: a reader looking for what went wrong during
            // cleanup should find all of it, not whichever failure happened to be recorded last.
            failure.Data[CleanupFailureKey] = string.Join(
                Environment.NewLine + "--- and then ---" + Environment.NewLine,
                cleanupFailures.Select(cleanupFailure => cleanupFailure.ToString()));
        }
        catch (Exception attachFailure) when (attachFailure is ArgumentException or NotSupportedException)
        {
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
    /// <summary>The row exists, but one or more declared compare-and-delete values differ.</summary>
    ComparisonMismatch,
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

/// <summary>Thread-safe command observer used by provider-neutral round-trip proofs.</summary>
public sealed class ProviderCommandObserver : IProviderCommandObserver
{
    private readonly object gate = new();
    private readonly List<ProviderCommandEvent> commands = [];

    /// <summary>Every provider command this observer has seen, reads and writes alike.</summary>
    public int RoundTrips
    {
        get
        {
            lock (gate) return commands.Count;
        }
    }

    public IReadOnlyList<ProviderCommandEvent> Commands
    {
        get
        {
            lock (gate) return Array.AsReadOnly(commands.ToArray());
        }
    }

    /// <summary>The commands of one kind, for proofs that assert a write path's shape.</summary>
    public IReadOnlyList<ProviderCommandEvent> OfKind(ProviderCommandKind kind)
    {
        lock (gate) return Array.AsReadOnly(commands.Where(command => command.Kind == kind).ToArray());
    }

    public void Observe(ProviderCommandEvent command)
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
    UpdateAggregationProfile,
    RenameStorageUnit,
    RenameColumn,
    AlterColumn,
    DropColumn,
    DropIndex,
    DropStorageUnit,
    CreateForeignKey,
    CreateCheckConstraint
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
    /// <summary>
    /// Inspects whether the deployed physical schema can serve <paramref name="desired"/>, using the
    /// same kernel admission rule provider sessions enforce. Safe startup application is opt-in.
    /// </summary>
    GroundworkRuntimeSchemaAdmissionResult InspectRuntimeAdmission(
        StorageUnit desired,
        GroundworkRuntimeSchemaAdmissionOptions? options = null);

    SchemaDiff Diff(StorageUnit desired);

    SchemaApplyResult Apply(StorageUnit desired);
}

/// <summary>
/// Non-owning view over one declared storage unit. This interface is intentionally not disposable:
/// a session opened from a provider connection is valid while that connection is alive, and a
/// session opened from a unit of work is valid until that unit reaches a terminal state or is
/// disposed.
/// </summary>
/// <remarks>
/// Every operation is declared twice: a synchronous member and an asynchronous counterpart that
/// takes a <see cref="CancellationToken"/>. Both surfaces are supported, and a provider implements
/// one session that serves both — the asynchronous member is the operation, and the synchronous
/// member is the same operation executed on the calling thread. Server-side hosts should prefer the
/// asynchronous surface; the synchronous surface remains because deleting it would not remove the
/// blocking call, only move it into consumer code where this library can no longer keep it off a
/// request thread. Whether a given provider actually yields its thread is a property of the driver
/// underneath it and is stated in that provider's documentation.
/// </remarks>
public interface IStorageSession
{
    StorageUnit Unit { get; }

    StorageAccess Access { get; }

    StoredEntry? Read(StorageKey key);

    ValueTask<StoredEntry?> ReadAsync(StorageKey key, CancellationToken cancellationToken = default);

    QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null);

    ValueTask<QueryMaterializedResult> QueryAsync(
        QueryRequest request,
        QueryRenderOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>Executes one declared profile or an explicitly accepted ad-hoc closed-vocabulary profile.</summary>
    AggregationResult Aggregate(AggregationQuery query);

    ValueTask<AggregationResult> AggregateAsync(
        AggregationQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a row. A ProviderSequence key must be omitted and is returned through
    /// <see cref="WriteOutcome.GeneratedValues"/>.
    /// </summary>
    WriteOutcome Insert(StorageValues values, WriteOptions? options = null);

    ValueTask<WriteOutcome> InsertAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>A ProviderSequence key is accepted only as the immutable row locator.</summary>
    WriteOutcome Update(StorageValues values, WriteOptions? options = null);

    ValueTask<WriteOutcome> UpdateAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// With ProviderSequence, an omitted key inserts a generated row. A supplied key is
    /// an immutable locator: it updates an existing row or returns NotFound, never inserts it.
    /// </summary>
    WriteOutcome Upsert(StorageValues values, WriteOptions? options = null);

    ValueTask<WriteOutcome> UpsertAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default);

    WriteOutcome Delete(StorageKey key, WriteOptions? options = null);

    ValueTask<WriteOutcome> DeleteAsync(
        StorageKey key,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a batch under one caller-supplied operation identity. The provider commits the
    /// durable operation ledger entry and all payload rows atomically.
    /// </summary>
    WriteOutcome Append(OperationId operationId, IReadOnlyList<StorageValues> values);

    ValueTask<WriteOutcome> AppendAsync(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        CancellationToken cancellationToken = default);

    /// <summary>Convenience overload for a one-row append.</summary>
    WriteOutcome Append(OperationId operationId, StorageValues value) =>
        Append(operationId, new[] { value });

    /// <summary>Convenience overload for a one-row append.</summary>
    ValueTask<WriteOutcome> AppendAsync(
        OperationId operationId,
        StorageValues value,
        CancellationToken cancellationToken = default) =>
        AppendAsync(operationId, new[] { value }, cancellationToken);

    /// <summary>Convenience overload for a caller-supplied append batch.</summary>
    WriteOutcome Append(OperationId operationId, params StorageValues[] values) =>
        Append(operationId, (IReadOnlyList<StorageValues>)values);

    /// <summary>Alias emphasizing that the operation carries a batch payload.</summary>
    WriteOutcome AppendBatch(OperationId operationId, IReadOnlyList<StorageValues> values) =>
        Append(operationId, values);

    /// <summary>Alias emphasizing that the operation carries a batch payload.</summary>
    ValueTask<WriteOutcome> AppendBatchAsync(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        CancellationToken cancellationToken = default) =>
        AppendAsync(operationId, values, cancellationToken);
}

/// <summary>
/// Internal bridge for execution adapters that need the connection's deployed-catalog evidence.
/// Provider sessions remain public, connection-free views; the bridge is deliberately internal so
/// the runtime coverage contract cannot become another consumer-facing capability to implement.
/// </summary>
internal interface IProviderBoundStorageSession
{
    IStorageProviderConnection? ProviderConnection { get; }
}

/// <summary>Durable lifecycle evidence for the current storage unit and access scope.</summary>
public sealed record StorageInspection(long? LifetimeCommittedSequenceHighWater);

/// <summary>Optional provider capability for durable scoped ProviderSequence inspection.</summary>
public interface IStorageInspectionSession
{
    StorageInspection Inspect();

    ValueTask<StorageInspection> InspectAsync(CancellationToken cancellationToken = default);
}

/// <summary>Public durable inspection entry point that refuses before provider work when unsupported.</summary>
public static class StorageInspectionSessionExtensions
{
    public static StorageInspection Inspect(this IStorageSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        StorageAccessValidation.EnsurePointOperation(session.Access, "inspect");
        if (session is not IStorageInspectionSession inspection)
        {
            throw new NotSupportedException(
                "GW-INSPECT-001: this provider session does not advertise durable sequence inspection; " +
                "inspect IStorageInspectionSession before using Inspect.");
        }

        EnsureProviderSequence(session.Unit);
        return inspection.Inspect();
    }

    public static ValueTask<StorageInspection> InspectAsync(
        this IStorageSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        StorageAccessValidation.EnsurePointOperation(session.Access, "inspect");
        if (session is not IStorageInspectionSession inspection)
        {
            throw new NotSupportedException(
                "GW-INSPECT-001: this provider session does not advertise durable sequence inspection; " +
                "inspect IStorageInspectionSession before using Inspect.");
        }

        EnsureProviderSequence(session.Unit);
        return inspection.InspectAsync(cancellationToken);
    }

    /// <summary>Refuses inspection declarations that cannot produce a sequence high-water.</summary>
    public static void EnsureProviderSequence(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!unit.Columns.Any(column => column.Generation == ColumnGeneration.ProviderSequence))
        {
            throw new NotSupportedException(
                "GW-INSPECT-002: durable sequence inspection requires a ProviderSequence column; " +
                "declare one before calling Inspect.");
        }
    }
}

/// <summary>Optional provider capability for replay-stable per-row append outcomes.</summary>
public interface IExactAppendStorageSession
{
    AppendOutcomeReport AppendWithOutcomes(OperationId operationId, IReadOnlyList<StorageValues> values);

    ValueTask<AppendOutcomeReport> AppendWithOutcomesAsync(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        CancellationToken cancellationToken = default);
}

/// <summary>Optional query-only capability advertised by a privileged cross-scope session.</summary>
public interface IPrivilegedCrossScopeQuerySession
{
    CrossScopeQueryResult QueryAcrossScopes(
        QueryRequest request,
        QueryRenderOptions? options = null);

    ValueTask<CrossScopeQueryResult> QueryAcrossScopesAsync(
        QueryRequest request,
        QueryRenderOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Public cross-scope query entry point with explicit capability and access checks.</summary>
public static class PrivilegedCrossScopeQuerySessionExtensions
{
    public static CrossScopeQueryResult QueryAcrossScopes(
        this IStorageSession session,
        QueryRequest request,
        QueryRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        if (!session.Access.IsPrivilegedAcrossScopes)
            throw new InvalidOperationException(
                "GW-ACCESS-001: cross-scope queries require explicit privileged across-scope access.");
        if (session is not IPrivilegedCrossScopeQuerySession privileged)
            throw new NotSupportedException(
                "GW-ACCESS-002: this provider session does not advertise privileged cross-scope queries.");
        return privileged.QueryAcrossScopes(request, options);
    }

    public static ValueTask<CrossScopeQueryResult> QueryAcrossScopesAsync(
        this IStorageSession session,
        QueryRequest request,
        QueryRenderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        if (!session.Access.IsPrivilegedAcrossScopes)
            throw new InvalidOperationException(
                "GW-ACCESS-001: cross-scope queries require explicit privileged across-scope access.");
        if (session is not IPrivilegedCrossScopeQuerySession privileged)
            throw new NotSupportedException(
                "GW-ACCESS-002: this provider session does not advertise privileged cross-scope queries.");
        return privileged.QueryAcrossScopesAsync(request, options, cancellationToken);
    }
}

/// <summary>Public exact-append entry points that fail clearly when a provider lacks the capability.</summary>
public static class ExactAppendSessionExtensions
{
    public static AppendOutcomeReport AppendWithOutcomes(
        this IStorageSession session,
        OperationId operationId,
        IReadOnlyList<StorageValues> values)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(values);
        StorageAccessValidation.EnsurePointOperation(session.Access, "append");
        if (session is not IExactAppendStorageSession exact)
        {
            throw new NotSupportedException(
                "GW-APPEND-003: this provider session does not advertise exact append outcomes; " +
                "inspect IExactAppendStorageSession before using AppendWithOutcomes.");
        }

        return exact.AppendWithOutcomes(operationId, values);
    }

    public static ValueTask<AppendOutcomeReport> AppendWithOutcomesAsync(
        this IStorageSession session,
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(values);
        StorageAccessValidation.EnsurePointOperation(session.Access, "append");
        if (session is not IExactAppendStorageSession exact)
        {
            throw new NotSupportedException(
                "GW-APPEND-003: this provider session does not advertise exact append outcomes; " +
                "inspect IExactAppendStorageSession before using AppendWithOutcomes.");
        }

        return exact.AppendWithOutcomesAsync(operationId, values, cancellationToken);
    }

    public static AppendOutcomeReport AppendWithOutcomes(
        this IStorageSession session,
        OperationId operationId,
        StorageValues value) =>
        AppendWithOutcomes(session, operationId, new[] { value });

    public static AppendOutcomeReport AppendWithOutcomes(
        this IStorageSession session,
        OperationId operationId,
        params StorageValues[] values) =>
        AppendWithOutcomes(session, operationId, (IReadOnlyList<StorageValues>)values);
}

/// <summary>
/// Owns one staged transaction, the sessions it creates, and their provider resources. Commit and
/// rollback are terminal operations; disposing a non-terminal unit rolls it back and releases its
/// sessions. Sessions returned by <see cref="OpenSession"/> must not be used afterward.
/// </summary>
public interface IUnitOfWork : IDisposable
{
    /// <summary>Opens a session owned by this unit of work until it becomes terminal or is disposed.</summary>
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

/// <summary>
/// A storage session whose provider resources belong to the caller.
/// </summary>
/// <remarks>
/// Disposal releases the session's connection — returning it to the provider's pool — and must be
/// idempotent. Every operation after disposal throws, rather than silently using a released connection.
/// Sessions obtained from <see cref="IUnitOfWork.OpenSession"/> are owned by their unit of work and are not
/// of this kind: one owner per session, decided where it is opened.
/// </remarks>
public interface IOwnedStorageSession : IStorageSession, IDisposable, IAsyncDisposable
{
    /// <summary>Whether this caller-owned session has released its provider resources.</summary>
    bool IsReleased { get; }
}

/// <summary>
/// Owns the provider resources for a connection and the sessions opened directly from it. Dispose
/// the connection after all of its sessions are no longer needed; disposal invalidates those
/// non-owning session views.
/// </summary>
public interface IStorageProviderConnection : IDisposable
{
    IProviderCatalog Catalog { get; }

    ISchemaCoordinator Schema { get; }

    /// <summary>Provider capabilities relevant to staged writes and their outcome contract.</summary>
    IReadOnlyList<CapabilityDescriptor> Capabilities { get; }

    /// <summary>Opens a non-owning session view that remains valid while this connection is alive.</summary>
    /// <param name="observer">
    /// Optional sink for the provider commands this session issues. It counts every round trip the session
    /// performs — reads, writes, probes and retention — because the session is what issues them. Schema work
    /// is not included: it runs through <see cref="Schema"/> on the connection, not through a session.
    /// </param>
    IStorageSession OpenSession(StorageUnit unit, StorageAccess access, IProviderCommandObserver? observer = null);

    /// <summary>
    /// Opens a session whose provider resources belong to the caller and are released when it is disposed.
    /// </summary>
    /// <remarks>
    /// <see cref="OpenSession"/> returns a non-owning view over a connection this provider keeps until the
    /// provider itself is disposed, which leaves a consumer choosing between sharing one session across
    /// concurrent callers — PostgreSQL and SQL Server refuse concurrent commands on one connection — and
    /// opening per call, which leaks a connection every time. An owned session is the third option: its
    /// connection is not registered for provider-disposal and returns to the pool on release, so per-caller
    /// sessions neither leak nor serialize unrelated callers against each other.
    ///
    /// Use it when callers are concurrent and independent. Prefer <see cref="OpenSession"/> when the
    /// provider's own lifetime is the natural bound, which is the single-threaded case.
    /// </remarks>
    IOwnedStorageSession OpenOwnedSession(
        StorageUnit unit,
        StorageAccess access,
        IProviderCommandObserver? observer = null);

    /// <summary>Begins a unit of work that owns its transaction and staged sessions until terminal or disposed.</summary>
    IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units);

    /// <summary>Begins a unit of work with explicit batch outcome and flush behavior.</summary>
    IUnitOfWork BeginUnitOfWork(
        StorageAccess access,
        BatchWriteOptions options,
        params StorageUnit[] units);

    /// <summary>
    /// Begins a unit of work whose provider commands are counted by <paramref name="observer"/>.
    /// </summary>
    /// <remarks>
    /// A unit of work builds its own sessions rather than going through <see cref="OpenSession"/>, so it
    /// needs the observer handed to it directly. Without this overload the batched commit path — the one a
    /// checkpoint commit actually takes — would report zero provider round trips while looking healthy.
    /// </remarks>
    IUnitOfWork BeginUnitOfWork(
        StorageAccess access,
        BatchWriteOptions options,
        IProviderCommandObserver? observer,
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
