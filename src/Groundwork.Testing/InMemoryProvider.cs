using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Query.Model;

namespace Groundwork.Testing;

using Groundwork.Store;

/// <summary>A deterministic provider-neutral reference implementation for the testing package.</summary>
public sealed class InMemoryProviderFactory : IStorageProviderFactory
{
    private readonly object gate = new();
    private readonly Dictionary<string, InMemoryDatabase> databases = new(StringComparer.Ordinal);

    public IStorageProviderConnection Create(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        lock (gate)
        {
            if (!databases.TryGetValue(connectionString, out var database))
            {
                database = new InMemoryDatabase();
                databases.Add(connectionString, database);
            }

            return new InMemoryProviderConnection(database);
        }
    }
}

public sealed class SchemaConflictException : InvalidOperationException
{
    public SchemaConflictException(string message) : base(message)
    {
    }
}

public sealed class InMemoryProviderConnection : IStorageProviderConnection
{
    private readonly InMemoryDatabase database;
    private bool disposed;

    internal InMemoryProviderConnection(InMemoryDatabase database)
    {
        this.database = database;
        Catalog = new InMemoryProviderCatalog(database);
        Schema = new InMemorySchemaCoordinator(database);
    }

    public IProviderCatalog Catalog { get; }

    public ISchemaCoordinator Schema { get; }

    public IReadOnlyList<CapabilityDescriptor> Capabilities => BatchWriteCapabilities.ForProvider(
        "the in-memory provider", nativeBatch: false,
        exactOutcomeCost: "one provider operation per coalesced row",
        batchCost: "uses provider-neutral per-row operations inside the transaction",
        exactAppendOutcomes: true,
        durableHighWaterInspection: true,
        exactRetention: true,
        atomicCommit: true,
        compareAndDelete: true);

    public IStorageSession OpenSession(StorageUnit unit, StorageAccess access, IProviderCommandObserver? observer = null)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(access);
        PortabilityValidator.EnsurePhysicalIdentifiers(unit);
        var state = database.GetState(unit, access);
        return new InMemoryStorageSession(database, state, access, liveState: true, providerConnection: this, observer: observer);
    }

    public IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units)
        => BeginUnitOfWork(access, BatchWriteOptions.Default, units);

    public IUnitOfWork BeginUnitOfWork(
        StorageAccess access,
        BatchWriteOptions options,
        params StorageUnit[] units)
        => BeginUnitOfWork(access, options, observer: null, units);

    public IUnitOfWork BeginUnitOfWork(
        StorageAccess access,
        BatchWriteOptions options,
        IProviderCommandObserver? observer,
        params StorageUnit[] units)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(units);
        StorageAccessValidation.EnsureUnitOfWork(access);
        if (units.Length == 0)
            throw new ArgumentException("A unit of work must declare at least one storage unit.", nameof(units));

        foreach (var unit in units)
        {
            ArgumentNullException.ThrowIfNull(unit);
            PortabilityValidator.EnsurePhysicalIdentifiers(unit);
        }
        var states = units.Select(unit => database.GetState(unit, access)).ToArray();
        if (states.Select(state => state.Unit.Id).Distinct().Count() != states.Length)
            throw new ArgumentException("A unit of work cannot list the same storage unit twice.", nameof(units));

        return new InMemoryUnitOfWork(database, states, access, options, this, observer);
    }

    public void Dispose() => disposed = true;

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(InMemoryProviderConnection));
    }
}

internal sealed class InMemoryDatabase
{
    internal readonly object Gate = new();
    internal readonly Dictionary<StorageUnitId, InMemoryUnitState> Units = [];
    internal readonly Dictionary<PhysicalSchemaTargetIdentity, PhysicalSchemaAppliedState> SchemaHistory = [];

    // This is the reference provider's durable kernel-owned ledger. It lives beside, rather than
    // inside, a storage unit so the key is always (unit, scope, nonce), including for global units.
    internal readonly Dictionary<IdempotencyLedgerKey, IdempotencyLedgerEntry> IdempotencyLedger = [];

    internal readonly Dictionary<RetentionLedgerKey, RetentionLedgerEntry> RetentionLedger = [];

    internal InMemoryUnitState GetState(StorageUnit requested, StorageAccess access)
    {
        ArgumentNullException.ThrowIfNull(requested);
        ArgumentNullException.ThrowIfNull(access);
        AggregationProfileValidator.ValidateUnit(requested);
        lock (Gate)
        {
            if (!Units.TryGetValue(requested.Id, out var state))
                throw new InvalidOperationException(
                    $"Storage unit '{requested.Id.Value}' has not been applied to this provider.");

            ValidateScope(state.Unit, access);
            return state;
        }
    }

    internal static void ValidateScope(StorageUnit unit, StorageAccess access)
    {
        if (unit.Scope != access.Policy)
            throw new InvalidOperationException(
                $"Storage unit '{unit.Name}' requires {unit.Scope} access, but {access.Policy} was supplied.");
    }
}

internal sealed class InMemoryUnitState
{
    internal InMemoryUnitState(StorageUnit unit)
    {
        Unit = StorageDeclaration.Clone(unit);
    }

    internal StorageUnit Unit { get; private set; }

    internal void ReplaceDeclaration(StorageUnit unit) => Unit = StorageDeclaration.Clone(unit);

    // Compare-and-swap token used by unit-of-work commits.
    internal long Revision { get; set; }

    // ProviderSequence values are unit-wide, matching a relational identity/sequence rather
    // than a scope partition. Gaps are intentionally allowed when a staged transaction rolls back.
    internal long Sequence { get; set; }

    internal Dictionary<string, long> SequenceHighWaters { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, Dictionary<string, InMemoryEntry>> Partitions { get; } = [];

    internal List<ProviderIndex> PhysicalIndexes { get; } = [];

    internal InMemoryUnitState Clone()
    {
        var clone = new InMemoryUnitState(Unit);
        clone.Revision = Revision;
        clone.Sequence = Sequence;
        foreach (var pair in SequenceHighWaters)
            clone.SequenceHighWaters[pair.Key] = pair.Value;
        foreach (var partition in Partitions)
        {
            clone.Partitions[partition.Key] = partition.Value.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Clone(),
                StringComparer.Ordinal);
        }

        clone.PhysicalIndexes.AddRange(PhysicalIndexes.Select(index => new ProviderIndex(
            index.Name,
            index.Columns.Select(column => new ProviderIndexColumn(column.Column, column.Direction)).ToArray(),
            index.IsUnique,
            index.MissingValues,
            index.SchemaVersion)));

        return clone;
    }
}

internal sealed class InMemoryEntry
{
    internal InMemoryEntry(IReadOnlyDictionary<string, object?> values, long? version)
    {
        Values = StorageValues.Snapshot(values);
        Version = version;
    }

    internal IReadOnlyDictionary<string, object?> Values { get; }

    internal long? Version { get; }

    internal InMemoryEntry With(
        IReadOnlyDictionary<string, object?> values,
        long? version) => new(values, version);

    internal InMemoryEntry Clone() => new(Values, Version);
}

internal sealed class InMemoryProviderCatalog(InMemoryDatabase database) : IProviderCatalog
{
    public IReadOnlyList<ProviderIndex> ReadIndexes(StorageUnitId storageUnitId)
    {
        lock (database.Gate)
        {
            if (!database.Units.TryGetValue(storageUnitId, out var state))
                return [];

            return state.PhysicalIndexes
                .Select(index => new ProviderIndex(
                    index.Name,
                    index.Columns.Select(column => new ProviderIndexColumn(column.Column, column.Direction)).ToArray(),
                    index.IsUnique,
                    index.MissingValues,
                    index.SchemaVersion))
                .ToArray();
        }
    }
}

internal sealed class InMemorySchemaCoordinator : ISchemaCoordinator
{
    internal static readonly ProviderIdentity Identity = new("InMemory", "1.0");
    private readonly InMemoryDatabase database;

    internal InMemorySchemaCoordinator(InMemoryDatabase database)
    {
        this.database = database;
        Executor = new InMemoryPhysicalSchemaExecutor(database);
    }

    internal InMemoryPhysicalSchemaExecutor Executor { get; }

    public SchemaDiff Diff(StorageUnit desired)
    {
        var physical = Prepare(desired);
        var target = Target(physical);
        using var lease = Executor.AcquireApplicationLock(target.Identity);
        var history = Executor.ReadHistory(target.Identity, lease);
        ValidateUnplannedChanges(history.AppliedState?.Snapshot.Subject.Definition, physical);
        var plan = PhysicalSchemaDiffPlanner.Plan(target, history, DateTimeOffset.UtcNow);
        return new SchemaDiff(Describe(plan.Operations, history.AppliedState?.Snapshot.Subject.Definition, physical));
    }

    public SchemaApplyResult Apply(StorageUnit desired)
    {
        var physical = Prepare(desired);
        var target = Target(physical);
        lock (database.Gate)
        {
            var previous = database.Units.TryGetValue(physical.Id, out var state) ? state.Unit : null;
            ValidateUnplannedChanges(previous, physical);
            var result = PhysicalSchemaApplication.ApplyRecoverableWork(target, Executor);
            return new SchemaApplyResult(
                new SchemaDiff(Describe(result.Plan.Operations, previous, physical)),
                result.Outcome == PhysicalSchemaApplicationOutcome.Applied);
        }
    }

    internal static StorageUnit Prepare(StorageUnit desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ProviderOwnedColumns.ValidateLogicalDeclaration(desired);
        ConcurrencyDeclaration.ValidateDeclaration(desired);
        var portability = PortabilityValidator.Validate(desired);
        if (!portability.IsPortable)
        {
            var refusal = portability.Refusals[0];
            throw new InvalidOperationException($"{refusal.Code} at {refusal.Path}: {refusal.Message}");
        }
        desired.AppendIdempotency?.Validate(desired);
        desired.RetentionIdempotency?.Validate(desired);
        var physical = SearchKeyProjection.Expand(desired);
        AggregationProfileValidator.ValidateUnit(physical);
        return physical;
    }

    internal static PhysicalSchemaTarget Target(StorageUnit physical) =>
        new(new SchemaSubject(physical), Identity);

    private static void ValidateUnplannedChanges(StorageUnit? previous, StorageUnit desired)
    {
        if (previous is null)
            return;
        if (!LogicalKeyColumns(previous).SequenceEqual(LogicalKeyColumns(desired), StringComparer.Ordinal))
            throw new SchemaConflictException($"Storage unit '{desired.Name}' cannot change its key.");
        if (previous.Concurrency != desired.Concurrency)
            throw new SchemaConflictException(
                $"Storage unit '{desired.Name}' cannot change its concurrency declaration.");
        if (previous.Scope != desired.Scope)
            throw new SchemaConflictException(
                $"Storage unit '{desired.Name}' cannot change scope from {previous.Scope} to {desired.Scope}.");
        if (previous.SchemaVersion != desired.SchemaVersion)
            throw new SchemaConflictException(
                $"Storage unit '{desired.Name}' cannot change its schema version.");
        if (!string.Equals(
                RetentionCanonicalization.Canonicalize(previous.Retention),
                RetentionCanonicalization.Canonicalize(desired.Retention),
                StringComparison.Ordinal))
            throw new SchemaConflictException($"Storage unit '{desired.Name}' cannot change retention.");
        if (previous.AppendIdempotency?.Window != desired.AppendIdempotency?.Window ||
            !string.Equals(
                previous.AppendIdempotency?.LedgerName,
                desired.AppendIdempotency?.LedgerName,
                StringComparison.Ordinal))
            throw new SchemaConflictException($"Storage unit '{desired.Name}' cannot change append idempotency.");
        if (previous.RetentionIdempotency?.Window != desired.RetentionIdempotency?.Window ||
            !string.Equals(
                previous.RetentionIdempotency?.LedgerName,
                desired.RetentionIdempotency?.LedgerName,
                StringComparison.Ordinal))
            throw new SchemaConflictException($"Storage unit '{desired.Name}' cannot change retention idempotency.");
    }

    private static IReadOnlyList<string> LogicalKeyColumns(StorageUnit unit)
    {
        var columns = unit.Columns.ToDictionary(column => column.Name, StringComparer.Ordinal);
        return unit.Key.Columns.Select(column => columns[column].LogicalId).ToArray();
    }

    private static IReadOnlyList<SchemaChange> Describe(
        IEnumerable<PhysicalSchemaOperation> operations,
        StorageUnit? previous,
        StorageUnit desired)
    {
        var changes = SchemaChangeMapping.Describe(operations).ToList();
        var previousProfiles = previous?.AggregationProfiles
            .ToDictionary(profile => profile.Name, StringComparer.Ordinal) ?? [];
        var desiredProfiles = desired.AggregationProfiles.ToDictionary(profile => profile.Name, StringComparer.Ordinal);
        changes.AddRange(desiredProfiles.Values
            .Where(profile =>
                !previousProfiles.TryGetValue(profile.Name, out var prior) ||
                !string.Equals(
                    AggregationProfileCanonicalization.Canonicalize(prior),
                    AggregationProfileCanonicalization.Canonicalize(profile),
                    StringComparison.Ordinal))
            .Select(profile => new SchemaChange(SchemaChangeKind.UpdateAggregationProfile, profile.Name)));
        changes.AddRange(previousProfiles.Keys
            .Where(name => !desiredProfiles.ContainsKey(name))
            .Select(name => new SchemaChange(SchemaChangeKind.UpdateAggregationProfile, name)));
        return changes;
    }
}

internal sealed class InMemoryPhysicalSchemaExecutor(InMemoryDatabase database) : IPhysicalSchemaExecutor
{
    public IPhysicalSchemaApplicationLock AcquireApplicationLock(PhysicalSchemaTargetIdentity target)
    {
        Monitor.Enter(database.Gate);
        return new ApplicationLock(database.Gate, target);
    }

    public PhysicalSchemaHistoryState ReadHistory(
        PhysicalSchemaTargetIdentity target,
        IPhysicalSchemaApplicationLock applicationLock) =>
        database.SchemaHistory.TryGetValue(target, out var applied)
            ? PhysicalSchemaHistoryState.FromApplied(applied)
            : PhysicalSchemaHistoryState.Empty;

    public PhysicalSchemaOperationAcknowledgement ApplyOperation(
        PhysicalSchemaTargetIdentity target,
        PhysicalSchemaOperation operation,
        IPhysicalSchemaApplicationLock applicationLock) =>
        ApplyOperationBatch(target, [operation], applicationLock)[0];

    public IReadOnlyList<PhysicalSchemaOperationAcknowledgement> ApplyOperationBatch(
        PhysicalSchemaTargetIdentity target,
        IReadOnlyList<PhysicalSchemaOperation> operations,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        var lease = (ApplicationLock)applicationLock;
        database.Units.TryGetValue(target.SubjectId, out var current);
        var next = current?.Clone();
        foreach (var operation in operations)
            ApplyOperation(operation, ref next);
        if (next is not null && current is not null)
            next.Revision = checked(current.Revision + 1);
        lease.PendingState = next;
        lease.HasPendingState = true;
        var appliedAt = DateTimeOffset.UtcNow;
        return operations.Select(operation => new PhysicalSchemaOperationAcknowledgement(
            operation.Identity,
            operation.Fingerprint,
            appliedAt)).ToArray();
    }

    public void PublishAppliedState(
        PhysicalSchemaAppliedState state,
        string? expectedAppliedTargetFingerprint,
        IPhysicalSchemaApplicationLock applicationLock)
    {
        var lease = (ApplicationLock)applicationLock;
        var actual = database.SchemaHistory.TryGetValue(state.TargetIdentity, out var current)
            ? current.TargetFingerprint
            : null;
        if (!string.Equals(actual, expectedAppliedTargetFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"In-memory schema history CAS failed for '{state.TargetIdentity}'.");

        if (lease.HasPendingState)
        {
            if (lease.PendingState is null)
                database.Units.Remove(state.TargetIdentity.SubjectId);
            else
                database.Units[state.TargetIdentity.SubjectId] = lease.PendingState;
        }
        database.SchemaHistory[state.TargetIdentity] = state;
    }

    private static void ApplyOperation(PhysicalSchemaOperation operation, ref InMemoryUnitState? state)
    {
        switch (operation)
        {
            case CreatePrimaryStorageOperation create:
                state ??= new InMemoryUnitState(create.Subject.Definition);
                break;
            case AddColumnOperation:
            case FinalizeColumnOperation:
            case AlterColumnOperation:
            case RenamePrimaryStorageOperation:
            case ColumnSupersessionOperation:
            case ApplyProviderPhysicalSchemaDefinitionOperation:
            case PublishAppliedStateOperation:
                break;
            case BackfillColumnOperation { Derived: not null } backfill:
                RewriteRows(state!, values => SearchKeyProjection.Populate(backfill.Subject.Definition, values));
                break;
            case BackfillColumnOperation { Column.Default: not null } backfill:
                RewriteRows(state!, values =>
                {
                    var next = new Dictionary<string, object?>(values, StringComparer.Ordinal);
                    next[backfill.Column.Name] = StorageValues.CloneValue(backfill.Column.Default.Value);
                    return next;
                });
                break;
            case BackfillColumnOperation:
                break;
            case CreatePhysicalIndexOperation createIndex:
                state!.PhysicalIndexes.Add(ProviderIndex(createIndex.Index));
                break;
            case RebuildPhysicalIndexOperation rebuildIndex:
                state!.PhysicalIndexes.RemoveAll(index => index.Name == rebuildIndex.Index.Name);
                state.PhysicalIndexes.Add(ProviderIndex(rebuildIndex.Index));
                break;
            case RenameColumnOperation rename:
                RewriteRows(state!, values =>
                {
                    var next = new Dictionary<string, object?>(values, StringComparer.Ordinal);
                    if (next.Remove(rename.FromName, out var value))
                        next[rename.ToName] = value;
                    return next;
                });
                break;
            case DropColumnOperation dropColumn:
                RewriteRows(state!, values =>
                {
                    var next = new Dictionary<string, object?>(values, StringComparer.Ordinal);
                    next.Remove(dropColumn.Column.Name);
                    return next;
                });
                break;
            case DropPhysicalIndexOperation dropIndex:
                state!.PhysicalIndexes.RemoveAll(index => index.Name == dropIndex.Index.Name);
                break;
            case DropPrimaryStorageOperation:
                state = null;
                break;
            case ValidatePhysicalSchemaOperation validate when validate.Subject.Evolution.RetiresPrimaryStorage:
                if (state is not null)
                    throw new InvalidOperationException(
                        $"In-memory schema validation expected retired storage '{validate.Subject.Name}' to be absent.");
                break;
            case ValidatePhysicalSchemaOperation validate:
                if (state is null)
                    throw new InvalidOperationException(
                        $"In-memory schema validation expected active storage '{validate.Subject.Name}' to exist.");
                state.ReplaceDeclaration(validate.Subject.Definition);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation.Kind, null);
        }
    }

    private static void RewriteRows(
        InMemoryUnitState state,
        Func<IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>> rewrite)
    {
        foreach (var partition in state.Partitions.Values)
        {
            foreach (var pair in partition.ToArray())
                partition[pair.Key] = pair.Value.With(rewrite(pair.Value.Values), pair.Value.Version);
        }
    }

    private static ProviderIndex ProviderIndex(IndexDefinition index) => new(
        index.Name,
        index.Columns.Select(column => new ProviderIndexColumn(column.Column, column.Direction)).ToArray(),
        index.IsUnique,
        index.MissingValues,
        index.SchemaVersion);

    private sealed class ApplicationLock(object gate, PhysicalSchemaTargetIdentity target)
        : IPhysicalSchemaApplicationLock
    {
        internal InMemoryUnitState? PendingState { get; set; }

        internal bool HasPendingState { get; set; }

        public PhysicalSchemaTargetIdentity Target { get; } = target;

        public void Dispose() => Monitor.Exit(gate);
    }
}

internal static class StorageDeclaration
{
    internal static StorageUnit Clone(StorageUnit unit) => unit with
    {
        Columns = unit.Columns.Select(column => column with
        {
            Default = column.Default is null ? null : new PortableDefault(
                StorageValues.CloneValue(column.Default.Value))
        }).ToArray(),
        Key = new KeyDefinition { Columns = unit.Key.Columns.ToArray() },
        DerivedColumns = unit.DerivedColumns.ToArray(),
        Indexes = unit.Indexes.Select(index => index with
        {
            Columns = index.Columns.ToArray()
        }).ToArray(),
        AggregationProfiles = unit.AggregationProfiles.Select(AggregationProfileSnapshot.Capture).ToArray(),
        Retention = unit.Retention is null ? null : unit.Retention with
        {
            PartitionColumns = unit.Retention.PartitionColumns.ToArray()
        },
        RetentionIdempotency = unit.RetentionIdempotency is null ? null : unit.RetentionIdempotency with { }
    };
}

internal sealed class InMemoryStorageSession : IStorageSession, IProviderBoundStorageSession, IExactAppendStorageSession, IConcurrencyStorageSession, ICompareAndDeleteStorageSession, IRetentionStorageSession, IStorageInspectionSession, IExactRetentionStorageSession, IPrivilegedCrossScopeQuerySession
{
    private readonly InMemoryDatabase database;
    private InMemoryUnitState state;
    private readonly bool liveState;
    private readonly Dictionary<IdempotencyLedgerKey, IdempotencyLedgerEntry>? stagedLedger;
    private readonly Dictionary<RetentionLedgerKey, RetentionLedgerEntry>? stagedRetentionLedger;
    private readonly Dictionary<StorageUnitId, InMemoryUnitState>? stagedUnits;
    private readonly string partition;
    private bool disposed;

    internal InMemoryStorageSession(
        InMemoryDatabase database,
        InMemoryUnitState state,
        StorageAccess access,
        bool liveState = false,
        Dictionary<IdempotencyLedgerKey, IdempotencyLedgerEntry>? stagedLedger = null,
        Dictionary<StorageUnitId, InMemoryUnitState>? stagedUnits = null,
        Dictionary<RetentionLedgerKey, RetentionLedgerEntry>? stagedRetentionLedger = null,
        IStorageProviderConnection? providerConnection = null,
        IProviderCommandObserver? observer = null)
    {
        commandObserver = observer;
        this.database = database;
        this.state = state;
        this.liveState = liveState;
        this.stagedLedger = stagedLedger;
        this.stagedRetentionLedger = stagedRetentionLedger;
        this.stagedUnits = stagedUnits;
        Access = access;
        Unit = StorageDeclaration.Clone(state.Unit);
        partition = access.Scope?.Value ?? "<global>";
        ProviderConnection = providerConnection;
    }

    /// <summary>
    /// Counts every provider command this session issues. It belongs to the session because the session is
    /// what issues commands; it used to be read off an individual write's options, so a batch observed only
    /// whatever happened to be staged first.
    /// </summary>
    private readonly IProviderCommandObserver? commandObserver;
    private bool suppressQueryObservation;

    public StorageUnit Unit { get; }

    public StorageAccess Access { get; }

    internal IStorageProviderConnection? ProviderConnection { get; }

    IStorageProviderConnection? IProviderBoundStorageSession.ProviderConnection => ProviderConnection;

    public StoredEntry? Read(StorageKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        RefusePrivilegedPointOperation("read");
        lock (database.Gate)
        {
            ThrowIfDisposed();
            commandObserver?.Observe(new ProviderCommandEvent("in-memory.read", null, ProviderCommandKind.Read, IsProbe: false));
            var entry = Mutation.Read(CurrentState(), partition, key);
            return entry is null
                ? null
                : new StoredEntry(new StorageValues(SearchKeyProjection.PublicValues(entry.Values.Values)), entry.Version);
        }
    }

    public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        StorageAccessValidation.EnsureOrdinaryQuery(Access);
        if (!string.Equals(request.Table.Value, Unit.Name, StringComparison.Ordinal))
            throw new ArgumentException($"Query table '{request.Table.Value}' does not match session unit '{Unit.Name}'.", nameof(request));
        var suppliedOptions = options ?? QueryRenderOptions.Default;
        var searchKeyColumns = SearchKeyQueryMappings.For(Unit);
        var executionRequest = QuerySearchKeyRewriter.Rewrite(request, searchKeyColumns);
        var renderOptions = suppliedOptions.WithIdentityTieBreaks(Unit.Key.Columns
            .Select(name => QueryColumn(name))
            .Where(column => column is not null)
            .Select(column => column!)) with
        {
            SearchKeyColumns = searchKeyColumns
        };
        var validation = PortableQuerySemantics.Validate(executionRequest);
        if (!validation.IsPortable)
        {
            var refusal = validation.Refusals[0];
            throw new QueryRenderException(refusal.Code, refusal.Message + " (" + refusal.Path + ").");
        }
        ValidateInBudget(executionRequest.Where, suppliedOptions.InValueLimit, request.Table.Value);
        lock (database.Gate)
        {
            ThrowIfDisposed();
            if (!suppressQueryObservation)
                commandObserver?.Observe(new ProviderCommandEvent("in-memory.query", null, ProviderCommandKind.Read, IsProbe: false));
            var rows = CurrentState().Partitions.TryGetValue(partition, out var entries)
                ? entries.Values
                    .Where(entry => PortableQuerySemantics.Evaluate(executionRequest.Where, entry.Values))
                    .Select(entry => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(entry.Values, StringComparer.Ordinal))
                    .ToList()
                : new List<IReadOnlyDictionary<string, object?>>();

            if (executionRequest.LatestPerKey is not null)
                rows = LatestPerKeyRows(rows, executionRequest.LatestPerKey,
                    renderOptions.TieBreakColumns, renderOptions.LatestPartitionColumns);

            var order = renderOptions.GetEffectiveOrder(executionRequest);
            if (order.Length != 0)
                rows.Sort(new MemoryRowComparer(order));

            var deferContinuation = request.Paging.ContinuationToken is not null &&
                (request.Result.IncludesTotalCount || request.Distinct);
            if (!deferContinuation && request.Paging.ContinuationToken is { } token)
            {
                IReadOnlyList<QueryConstant> cursor;
                try
                {
                    cursor = QueryContinuationToken.Decode(token, executionRequest, renderOptions);
                }
                catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
                {
                    throw new QueryRenderException("GW-QUERY-013", "The keyset continuation token is invalid: " + exception.Message);
                }
                rows = rows.Where(row => IsAfter(row, order, cursor)).ToList();
            }

            var selectedIndex = renderOptions.FindPinnedIndex()?.Name;
            if (request.Result is ResultShape.Reduction reduction)
            {
                var reduced = ReduceRows(reduction, request, rows);
                return QueryResultMaterializer.Materialize(request, renderOptions, [reduced], selectedIndex,
                    sourceIncludesRequestedOffset: true,
                    sourceIncludesContinuation: true);
            }
            if (!request.Result.IncludesTotalCount)
                return QueryResultMaterializer.Materialize(request, renderOptions, rows, selectedIndex, sourceIncludesRequestedOffset: false);

            if (rows.Count == 0)
                return new QueryMaterializedResult(Array.Empty<IReadOnlyDictionary<string, object?>>(), 0L, null, selectedIndex);

            var counted = rows.Select((row, index) => index == 0
                ? (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(row)
                {
                    ["__groundwork_total_count"] = (long)rows.Count
                }
                : row).ToArray();
            return QueryResultMaterializer.Materialize(request, renderOptions, counted, selectedIndex,
                sourceIncludesRequestedOffset: false,
                sourceIncludesContinuation: !deferContinuation);
        }
    }

    public CrossScopeQueryResult QueryAcrossScopes(
        QueryRequest request,
        QueryRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Access.IsPrivilegedAcrossScopes)
            throw new InvalidOperationException(
                "GW-ACCESS-001: cross-scope queries require explicit privileged across-scope access.");
        if (!string.Equals(request.Table.Value, Unit.Name, StringComparison.Ordinal))
            throw new ArgumentException(
                $"Query table '{request.Table.Value}' does not match session unit '{Unit.Name}'.",
                nameof(request));
        StorageAccessValidation.ObservePrivilegedQuery(Access, Unit);

        var suppliedOptions = options ?? QueryRenderOptions.Default;
        var searchKeyColumns = SearchKeyQueryMappings.For(Unit);
        var executionRequest = QuerySearchKeyRewriter.Rewrite(request, searchKeyColumns);
        var table = new TableId(Unit.Name);
        var scopeToken = new ColumnRef(
            table,
            CrossScopeQueryMaterializer.ScopeTokenColumn,
            QueryType.String,
            isNullable: false);
        var renderOptions = suppliedOptions.WithIdentityTieBreaks(
            new[] { scopeToken }
                .Concat(Unit.Key.Columns
                    .Select(QueryColumn)
                    .Where(column => column is not null)
                    .Select(column => column!))) with
        {
            LatestPartitionColumns = [scopeToken],
            SearchKeyColumns = searchKeyColumns
        };
        var validation = PortableQuerySemantics.Validate(executionRequest);
        if (!validation.IsPortable)
        {
            var refusal = validation.Refusals[0];
            throw new QueryRenderException(
                refusal.Code,
                refusal.Message + " (" + refusal.Path + ").");
        }
        ValidateInBudget(executionRequest.Where, suppliedOptions.InValueLimit, request.Table.Value);

        lock (database.Gate)
        {
            ThrowIfDisposed();
            commandObserver?.Observe(new ProviderCommandEvent("in-memory.query-across-scopes", null, ProviderCommandKind.Read, IsProbe: false));
            var values = CurrentState().Partitions
                .SelectMany(partition => partition.Value.Values
                    .Where(entry => PortableQuerySemantics.Evaluate(executionRequest.Where, entry.Values))
                    .Select(entry =>
                    {
                        var row = new Dictionary<string, object?>(entry.Values, StringComparer.Ordinal)
                        {
                            [CrossScopeQueryMaterializer.RawScopeColumn] = partition.Key,
                            [CrossScopeQueryMaterializer.ScopeTokenColumn] =
                                CrossScopeQueryMaterializer.ScopeToken(new StorageScope(partition.Key))
                        };
                        return (IReadOnlyDictionary<string, object?>)row;
                    }))
                .ToList();

            if (executionRequest.LatestPerKey is not null)
                values = LatestPerKeyRows(values, executionRequest.LatestPerKey,
                    renderOptions.TieBreakColumns, renderOptions.LatestPartitionColumns);

            var order = renderOptions.GetEffectiveOrder(executionRequest);
            if (order.Length != 0)
                values.Sort(new MemoryRowComparer(order));

            var rows = values.Select(row => new CrossScopeQueryRow(
                new StorageScope((string)row[CrossScopeQueryMaterializer.RawScopeColumn]!),
                row)).ToArray();

            var boundRequest = QueryRequestExecution.WithProviderPredicate(
                executionRequest,
                executionRequest.Where,
                CrossScopeQueryMaterializer.BindingDiscriminator(Access));
            return CrossScopeQueryMaterializer.Materialize(
                boundRequest,
                renderOptions,
                rows,
                renderOptions.FindPinnedIndex()?.Name);
        }
    }

    public AggregationResult Aggregate(AggregationQuery query)
    {
        RefusePrivilegedPointOperation("aggregate");
        ArgumentNullException.ThrowIfNull(query);
        var profile = AggregationProfileValidator.ResolveOrThrow(Unit, query);
        AggregationExecutor.ValidateQuery(Unit, profile, query);
        commandObserver?.Observe(new ProviderCommandEvent("in-memory.aggregate", query.ProfileName, ProviderCommandKind.Read, IsProbe: false));
        // The executor scans through this session's public Query. That scan is the aggregate's own work,
        // already represented by the event above — letting it also raise in-memory.query would count one
        // logical operation twice, which is the exact failure the Kind/probe distinctions exist to avoid.
        // Sessions are single-caller (they are not thread-safe), so a field suffices.
        suppressQueryObservation = true;
        try
        {
            return AggregationSessionExecutor.Execute(this, query);
        }
        finally
        {
            suppressQueryObservation = false;
        }
    }

    private ColumnRef? QueryColumn(string name)
    {
        var column = Unit.Columns.Single(item => item.Name == name);
        if (Unit.Concurrency.IsOptimistic &&
            string.Equals(Unit.Concurrency.TokenColumn, column.Name, StringComparison.Ordinal))
            return null;
        return column.Type switch
        {
            PortableType.Boolean => new ColumnRef(new TableId(Unit.Name), name, QueryType.Boolean, column.IsNullable),
            PortableType.Int32 => new ColumnRef(new TableId(Unit.Name), name, QueryType.Int32, column.IsNullable),
            PortableType.Int64 => new ColumnRef(new TableId(Unit.Name), name, QueryType.Int64, column.IsNullable),
            PortableType.Decimal => new ColumnRef(new TableId(Unit.Name), name, QueryType.Decimal, column.IsNullable),
            PortableType.String => new ColumnRef(new TableId(Unit.Name), name, QueryType.String, column.IsNullable),
            PortableType.DateTimeOffset => new ColumnRef(new TableId(Unit.Name), name, QueryType.DateTimeOffset, column.IsNullable),
            PortableType.Guid => new ColumnRef(new TableId(Unit.Name), name, QueryType.Guid, column.IsNullable),
            PortableType.Binary => new ColumnRef(new TableId(Unit.Name), name, QueryType.Binary, column.IsNullable),
            _ => null
        };
    }

    private static void ValidateInBudget(Predicate predicate, int limit, string table)
    {
        switch (predicate)
        {
            case Predicate.In membership when membership.Values.Distinct().Count() > limit:
                throw new QueryRenderException(
                    "GW-QUERY-015",
                    $"Query on '{table}' has an In predicate on '{membership.Column.Name}' with {membership.Values.Distinct().Count()} distinct values, exceeding the configured maximum of {limit}.");
            case Predicate.And and:
                foreach (var term in and.Terms)
                    ValidateInBudget(term, limit, table);
                break;
            case Predicate.Or or:
                foreach (var term in or.Terms)
                    ValidateInBudget(term, limit, table);
                break;
            case Predicate.Not not:
                ValidateInBudget(not.Inner, limit, table);
                break;
        }
    }

    private static List<IReadOnlyDictionary<string, object?>> LatestPerKeyRows(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        LatestPerKey latest,
        IReadOnlyList<ColumnRef> tieBreakColumns,
        IReadOnlyList<ColumnRef> partitionColumns)
    {
        var groups = new[] { latest.Key }.Concat(partitionColumns)
            .GroupBy(column => column.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        return rows
            .Where(row => row.TryGetValue(latest.Timestamp.Name, out var timestamp) && timestamp is DateTimeOffset)
            .GroupBy(row => CompositeIdentity(row, groups), StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(row => ((DateTimeOffset)row[latest.Timestamp.Name]!).UtcTicks)
                .Aggregate((best, candidate) => CompareTie(candidate, best, tieBreakColumns) < 0 ? candidate : best))
            .ToList();
    }

    private static IReadOnlyDictionary<string, object?> ReduceRows(
        ResultShape.Reduction reduction,
        QueryRequest request,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        IEnumerable<IReadOnlyDictionary<string, object?>> input = rows;
        if (request.Distinct)
            input = input.GroupBy(row => row.TryGetValue(reduction.Column.Name, out var value) ? value : null).Select(group => group.First());
        if (request.Paging.Offset is int offset)
            input = input.Skip(offset);
        if (request.Paging.Limit is int limit)
            input = input.Take(limit);

        var values = input
            .Select(row => row.TryGetValue(reduction.Column.Name, out var value) ? value : null)
            .Where(value => value is not null)
            .ToArray();
        object? result = reduction switch
        {
            ResultShape.Sum when reduction.Column.Type is QueryType.Int32 or QueryType.Int64 =>
                values.Length == 0 ? null : values.Select(Convert.ToInt64).Aggregate(0L, checked((total, value) => total + value)),
            ResultShape.Sum when reduction.Column.Type == QueryType.Decimal =>
                values.Length == 0 ? null : values.Select(Convert.ToDecimal).Aggregate(0m, checked((total, value) => total + value)),
            ResultShape.Min => values.Length == 0 ? null : values.Min(),
            ResultShape.Max => values.Length == 0 ? null : values.Max(),
            _ => throw new InvalidOperationException("The in-memory reduction column is not portable.")
        };
        return new Dictionary<string, object?>(StringComparer.Ordinal) { [reduction.Column.Name] = result };
    }

    private static string CompositeIdentity(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<ColumnRef> columns) => string.Concat(columns.Select(column =>
    {
        var identity = ValueIdentity(row.TryGetValue(column.Name, out var value) ? value : null);
        return identity.Length.ToString(CultureInfo.InvariantCulture) + ":" + identity;
    }));

    private static int CompareTie(
        IReadOnlyDictionary<string, object?> left,
        IReadOnlyDictionary<string, object?> right,
        IReadOnlyList<ColumnRef> columns)
    {
        foreach (var column in columns)
        {
            left.TryGetValue(column.Name, out var leftValue);
            right.TryGetValue(column.Name, out var rightValue);
            var comparison = CompareForOrder(leftValue, rightValue,
                new OrderTerm(column, OrderDirection.Ascending, NullOrder.First));
            if (comparison != 0)
                return comparison;
        }
        return 0;
    }

    private static bool IsAfter(
        IReadOnlyDictionary<string, object?> row,
        IReadOnlyList<OrderTerm> order,
        IReadOnlyList<QueryConstant> cursor)
    {
        for (var index = 0; index < order.Count; index++)
        {
            var term = order[index];
            row.TryGetValue(term.Column.Name, out var actual);
            var boundary = cursor[index].Kind == QueryConstantKind.Null ? null : cursor[index].Value;
            var comparison = CompareForOrder(actual, boundary, term);
            if (comparison > 0)
                return true;
            if (comparison < 0)
                return false;
        }
        return false;
    }

    private sealed class MemoryRowComparer(IReadOnlyList<OrderTerm> order) : IComparer<IReadOnlyDictionary<string, object?>>
    {
        public int Compare(IReadOnlyDictionary<string, object?>? left, IReadOnlyDictionary<string, object?>? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            foreach (var term in order)
            {
                left.TryGetValue(term.Column.Name, out var leftValue);
                right.TryGetValue(term.Column.Name, out var rightValue);
                var comparison = CompareForOrder(leftValue, rightValue, term);
                if (comparison != 0)
                    return comparison;
            }
            return 0;
        }
    }

    private static int CompareForOrder(object? left, object? right, OrderTerm term)
    {
        if (left is null || right is null)
        {
            var nullComparison = left is null && right is null ? 0 : left is null
                ? term.NullOrder == NullOrder.First ? -1 : 1
                : term.NullOrder == NullOrder.First ? 1 : -1;
            return nullComparison;
        }

        var comparison = CompareValues(left, right);
        return term.Direction == OrderDirection.Descending ? -comparison : comparison;
    }

    private static int CompareValues(object left, object right)
    {
        if (left is string leftText && right is string rightText)
            return string.CompareOrdinal(leftText, rightText);
        if (left is DateTimeOffset leftInstant && right is DateTimeOffset rightInstant)
            return leftInstant.UtcTicks.CompareTo(rightInstant.UtcTicks);
        if (left is Guid leftGuid && right is Guid rightGuid)
            return CompareBytes(GuidBytes(leftGuid), GuidBytes(rightGuid));
        if (left is byte[] leftBytes && right is byte[] rightBytes)
            return CompareBytes(leftBytes, rightBytes);
        return ((IComparable)left).CompareTo(right);
    }

    private static string ValueIdentity(object? value) => value switch
    {
        null => "n:",
        string text => "s:" + text,
        int number => "i32:" + number.ToString(CultureInfo.InvariantCulture),
        long number => "i64:" + number.ToString(CultureInfo.InvariantCulture),
        decimal number => "d:" + number.ToString(CultureInfo.InvariantCulture),
        bool flag => flag ? "bool:1" : "bool:0",
        Guid guid => "g:" + guid.ToString("D"),
        byte[] bytes => "b:" + Convert.ToBase64String(bytes),
        DateTimeOffset instant => "t:" + instant.UtcTicks.ToString(CultureInfo.InvariantCulture),
        _ => value.GetType().FullName + ":" + (value.ToString() ?? string.Empty)
    };

    private static byte[] GuidBytes(Guid value)
    {
        var text = value.ToString("N");
        var bytes = new byte[16];
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] = byte.Parse(text.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return bytes;
    }

    private static int CompareBytes(byte[] left, byte[] right)
    {
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var comparison = left[index].CompareTo(right[index]);
            if (comparison != 0) return comparison;
        }
        return left.Length.CompareTo(right.Length);
    }

    public WriteOutcome Insert(StorageValues values, WriteOptions? options = null)
    {
        RefusePrivilegedPointOperation("insert");
        WritePreconditionValidator.ValidateWrittenValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.Insert, options);
        return Mutate(values, options, MutationKind.Insert, "in-memory.insert");
    }

    public WriteOutcome Update(StorageValues values, WriteOptions? options = null)
    {
        RefusePrivilegedPointOperation("update");
        WritePreconditionValidator.ValidateWrittenValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.Update, options);
        return Mutate(values, options, MutationKind.Update, "in-memory.update");
    }

    public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null)
    {
        RefusePrivilegedPointOperation("upsert");
        WritePreconditionValidator.ValidateWrittenValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.Upsert, options);
        return Mutate(values, options, MutationKind.Upsert, "in-memory.upsert", preserveCreatedAt: true);
    }

    public WriteOutcome ConditionalUpsert(StorageValues values, WriteOptions? options = null)
    {
        RefusePrivilegedPointOperation("conditional upsert");
        WritePreconditionValidator.ValidateWrittenValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.ConditionalUpsert, options);
        return Mutate(values, options, MutationKind.Upsert, "in-memory.conditional-upsert", exactOutcome: true, preserveCreatedAt: true);
    }

    public WriteOutcome Delete(StorageKey key, WriteOptions? options = null)
    {
        RefusePrivilegedPointOperation("delete");
        ArgumentNullException.ThrowIfNull(key);
        WritePreconditionValidator.Validate(Unit, WriteOperation.Delete, options);
        WriteOutcome deleteOutcome;
        lock (database.Gate)
        {
            ThrowIfDisposed();
            deleteOutcome = Mutation.Delete(CurrentState(), partition, key, options);
        }
        commandObserver?.Observe(new ProviderCommandEvent("in-memory.delete", null, ProviderCommandKind.Write, IsProbe: false));
        return deleteOutcome;
    }

    public WriteOutcome CompareAndDelete(
        StorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        WriteOptions? options = null)
    {
        RefusePrivilegedPointOperation("compare-and-delete");
        var canonicalKey = CompareAndDeleteValidation.CanonicalizeKey(Unit, key);
        var validated = CompareAndDeleteValidation.Validate(Unit, canonicalKey, expectedValues, options);
        WriteOutcome compareOutcome;
        lock (database.Gate)
        {
            ThrowIfDisposed();
            compareOutcome = Mutation.CompareAndDelete(CurrentState(), partition, canonicalKey, validated, options);
        }
        commandObserver?.Observe(new ProviderCommandEvent("in-memory.compare-and-delete", null, ProviderCommandKind.Write, IsProbe: false));
        return compareOutcome;
    }

    public StorageInspection Inspect()
    {
        RefusePrivilegedPointOperation("inspect");
        StorageInspectionSessionExtensions.EnsureProviderSequence(Unit);
        lock (database.Gate)
        {
            ThrowIfDisposed();
            var highWater = CurrentState().SequenceHighWaters.GetValueOrDefault(partition);
            return new StorageInspection(highWater == 0 ? null : highWater);
        }
    }

    public RetentionOperationResult ApplyRetention(OperationId operationId, RetentionExecutionOptions? options = null)
    {
        RefusePrivilegedPointOperation("retention");
        var declaration = Unit.RetentionIdempotency ?? throw new InvalidOperationException(
            $"Storage unit '{Unit.Name}' does not declare retention idempotency; declare RetentionIdempotency before using operation-identified retention.");
        options ??= new RetentionExecutionOptions();
        RetentionSessionExtensions.ValidateExecutionOptions(options);
        if (string.IsNullOrWhiteSpace(operationId.Nonce))
            throw new ArgumentException("An operation id requires a non-empty nonce.", nameof(operationId));
        if (operationId.Nonce.Length > 256)
            throw new ArgumentException("An operation nonce cannot exceed 256 UTF-16 code units.", nameof(operationId));

        lock (database.Gate)
        {
            ThrowIfDisposed();
            var ledger = liveState
                ? database.RetentionLedger
                : stagedRetentionLedger ?? throw new InvalidOperationException("The staged retention ledger is unavailable.");
            var workingLedger = ledger.ToDictionary(pair => pair.Key, pair => pair.Value);
            var now = DateTimeOffset.UtcNow;
            ReclaimRetentionLedger(workingLedger, Unit.Id, now, declaration.Window);
            var key = new RetentionLedgerKey(Unit.Id, partition, operationId.Nonce);
            var fingerprint = RetentionOperationCodec.Fingerprint(Unit, options);
            if (workingLedger.TryGetValue(key, out var existing) &&
                IdempotencyRules.IsWithinWindow(existing.CommittedAt, now, declaration.Window))
            {
                if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                    throw new RetentionIdempotencyConflictException(Unit.Id.Value, partition, operationId.Nonce, existing.Fingerprint, fingerprint);
                return existing.Result with { Status = RetentionOperationStatus.Replayed };
            }

            // Exact retention is transactional at the mapping boundary: execute against a
            // private snapshot and publish rows plus the ledger only after every batch has
            // completed. Cancellation therefore cannot leave a partially retained stream.
            var candidate = CurrentState().Clone();
            var retention = ApplyRetentionCore(options, candidate);
            if (liveState)
                database.Units[Unit.Id] = candidate;
            else
            {
                stagedUnits![Unit.Id] = candidate;
                state = candidate;
            }
            var result = new RetentionOperationResult(
                RetentionOperationStatus.Executed,
                retention.DeletedRows,
                retention.Batches,
                retention.Completed);
            workingLedger[key] = new RetentionLedgerEntry(now, fingerprint, result);
            ledger.Clear();
            foreach (var pair in workingLedger)
                ledger[pair.Key] = pair.Value;
            return result;
        }
    }

    public RetentionResult ApplyRetention(RetentionExecutionOptions? options = null)
    {
        RefusePrivilegedPointOperation("retention");
        options ??= new RetentionExecutionOptions();
        RetentionSessionExtensions.ValidateExecutionOptions(options);
        return ApplyRetentionCore(options);
    }

    private RetentionResult ApplyRetentionCore(
        RetentionExecutionOptions options,
        InMemoryUnitState? targetState = null)
    {
        options ??= new RetentionExecutionOptions();
        if (options.MaxRowsPerBatch <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxRowsPerBatch));
        var declaration = Unit.Retention ??
            throw new InvalidOperationException($"Storage unit '{Unit.Name}' does not declare retention.");

        string[] victimKeys;
        lock (database.Gate)
        {
            ThrowIfDisposed();
            options.CancellationToken.ThrowIfCancellationRequested();
            var current = targetState ?? CurrentState();
            if (!current.Partitions.TryGetValue(partition, out var entries) || entries.Count == 0)
                return new RetentionResult(0, 0);

            // Snapshot the watermark under the short read lock. Deleting outside that lock
            // prevents OnAppend from turning a retention scan into a write convoy.
            var rows = entries.Values.Select(entry => entry.Values).ToArray();
            victimKeys = RetentionRows.OrderVictims(
                Unit,
                declaration,
                RetentionSessionExtensions.EffectiveKeepNewest(Unit, options),
                rows)
                .Select(row => InMemoryKey(Unit, row))
                .ToArray();
        }

        var deleted = 0;
        var batches = 0;
        foreach (var batch in victimKeys.Chunk(options.MaxRowsPerBatch))
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            lock (database.Gate)
            {
                ThrowIfDisposed();
                var current = targetState ?? CurrentState();
                if (!current.Partitions.TryGetValue(partition, out var entries))
                    continue;
                foreach (var identity in batch)
                {
                    options.CancellationToken.ThrowIfCancellationRequested();
                    if (entries.Remove(identity))
                    {
                        deleted++;
                        current.Revision = checked(current.Revision + 1);
                    }
                }
            }

            batches++;
            commandObserver?.Observe(new ProviderCommandEvent("in-memory.retention", null, ProviderCommandKind.Write, IsProbe: false));
        }

        return new RetentionResult(deleted, batches);
    }

    public WriteOutcome Append(OperationId operationId, IReadOnlyList<StorageValues> values)
    {
        RefusePrivilegedPointOperation("append");
        var declaration = IdempotencyRules.RequireDeclaration(Unit);
        IdempotencyRules.ValidateOperation(Unit, operationId, values);
        AppendOutcomeReport outcome;
        lock (database.Gate)
        {
            ThrowIfDisposed();
            outcome = AppendCore(operationId, values, declaration, exactOutcomes: false);
        }
        commandObserver?.Observe(new ProviderCommandEvent("in-memory.append", null, ProviderCommandKind.Write, IsProbe: false));
        if (Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
            outcome.Status is WriteOutcomeStatus.Inserted or WriteOutcomeStatus.Replayed)
            ApplyOnAppendRetention();
        return new(outcome.Status);
    }

    public AppendOutcomeReport AppendWithOutcomes(OperationId operationId, IReadOnlyList<StorageValues> values)
    {
        RefusePrivilegedPointOperation("append with outcomes");
        var declaration = IdempotencyRules.RequireDeclaration(Unit);
        IdempotencyRules.ValidateOperation(Unit, operationId, values);
        AppendOutcomeReport outcome;
        lock (database.Gate)
        {
            ThrowIfDisposed();
            outcome = AppendCore(operationId, values, declaration, exactOutcomes: true);
        }
        commandObserver?.Observe(new ProviderCommandEvent("in-memory.append", null, ProviderCommandKind.Write, IsProbe: false));
        if (Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
            outcome.Status is WriteOutcomeStatus.Inserted or WriteOutcomeStatus.Replayed)
            ApplyOnAppendRetention();
        return outcome;
    }

    private void RefusePrivilegedPointOperation(string operation)
        => StorageAccessValidation.EnsurePointOperation(Access, operation);

    private AppendOutcomeReport AppendCore(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        AppendIdempotencyDeclaration declaration,
        bool exactOutcomes)
    {
        var now = DateTimeOffset.UtcNow;
        var ledger = liveState
            ? database.IdempotencyLedger
            : stagedLedger ?? throw new InvalidOperationException("The staged append ledger is unavailable.");
        var workingLedger = ledger.ToDictionary(pair => pair.Key, pair => pair.Value);
        ReclaimLedger(workingLedger, Unit.Id, now, declaration.Window);
        var ledgerKey = new IdempotencyLedgerKey(Unit.Id, partition, operationId.Nonce);
        var fingerprint = ExactAppendCodec.Fingerprint(Unit, values);
        if (workingLedger.TryGetValue(ledgerKey, out var existing))
        {
            if (IdempotencyRules.IsWithinWindow(existing.CommittedAt, now, declaration.Window))
            {
                if (exactOutcomes && !string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                    throw new AppendIdempotencyConflictException(Unit.Id.Value, partition, operationId.Nonce, existing.Fingerprint, fingerprint);
                return new AppendOutcomeReport(WriteOutcomeStatus.Replayed, ExactAppendCodec.DeserializeOutcomes(existing.SerializedOutcomes));
            }
            workingLedger.Remove(ledgerKey);
        }

        var candidate = CurrentState().Clone();
        var outcomes = new List<WriteOutcome>(values.Count);
        foreach (var value in values)
        {
            WritePreconditionValidator.ValidateWrittenValues(Unit, value.Values);
            var outcome = Mutation.Apply(candidate, partition, value, WriteOptions.Unconditional, MutationKind.Insert);
            if (!outcome.Succeeded)
                throw new InvalidOperationException($"Append row failed with outcome '{outcome.Status}'.");
            outcomes.Add(outcome);
        }

        state = candidate;
        if (liveState)
            database.Units[Unit.Id] = candidate;
        else
            stagedUnits![Unit.Id] = candidate;
        workingLedger[ledgerKey] = new IdempotencyLedgerEntry(now, fingerprint, ExactAppendCodec.SerializeOutcomes(outcomes));
        ledger.Clear();
        foreach (var pair in workingLedger)
            ledger[pair.Key] = pair.Value;
        return new AppendOutcomeReport(WriteOutcomeStatus.Inserted, outcomes);
    }

    private static void ReclaimLedger(
        Dictionary<IdempotencyLedgerKey, IdempotencyLedgerEntry> ledger,
        StorageUnitId unit,
        DateTimeOffset providerNow,
        TimeSpan window)
    {
        var cutoff = IdempotencyRules.ReclamationCutoff(providerNow, window);
        foreach (var key in ledger
                     .Where(pair => pair.Key.Unit == unit && pair.Value.CommittedAt <= cutoff)
                     .Take(128)
                     .Select(pair => pair.Key)
                     .ToArray())
            ledger.Remove(key);
    }

    private static void ReclaimRetentionLedger(
        Dictionary<RetentionLedgerKey, RetentionLedgerEntry> ledger,
        StorageUnitId unit,
        DateTimeOffset providerNow,
        TimeSpan window)
    {
        var cutoff = IdempotencyRules.ReclamationCutoff(providerNow, window);
        foreach (var key in ledger
                     .Where(pair => pair.Key.Unit == unit && pair.Value.CommittedAt <= cutoff)
                     .Take(128)
                     .Select(pair => pair.Key)
                     .ToArray())
            ledger.Remove(key);
    }

    /// <summary>
    /// The reference provider keeps every unit in process behind a monitor, so it has no I/O to
    /// yield a thread for. The asynchronous surface observes cancellation, runs the same guarded
    /// body on the calling thread, and returns an already-completed task.
    /// </summary>
    private static ValueTask<T> Completed<T>(CancellationToken cancellationToken, Func<T> operation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(operation());
    }

    public ValueTask<StoredEntry?> ReadAsync(StorageKey key, CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => Read(key));

    public ValueTask<QueryMaterializedResult> QueryAsync(
        QueryRequest request,
        QueryRenderOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => Query(request, options));

    public ValueTask<CrossScopeQueryResult> QueryAcrossScopesAsync(
        QueryRequest request,
        QueryRenderOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => QueryAcrossScopes(request, options));

    public ValueTask<AggregationResult> AggregateAsync(
        AggregationQuery query,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => Aggregate(query));

    public ValueTask<WriteOutcome> InsertAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => Insert(values, options));

    public ValueTask<WriteOutcome> UpdateAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => Update(values, options));

    public ValueTask<WriteOutcome> UpsertAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => Upsert(values, options));

    public ValueTask<WriteOutcome> DeleteAsync(
        StorageKey key,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => Delete(key, options));

    public ValueTask<WriteOutcome> ConditionalUpsertAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => ConditionalUpsert(values, options));

    public ValueTask<WriteOutcome> CompareAndDeleteAsync(
        StorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => CompareAndDelete(key, expectedValues, options));

    public ValueTask<WriteOutcome> AppendAsync(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => Append(operationId, values));

    public ValueTask<AppendOutcomeReport> AppendWithOutcomesAsync(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, () => AppendWithOutcomes(operationId, values));

    public ValueTask<StorageInspection> InspectAsync(CancellationToken cancellationToken = default) =>
        Completed(cancellationToken, Inspect);

    public ValueTask<RetentionResult> ApplyRetentionAsync(RetentionExecutionOptions? options = null) =>
        Completed(options?.CancellationToken ?? CancellationToken.None, () => ApplyRetention(options));

    public ValueTask<RetentionOperationResult> ApplyRetentionAsync(
        OperationId operationId,
        RetentionExecutionOptions? options = null) =>
        Completed(options?.CancellationToken ?? CancellationToken.None,
            () => ApplyRetention(operationId, options));

    internal void Close() => disposed = true;

    private WriteOutcome Mutate(
        StorageValues values,
        WriteOptions? options,
        MutationKind kind,
        string operation,
        bool exactOutcome = false,
        bool preserveCreatedAt = false)
    {
        ArgumentNullException.ThrowIfNull(values);
        values = new StorageValues(SearchKeyProjection.Populate(Unit, values.Values));
        WriteOutcome outcome;
        lock (database.Gate)
        {
            ThrowIfDisposed();
            outcome = Mutation.Apply(CurrentState(), partition, values, options, kind, exactOutcome, preserveCreatedAt);
        }
        // Observed after the provider work ran: an admission failure that throws above issued no command,
        // and a NotFound/conflict outcome is still one executed command.
        commandObserver?.Observe(new ProviderCommandEvent(operation, null, ProviderCommandKind.Write, IsProbe: false));

        if (outcome.Succeeded && Unit.Retention?.Trigger == RetentionTrigger.OnAppend &&
            kind is MutationKind.Insert or MutationKind.Upsert)
            ApplyOnAppendRetention();
        return outcome;
    }

    private void ApplyOnAppendRetention()
    {
        // Retention runs after the write lock is released. Providers with native post-commit
        // retention follow the same shape, so concurrent appends do not queue behind a scan.
        void Cleanup() => ApplyRetention(new RetentionExecutionOptions
        {
            MaxRowsPerBatch = 512
        });
        if (liveState)
            OnAppendRetentionCoordinator.Run(database, Unit, Access.Scope?.Value, Cleanup);
        else
            Cleanup();
    }

    private static string InMemoryKey(StorageUnit unit, IReadOnlyDictionary<string, object?> values) =>
        string.Join("|", unit.Key.Columns.Select(column => ValueCanonicalizer.Canonical(values.GetValueOrDefault(column))));

    private InMemoryUnitState CurrentState() =>
        liveState ? database.Units[Unit.Id] : state;

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(InMemoryStorageSession));
    }
}

internal readonly record struct IdempotencyLedgerKey(StorageUnitId Unit, string Scope, string Nonce);

internal sealed record IdempotencyLedgerEntry(
    DateTimeOffset CommittedAt,
    string Fingerprint,
    string SerializedOutcomes);

internal readonly record struct RetentionLedgerKey(StorageUnitId Unit, string Scope, string Nonce);

internal sealed record RetentionLedgerEntry(
    DateTimeOffset CommittedAt,
    string Fingerprint,
    RetentionOperationResult Result);

internal sealed class InMemoryUnitOfWork : IUnitOfWork
{
    private readonly IStorageProviderConnection providerConnection;
    private readonly IProviderCommandObserver? commandObserver;
    private readonly InMemoryDatabase database;
    private readonly StorageAccess access;
    private readonly Dictionary<StorageUnitId, InMemoryUnitState> staged;
    private readonly Dictionary<StorageUnitId, long> baseRevisions;
    private readonly Dictionary<IdempotencyLedgerKey, IdempotencyLedgerEntry> stagedLedger;
    private readonly Dictionary<RetentionLedgerKey, RetentionLedgerEntry> stagedRetentionLedger;
    private readonly List<InMemoryStorageSession> sessions = [];
    private readonly BatchContext batch;
    private bool terminal;

    internal InMemoryUnitOfWork(
        InMemoryDatabase database,
        IReadOnlyList<InMemoryUnitState> states,
        StorageAccess access,
        BatchWriteOptions options,
        IStorageProviderConnection providerConnection,
        IProviderCommandObserver? observer = null)
    {
        this.providerConnection = providerConnection ?? throw new ArgumentNullException(nameof(providerConnection));
        commandObserver = observer;
        this.database = database;
        this.access = access;
        batch = new BatchContext(options);
        lock (database.Gate)
        {
            baseRevisions = states.ToDictionary(state => state.Unit.Id, state => state.Revision);
            staged = states.ToDictionary(state => state.Unit.Id, state => state.Clone());
            stagedLedger = database.IdempotencyLedger.ToDictionary(pair => pair.Key, pair => pair.Value);
            stagedRetentionLedger = database.RetentionLedger.ToDictionary(pair => pair.Key, pair => pair.Value);
        }
    }

    public IStorageSession OpenSession(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ThrowIfTerminal();
        if (!staged.TryGetValue(unit.Id, out var state))
            throw new InvalidOperationException(
                $"Storage unit '{unit.Id.Value}' was not declared for this unit of work.");

        var session = new InMemoryStorageSession(database, state, access, stagedLedger: stagedLedger, stagedUnits: staged, stagedRetentionLedger: stagedRetentionLedger, providerConnection: providerConnection, observer: commandObserver);
        sessions.Add(session);
        var batched = BatchStorageSession.Create(session, batch);
        batch.Register(batched);
        return batched;
    }

    public void Stage(RowWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ThrowIfTerminal();
        if (!staged.ContainsKey(write.Unit.Id))
            throw new InvalidOperationException(
                $"Storage unit '{write.Unit.Id.Value}' was not declared for this unit of work.");
        if (!sessions.Any(session => session.Unit.Id == write.Unit.Id))
            _ = OpenSession(write.Unit);
        batch.Stage(write);
        if (batch.ReachedCap)
            batch.FlushAll();
    }

    public BatchWriteSummary Commit() => BatchWriteSummary.FromOutcomes(CompleteCommit());

    public BatchWriteReport CommitWithOutcomes()
    {
        ThrowIfTerminal();
        batch.RequireExactOutcomes();
        return new BatchWriteReport(CompleteCommit());
    }

    private IReadOnlyList<RowWriteOutcome> CompleteCommit()
    {
        ThrowIfTerminal();
        batch.FlushAll();
        lock (database.Gate)
        {
            foreach (var pair in staged)
            {
                if (!database.Units.TryGetValue(pair.Key, out var current) ||
                    current.Revision != baseRevisions[pair.Key])
                {
                    throw new InvalidOperationException(
                        $"Storage unit '{pair.Key.Value}' changed while the unit of work was active.");
                }
            }

            foreach (var pair in staged)
                database.Units[pair.Key] = pair.Value;
            var stagedUnits = this.staged.Keys.ToHashSet();
            foreach (var key in database.IdempotencyLedger.Keys
                         .Where(key => stagedUnits.Contains(key.Unit))
                         .ToArray())
                database.IdempotencyLedger.Remove(key);
            foreach (var pair in stagedLedger.Where(pair => stagedUnits.Contains(pair.Key.Unit)))
                database.IdempotencyLedger[pair.Key] = pair.Value;
            foreach (var key in database.RetentionLedger.Keys
                         .Where(key => stagedUnits.Contains(key.Unit))
                         .ToArray())
                database.RetentionLedger.Remove(key);
            foreach (var pair in stagedRetentionLedger.Where(pair => stagedUnits.Contains(pair.Key.Unit)))
                database.RetentionLedger[pair.Key] = pair.Value;
        }

        terminal = true;
        CloseSessions();
        return batch.DrainCompleted();
    }

    /// <summary>
    /// The reference provider commits in process, so an asynchronous commit runs the same body on
    /// the calling thread and returns an already-completed task.
    /// </summary>
    public ValueTask<BatchWriteReport> CommitWithOutcomesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CommitWithOutcomes());
    }

    public ValueTask<BatchWriteSummary> CommitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Commit());
    }

    public void Rollback()
    {
        ThrowIfTerminal();
        terminal = true;
        CloseSessions();
    }

    public void Dispose()
    {
        if (!terminal)
            Rollback();
    }

    private void ThrowIfTerminal()
    {
        if (terminal)
            throw new InvalidOperationException("The unit of work is already terminal.");
    }

    private void CloseSessions()
    {
        foreach (var session in sessions)
            session.Close();
    }
}

internal enum MutationKind
{
    Insert,
    Update,
    Upsert
}

internal static class Mutation
{
    internal static StoredEntry? Read(InMemoryUnitState state, string partition, StorageKey key)
    {
        var identity = Key(state.Unit, key.Values);
        return state.Partitions.TryGetValue(partition, out var entries) &&
               entries.TryGetValue(identity, out var entry)
            ? new StoredEntry(new StorageValues(entry.Values), entry.Version)
            : null;
    }

    internal static WriteOutcome Apply(
        InMemoryUnitState state,
        string partition,
        StorageValues values,
        WriteOptions? options,
        MutationKind kind,
        bool exactOutcome = false,
        bool preserveCreatedAt = false)
    {
        ValidateValues(
            state.Unit,
            values.Values,
            requireAllNonNullable: kind == MutationKind.Insert,
            rejectGeneratedInsert: kind == MutationKind.Insert);
        var sequence = state.Unit.Columns.FirstOrDefault(column =>
            column.Generation == ColumnGeneration.ProviderSequence);
        var generated = new Dictionary<string, object?>(StringComparer.Ordinal);
        var sourceValues = values.Values;
        var hasSequenceLocator = sequence is not null && sourceValues.ContainsKey(sequence.Name);
        if (sequence is not null && !hasSequenceLocator && (kind is MutationKind.Insert or MutationKind.Upsert))
        {
            var copy = sourceValues.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var value = checked(++state.Sequence);
            copy[sequence.Name] = value;
            sourceValues = new StorageValues(copy).Values;
            generated[sequence.Name] = value;
        }
        var identity = Key(state.Unit, sourceValues);
        var entries = GetEntries(state, partition);
        entries.TryGetValue(identity, out var existing);

        if (kind == MutationKind.Insert && existing is not null)
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing.Version);
        if (kind == MutationKind.Update && existing is null)
            return new WriteOutcome(WriteOutcomeStatus.NotFound);
        if (kind == MutationKind.Upsert && hasSequenceLocator && existing is null)
            return new WriteOutcome(WriteOutcomeStatus.NotFound);

        var precondition = options?.Precondition ?? WritePrecondition.Unconditional;
        if (state.Unit.Concurrency.IsOptimistic)
        {
            if (precondition.Kind == WritePreconditionKind.CreateOnly && existing is not null)
                return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);
            if (precondition.Kind == WritePreconditionKind.IfVersion &&
                (existing is null || precondition.Version != existing.Version))
                return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing?.Version);
        }

        var storedValues = sourceValues;
        if (existing is not null && kind != MutationKind.Insert)
        {
            var merged = existing.Values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            foreach (var pair in sourceValues)
                merged[pair.Key] = pair.Value;
            storedValues = merged;
        }

        ValidateValues(
            state.Unit,
            storedValues,
            requireAllNonNullable: true,
            rejectGeneratedInsert: false);

        if (!UniqueIndexesAllow(state.Unit, entries, identity, storedValues))
            return new WriteOutcome(WriteOutcomeStatus.UniqueViolation, existing?.Version);

        long? version = state.Unit.Concurrency.IsOptimistic
            ? existing?.Version + 1 ?? 1
            : null;
        var status = kind switch
        {
            MutationKind.Insert => WriteOutcomeStatus.Inserted,
            MutationKind.Update => WriteOutcomeStatus.Updated,
            MutationKind.Upsert when hasSequenceLocator => WriteOutcomeStatus.Updated,
            MutationKind.Upsert when exactOutcome && existing is null => WriteOutcomeStatus.Inserted,
            MutationKind.Upsert when exactOutcome => WriteOutcomeStatus.Updated,
            _ => WriteOutcomeStatus.Upserted
        };
        if ((exactOutcome || preserveCreatedAt) && existing is not null &&
            existing.Values.TryGetValue("createdAt", out var existingCreatedAt))
        {
            var preserved = storedValues.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            preserved["createdAt"] = existingCreatedAt;
            storedValues = new StorageValues(preserved).Values;
        }
        entries[identity] = new InMemoryEntry(storedValues, version);
        if (generated.TryGetValue(sequence?.Name ?? string.Empty, out var generatedValue) && generatedValue is long generatedSequence)
        {
            state.SequenceHighWaters[partition] = Math.Max(
                state.SequenceHighWaters.GetValueOrDefault(partition),
                generatedSequence);
        }
        state.Revision = checked(state.Revision + 1);
        return new WriteOutcome(status, version, generatedValues: generated);
    }

    internal static WriteOutcome Delete(
        InMemoryUnitState state,
        string partition,
        StorageKey key,
        WriteOptions? options)
    {
        var identity = Key(state.Unit, key.Values);
        var entries = GetEntries(state, partition);
        if (!entries.TryGetValue(identity, out var existing))
            return new WriteOutcome(WriteOutcomeStatus.NotFound);

        var precondition = options?.Precondition ?? WritePrecondition.Unconditional;
        if (state.Unit.Concurrency.IsOptimistic && precondition.Kind == WritePreconditionKind.IfVersion &&
            precondition.Version != existing.Version)
            return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);

        entries.Remove(identity);
        state.Revision = checked(state.Revision + 1);
        return new WriteOutcome(WriteOutcomeStatus.Deleted,
            state.Unit.Concurrency.IsOptimistic ? existing.Version : null);
    }

    internal static WriteOutcome CompareAndDelete(
        InMemoryUnitState state,
        string partition,
        StorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        WriteOptions? options)
    {
        var identity = Key(state.Unit, key.Values);
        var entries = GetEntries(state, partition);
        if (!entries.TryGetValue(identity, out var existing))
            return new WriteOutcome(WriteOutcomeStatus.NotFound);

        if (options?.Precondition.Kind == WritePreconditionKind.IfVersion &&
            options.Precondition.Version != existing.Version)
            return new WriteOutcome(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);

        foreach (var pair in expectedValues)
        {
            var definition = state.Unit.Columns.Single(column => column.Name == pair.Key);
            var actual = existing.Values.GetValueOrDefault(pair.Key);
            if (!CompareAndDeleteValidation.ValuesEqual(actual, pair.Value, definition.Type))
                return new WriteOutcome(WriteOutcomeStatus.ComparisonMismatch, existing.Version);
        }

        entries.Remove(identity);
        state.Revision = checked(state.Revision + 1);
        return new WriteOutcome(
            WriteOutcomeStatus.Deleted,
            state.Unit.Concurrency.IsOptimistic ? existing.Version : null);
    }

    private static Dictionary<string, InMemoryEntry> GetEntries(
        InMemoryUnitState state,
        string partition)
    {
        if (!state.Partitions.TryGetValue(partition, out var entries))
        {
            entries = new Dictionary<string, InMemoryEntry>(StringComparer.Ordinal);
            state.Partitions.Add(partition, entries);
        }

        return entries;
    }

    private static bool UniqueIndexesAllow(
        StorageUnit unit,
        IReadOnlyDictionary<string, InMemoryEntry> entries,
        string identity,
        IReadOnlyDictionary<string, object?> values)
    {
        foreach (var index in unit.Indexes.Where(index => index.IsUnique))
        {
            var candidate = IndexKey(index, values);
            if (candidate is null)
                continue;
            foreach (var pair in entries)
            {
                if (pair.Key == identity)
                    continue;
                if (IndexKey(index, pair.Value.Values) == candidate)
                    return false;
            }
        }

        return true;
    }

    private static string? IndexKey(
        IndexDefinition index,
        IReadOnlyDictionary<string, object?> values)
    {
        var parts = new List<string>(index.Columns.Count);
        foreach (var column in index.Columns)
        {
            if (!values.TryGetValue(column.Column, out var value))
            {
                if (index.MissingValues == MissingValueBehavior.Excluded)
                    return null;
                parts.Add("<missing>");
            }
            else
                parts.Add(ValueCanonicalizer.Canonical(value));
        }

        return string.Join("|", parts);
    }

    private static string Key(StorageUnit unit, IReadOnlyDictionary<string, object?> values)
    {
        var parts = unit.Key.Columns.Select(column =>
        {
            if (!values.TryGetValue(column, out var value))
                throw new ArgumentException($"Key column '{column}' is required.", nameof(values));
            var definition = unit.Columns.Single(candidate => candidate.Name == column);
            return ValueCanonicalizer.Canonical(value, definition.Type);
        });
        return string.Join("|", parts);
    }

    private static void ValidateValues(
        StorageUnit unit,
        IReadOnlyDictionary<string, object?> values,
        bool requireAllNonNullable,
        bool rejectGeneratedInsert)
    {
        var known = unit.Columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        var unknown = values.Keys.Where(key => !known.Contains(key)).OrderBy(key => key, StringComparer.Ordinal).FirstOrDefault();
        if (unknown is not null)
            throw new ArgumentException($"Column '{unknown}' is not declared by '{unit.Name}'.", nameof(values));

        foreach (var column in unit.Columns.Where(column =>
                     !column.IsNullable &&
                     !(unit.Concurrency.IsOptimistic &&
                       string.Equals(unit.Concurrency.TokenColumn, column.Name, StringComparison.Ordinal))))
        {
            if (column.Generation == ColumnGeneration.ProviderSequence)
            {
                if (rejectGeneratedInsert && values.ContainsKey(column.Name))
                    throw new ArgumentException($"ProviderSequence column '{column.Name}' is assigned by the in-memory provider and cannot be supplied for Insert.", nameof(values));
                continue;
            }
            if ((values.TryGetValue(column.Name, out var value) && value is null) ||
                (requireAllNonNullable && !values.ContainsKey(column.Name)))
                throw new ArgumentException($"Non-nullable column '{column.Name}' is required.", nameof(values));
        }
    }
}

internal static class ValueCanonicalizer
{
    internal static string Canonical(object? value, PortableType type) => type switch
    {
        PortableType.Int64 when value is int or long => Canonical(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        PortableType.Decimal when value is byte or sbyte or short or ushort or int or uint or long or ulong or decimal =>
            Canonical(Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
        PortableType.DateTimeOffset when value is DateTimeOffset timestamp => Canonical(timestamp.ToUniversalTime()),
        _ => Canonical(value)
    };

    internal static string Canonical(object? value) => value switch
    {
        null => "null",
        string text => $"string:{text.Length}:{text}",
        bool boolean => $"bool:{boolean}",
        int number => $"int:{number.ToString(CultureInfo.InvariantCulture)}",
        long number => $"long:{number.ToString(CultureInfo.InvariantCulture)}",
        decimal number => $"decimal:{number.ToString(CultureInfo.InvariantCulture)}",
        double number => $"double:{number.ToString("R", CultureInfo.InvariantCulture)}",
        float number => $"float:{number.ToString("R", CultureInfo.InvariantCulture)}",
        Guid guid => $"guid:{guid:D}",
        DateTimeOffset timestamp => $"timestamp:{timestamp:O}",
        byte[] bytes => $"binary:{Convert.ToBase64String(bytes)}",
        JsonElement json => $"json:{json.GetRawText()}",
        _ => $"{value.GetType().FullName}:{value}"
    };
}
