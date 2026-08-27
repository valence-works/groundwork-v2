using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Store;

namespace Groundwork.MongoDb;

/// <summary>Provides the production provider-neutral MongoDB contract over the native adapter.</summary>
public sealed class MongoProviderFactory : IStorageProviderFactory
{
    public IStorageProviderConnection Create(string connectionString) =>
        new MongoStoreConnection(new MongoDbProviderFactory().Create(connectionString));
}

internal sealed class MongoStoreConnection(IMongoProviderConnection inner) : IStorageProviderConnection, IQueryAdmissionProviderConnection
{
    /// <summary>
    /// MongoDB has no bound-parameter budget — its real bound is the 16 MB command document. Keep
    /// the keyed batch count effectively unbounded and reserve a conservative payload budget below
    /// that BSON limit. Ordinary membership predicates retain the renderer's portable 1,000-value
    /// limit so admission and rendering cannot disagree.
    /// </summary>
    public QueryAdmissionProfile QueryAdmission { get; } = new()
    {
        MaximumParameters = int.MaxValue,
        MaximumInValues = QueryRenderOptions.Default.InValueLimit,
        MaximumBatchReadKeys = int.MaxValue,
        MaximumBatchReadPayloadBytes = 15L * 1024 * 1024
    };

    public IProviderCatalog Catalog { get; } = new MongoStoreCatalog(inner.Catalog);

    public ISchemaCoordinator Schema { get; } = new MongoStoreSchema(inner.Schema);

    public IReadOnlyList<CapabilityDescriptor> Capabilities
    {
        get
        {
            var descriptors = BatchWriteCapabilities.ForProvider(
                "MongoDB", nativeBatch: true,
                exactOutcomeCost: "one FindOneAndUpdate per coalesced row",
                batchCost: "uses unordered BulkWrite for aggregate commits",
                exactAppendOutcomes: true,
                durableHighWaterInspection: true,
                exactRetention: true,
                atomicCommit: inner.ProviderSequenceFit is ProviderFit.Supported,
                compareAndDelete: inner.ProviderSequenceFit is ProviderFit.Supported,
                setMutation: "Updates or deletes every document matching an index-covered portable predicate on MongoDB with one updateMany/deleteMany, and reports matchedCount/deletedCount. Unlike the relational providers, a multi-document updateMany/deleteMany is atomic only when it runs inside a transaction: open a unit of work on a transaction-capable deployment when the whole set must apply or none of it.");
            return descriptors
                .Where(descriptor => descriptor.Id != BatchWriteCapabilities.AppendIdempotency ||
                                     inner.ProviderSequenceFit is ProviderFit.Supported)
                .Where(descriptor => descriptor.Id != BatchWriteCapabilities.ExactAppendOutcomes ||
                                     inner.ProviderSequenceFit is ProviderFit.Supported)
                .Where(descriptor => descriptor.Id != BatchWriteCapabilities.DurableHighWaterInspection ||
                                     inner.ProviderSequenceFit is ProviderFit.Supported)
                .Where(descriptor => descriptor.Id != BatchWriteCapabilities.ExactRetention ||
                                     inner.ProviderSequenceFit is ProviderFit.Supported)
                .Where(descriptor => descriptor.Id != BatchWriteCapabilities.ProviderSequence ||
                                     inner.ProviderSequenceFit is ProviderFit.Supported)
                .Select(descriptor => descriptor.Id == BatchWriteCapabilities.ProviderSequence
                    ? MongoCapabilities.ProviderSequenceDescriptor
                    : descriptor)
                .ToArray();
        }
    }

    public IStorageSession OpenSession(StorageUnit unit, StorageAccess access, IProviderCommandObserver? observer = null)
    {
        var session = inner.OpenSession(unit, ToNative(access), observer);
        return inner.ProviderSequenceFit is ProviderFit.Supported
            ? new MongoExactStoreSession(session, commandObserver: observer, providerConnection: this)
            : new MongoStoreSession(session, commandObserver: observer, providerConnection: this);
    }

    public IOwnedStorageSession OpenOwnedSession(
        StorageUnit unit,
        StorageAccess access,
        IProviderCommandObserver? observer = null)
    {
        var session = inner.OpenSession(unit, ToNative(access), observer);
        return inner.ProviderSequenceFit is ProviderFit.Supported
            ? new MongoExactStoreSession(session, observer)
            : new MongoStoreSession(session, commandObserver: observer);
    }

    public IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units) =>
        BeginUnitOfWork(access, BatchWriteOptions.Default, units);

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
        StorageAccessValidation.EnsureUnitOfWork(access);
        return new MongoStoreUnitOfWork(
            inner.BeginUnitOfWork(ToNative(access), observer, units),
            options,
            inner.ProviderSequenceFit is ProviderFit.Supported,
            observer,
            this);
    }

    public void Dispose() => inner.Dispose();

    private static MongoStorageAccess ToNative(StorageAccess access) => access.Kind switch
    {
        StorageAccessKind.Global => MongoStorageAccess.Global,
        StorageAccessKind.Scoped => MongoStorageAccess.Scoped(access.Scope ?? throw new InvalidOperationException(
            "A scoped access context requires a scope.")),
        StorageAccessKind.PrivilegedAcrossScopes => MongoStorageAccess.PrivilegedAcrossScopes(
            access.Audit ?? throw new InvalidOperationException(
                "Privileged across-scope access requires audit metadata.")),
        _ => throw new ArgumentOutOfRangeException(nameof(access.Kind), access.Kind, null)
    };
}

internal sealed class MongoStoreCatalog(IMongoProviderCatalog inner) : IProviderCatalog
{
    public IReadOnlyList<ProviderIndex> ReadIndexes(StorageUnitId storageUnitId) =>
        inner.ReadIndexes(storageUnitId).Select(index => new ProviderIndex(
            index.Name,
            index.Columns.Select(column => new ProviderIndexColumn(column.Column, column.Direction)).ToArray(),
            index.IsUnique,
            index.MissingValues,
            index.SchemaVersion)).ToArray();
}

internal sealed class MongoStoreSchema(IMongoSchemaCoordinator inner) : ISchemaCoordinator
{
    public SchemaDiff Diff(StorageUnit desired) => ToStore(inner.Diff(desired));

    public SchemaApplyResult Apply(StorageUnit desired)
    {
        var result = inner.Apply(desired);
        return new SchemaApplyResult(ToStore(result.Diff), result.Applied);
    }

    private static SchemaDiff ToStore(MongoSchemaDiff diff) => new(diff.Changes.Select(change =>
        new SchemaChange((SchemaChangeKind)change.Kind, change.Identity)).ToArray());
}

internal class MongoStoreSession(
    IMongoStorageSession inner,
    Action<StorageKey>? beforeRead = null,
    IProviderCommandObserver? commandObserver = null,
    IStorageProviderConnection? providerConnection = null) : IOwnedStorageSession, IStorageSession, IProviderBoundStorageSession, IConcurrencyStorageSession, IBatchedStorageSession, IRetentionStorageSession, IPrivilegedCrossScopeQuerySession, ISetMutationStorageSession
{
    private bool released;

    /// <summary>
    /// MongoDB's driver is thread-safe and a session here holds no exclusive connection, so releasing one
    /// only closes it. The capability exists so a consumer can use one session lifetime model across every
    /// provider rather than special-casing this one.
    /// </summary>
    public void Dispose() => released = true;

    public ValueTask DisposeAsync()
    {
        released = true;
        return ValueTask.CompletedTask;
    }

    public StorageUnit Unit => inner.Unit;

    IStorageProviderConnection? IProviderBoundStorageSession.ProviderConnection => providerConnection;

    public StorageAccess Access => inner.Access.IsPrivilegedAcrossScopes
        ? StorageAccess.PrivilegedAcrossScopes(inner.Access.Audit ?? throw new InvalidOperationException(
            "A privileged provider session requires audit metadata."))
        : inner.Access.Policy == ScopePolicy.Global
            ? StorageAccess.Global
            : StorageAccess.Scoped(inner.Access.Scope ?? throw new InvalidOperationException(
                "A scoped provider session requires a scope."));

    public StoredEntry? Read(StorageKey key)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "read");
        beforeRead?.Invoke(key);
        return ToStore(inner.Read(new MongoStorageKey(key.Values)));
    }

    public async ValueTask<StoredEntry?> ReadAsync(StorageKey key, CancellationToken cancellationToken = default)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "read");
        beforeRead?.Invoke(key);
        return ToStore(await inner.ReadAsync(new MongoStorageKey(key.Values), cancellationToken).ConfigureAwait(false));
    }

    public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null)
    {
        StorageAccessValidation.EnsureOrdinaryQuery(Access);
        return inner.Query(request, options);
    }

    public ValueTask<QueryMaterializedResult> QueryAsync(
        QueryRequest request,
        QueryRenderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        StorageAccessValidation.EnsureOrdinaryQuery(Access);
        return inner.QueryAsync(request, options, cancellationToken);
    }

    public CrossScopeQueryResult QueryAcrossScopes(QueryRequest request, QueryRenderOptions? options = null) =>
        inner.QueryAcrossScopes(request, options);

    public ValueTask<CrossScopeQueryResult> QueryAcrossScopesAsync(
        QueryRequest request,
        QueryRenderOptions? options = null,
        CancellationToken cancellationToken = default) =>
        inner.QueryAcrossScopesAsync(request, options, cancellationToken);

    public AggregationResult Aggregate(AggregationQuery query)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "aggregate");
        return inner.Aggregate(query);
    }

    public ValueTask<AggregationResult> AggregateAsync(
        AggregationQuery query,
        CancellationToken cancellationToken = default)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "aggregate");
        return inner.AggregateAsync(query, cancellationToken);
    }

    public WriteOutcome Insert(StorageValues values, WriteOptions? options = null)
    {
        ValidateInsert(values, options);
        return ToStore(inner.Insert(new MongoStorageValues(values.Values), ToNative(options)));
    }

    public async ValueTask<WriteOutcome> InsertAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateInsert(values, options);
        return ToStore(await inner.InsertAsync(
            new MongoStorageValues(values.Values), ToNative(options), cancellationToken).ConfigureAwait(false));
    }

    private void ValidateInsert(StorageValues values, WriteOptions? options)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "write");
        WritePreconditionValidator.ValidateWrittenValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.Insert, options);
    }

    public WriteOutcome Update(StorageValues values, WriteOptions? options = null)
    {
        ValidateUpdate(values, options);
        return ToStore(inner.Update(new MongoStorageValues(values.Values), ToNative(options)));
    }

    public async ValueTask<WriteOutcome> UpdateAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateUpdate(values, options);
        return ToStore(await inner.UpdateAsync(
            new MongoStorageValues(values.Values), ToNative(options), cancellationToken).ConfigureAwait(false));
    }

    private void ValidateUpdate(StorageValues values, WriteOptions? options)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "write");
        WritePreconditionValidator.ValidateWrittenValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.Update, options);
    }

    public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null)
    {
        ValidateUpsert(values, options);
        return ToStore(inner.Upsert(new MongoStorageValues(values.Values), ToNative(options)));
    }

    public async ValueTask<WriteOutcome> UpsertAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateUpsert(values, options);
        return ToStore(await inner.UpsertAsync(
            new MongoStorageValues(values.Values), ToNative(options), cancellationToken).ConfigureAwait(false));
    }

    private void ValidateUpsert(StorageValues values, WriteOptions? options)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "write");
        WritePreconditionValidator.ValidateWrittenValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.Upsert, options);
    }

    public WriteOutcome ConditionalUpsert(StorageValues values, WriteOptions? options = null)
    {
        ValidateConditionalUpsert(values, options);
        return ToStore(inner.ConditionalUpsert(new MongoStorageValues(values.Values), ToNative(options)), values, options);
    }

    public async ValueTask<WriteOutcome> ConditionalUpsertAsync(
        StorageValues values,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConditionalUpsert(values, options);
        return ToStore(await inner.ConditionalUpsertAsync(
            new MongoStorageValues(values.Values), ToNative(options), cancellationToken).ConfigureAwait(false),
            values, options);
    }

    private void ValidateConditionalUpsert(StorageValues values, WriteOptions? options)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "write");
        WritePreconditionValidator.ValidateWrittenValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.ConditionalUpsert, options);
    }

    public WriteOutcome Delete(StorageKey key, WriteOptions? options = null)
    {
        ValidateDelete(options);
        return ToStore(inner.Delete(new MongoStorageKey(key.Values), ToNative(options)));
    }

    public async ValueTask<WriteOutcome> DeleteAsync(
        StorageKey key,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateDelete(options);
        return ToStore(await inner.DeleteAsync(
            new MongoStorageKey(key.Values), ToNative(options), cancellationToken).ConfigureAwait(false));
    }

    private void ValidateDelete(WriteOptions? options)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "write");
        WritePreconditionValidator.Validate(Unit, WriteOperation.Delete, options);
    }

    public SetMutationResult UpdateWhere(Predicate where, IReadOnlyDictionary<string, object?> assignments) =>
        SetMutation.UpdateWhere(where, assignments);

    public ValueTask<SetMutationResult> UpdateWhereAsync(
        Predicate where,
        IReadOnlyDictionary<string, object?> assignments,
        CancellationToken cancellationToken = default) =>
        SetMutation.UpdateWhereAsync(where, assignments, cancellationToken);

    public SetMutationResult DeleteWhere(Predicate where) => SetMutation.DeleteWhere(where);

    public ValueTask<SetMutationResult> DeleteWhereAsync(
        Predicate where,
        CancellationToken cancellationToken = default) =>
        SetMutation.DeleteWhereAsync(where, cancellationToken);

    /// <summary>
    /// Total by construction: this adapter is built only by <see cref="MongoProviderFactory"/> over
    /// the MongoDB provider connection, whose sessions implement set-based mutation. Admission,
    /// capability and access are all decided before a call reaches here.
    /// </summary>
    private ISetMutationStorageSession SetMutation => (ISetMutationStorageSession)inner;

    public RetentionResult ApplyRetention(RetentionExecutionOptions? options = null)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "retention");
        if (inner is IRetentionStorageSession native)
            return native.ApplyRetention(options);
        return RetentionSessionExtensions.ApplyRetention(this, options);
    }

    public ValueTask<RetentionResult> ApplyRetentionAsync(RetentionExecutionOptions? options = null)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "retention");
        return inner is IRetentionStorageSession native
            ? native.ApplyRetentionAsync(options)
            : RetentionSessionExtensions.ApplyRetentionAsync(this, options);
    }

    public WriteOutcome Append(OperationId operationId, IReadOnlyList<StorageValues> values)
    {
        var native = ValidateAppend(operationId, values);
        return ToStore(inner.Append(operationId, native));
    }

    public async ValueTask<WriteOutcome> AppendAsync(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        CancellationToken cancellationToken = default)
    {
        var native = ValidateAppend(operationId, values);
        return ToStore(await inner.AppendAsync(operationId, native, cancellationToken).ConfigureAwait(false));
    }

    private MongoStorageValues[] ValidateAppend(OperationId operationId, IReadOnlyList<StorageValues> values)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "append");
        _ = IdempotencyRules.RequireDeclaration(Unit);
        IdempotencyRules.ValidateOperation(Unit, operationId, values);
        return values.Select(value => new MongoStorageValues(value.Values)).ToArray();
    }

    public IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes)
        => ApplyBatch(writes, exactOutcomes: false);

    public async ValueTask<IReadOnlyList<RowWriteOutcome>> ApplyBatchAsync(
        IReadOnlyList<RowWrite> writes,
        bool exactOutcomes,
        CancellationToken cancellationToken = default)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "write");
        ArgumentNullException.ThrowIfNull(writes);
        if (inner is IBatchedStorageSession native)
            return await native.ApplyBatchAsync(writes, exactOutcomes, cancellationToken).ConfigureAwait(false);
        var outcomes = new List<RowWriteOutcome>(writes.Count);
        foreach (var write in writes)
        {
            outcomes.Add(new RowWriteOutcome(write, await (write.Mode switch
            {
                RowWriteMode.Insert => InsertAsync(write.Values!, write.Options, cancellationToken),
                RowWriteMode.Update => UpdateAsync(write.Values!, write.Options, cancellationToken),
                RowWriteMode.Upsert when write.Options.Precondition.Kind == WritePreconditionKind.IfVersion =>
                    ConditionalUpsertAsync(write.Values!, write.Options, cancellationToken),
                RowWriteMode.Upsert => UpsertAsync(write.Values!, write.Options, cancellationToken),
                RowWriteMode.ConditionalUpsert => ConditionalUpsertAsync(write.Values!, write.Options, cancellationToken),
                RowWriteMode.Delete => DeleteAsync(write.Key!, write.Options, cancellationToken),
                RowWriteMode.CompareAndDelete => this is ICompareAndDeleteStorageSession compareAndDelete
                    ? compareAndDelete.CompareAndDeleteAsync(write.Key!, write.ExpectedValues, write.Options, cancellationToken)
                    : throw new NotSupportedException("The provider session does not support compare-and-delete."),
                _ => throw new ArgumentOutOfRangeException(nameof(writes), write.Mode, null)
            }).ConfigureAwait(false)));
        }
        return outcomes;
    }

    public IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes, bool exactOutcomes)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "write");
        ArgumentNullException.ThrowIfNull(writes);
        if (inner is IBatchedStorageSession native)
            return native.ApplyBatch(writes, exactOutcomes);
        return writes.Select(write => new RowWriteOutcome(write, write.Mode switch
        {
            RowWriteMode.Insert => Insert(write.Values!, write.Options),
            RowWriteMode.Update => Update(write.Values!, write.Options),
            RowWriteMode.Upsert when write.Options.Precondition.Kind == WritePreconditionKind.IfVersion => ConditionalUpsert(write.Values!, write.Options),
            RowWriteMode.Upsert => Upsert(write.Values!, write.Options),
            RowWriteMode.ConditionalUpsert => ConditionalUpsert(write.Values!, write.Options),
            RowWriteMode.Delete => Delete(write.Key!, write.Options),
            RowWriteMode.CompareAndDelete => this is ICompareAndDeleteStorageSession compareAndDelete
                ? compareAndDelete.CompareAndDelete(write.Key!, write.ExpectedValues, write.Options)
                : throw new NotSupportedException("The provider session does not support compare-and-delete."),
            _ => throw new ArgumentOutOfRangeException(nameof(write.Mode), write.Mode, null)
        })).ToArray();
    }

    private static MongoWriteOptions? ToNative(WriteOptions? options) => options is null
        ? null
        : new MongoWriteOptions { Precondition = options.Precondition };

    protected static WriteOutcome ToStore(MongoWriteOutcome result) =>
        new((WriteOutcomeStatus)result.Status, result.Version, result.UniqueIndexName,
            result.GeneratedValues);

    private WriteOutcome ToStore(MongoWriteOutcome result, StorageValues values, WriteOptions? options)
    {
        if (result.Status != MongoWriteOutcomeStatus.ConcurrencyConflict)
            return ToStore(result);

        return WriteOutcome.Deferred(
            WriteOutcomeStatus.ConcurrencyConflict,
            result.Version,
            () =>
            {
                commandObserver?.Observe(new ProviderCommandEvent(
                    "mongodb.write-probe",
                    "MongoDB.FindOne(identity)",
                    ProviderCommandKind.Write,
                    IsProbe: true));
                var key = new MongoStorageKey(values.Values);
                var existing = inner.Read(key);
                return existing is null
                    ? new WriteOutcomeDetail(WriteOutcomeStatus.NotFound)
                    : new WriteOutcomeDetail(WriteOutcomeStatus.ConcurrencyConflict, existing.Version);
            });
    }

    private static StoredEntry? ToStore(MongoStoredEntry? entry) => entry is null
        ? null
        : new StoredEntry(new StorageValues(entry.Values.Values), entry.Version);
}

internal sealed class MongoExactStoreSession : MongoStoreSession, IExactAppendStorageSession, ICompareAndDeleteStorageSession, IStorageInspectionSession, IExactRetentionStorageSession
{
    private readonly IMongoStorageSession exactInner;

    internal MongoExactStoreSession(
        IMongoStorageSession inner,
        IProviderCommandObserver? commandObserver = null,
        IStorageProviderConnection? providerConnection = null)
        : base(inner, commandObserver: commandObserver, providerConnection: providerConnection) => exactInner = inner;

    public AppendOutcomeReport AppendWithOutcomes(OperationId operationId, IReadOnlyList<StorageValues> values) =>
        ToStore(RequireExactAppend(values).AppendWithOutcomes(
            operationId,
            values.Select(value => new MongoStorageValues(value.Values)).ToArray()));

    public async ValueTask<AppendOutcomeReport> AppendWithOutcomesAsync(
        OperationId operationId,
        IReadOnlyList<StorageValues> values,
        CancellationToken cancellationToken = default) =>
        ToStore(await RequireExactAppend(values).AppendWithOutcomesAsync(
            operationId,
            values.Select(value => new MongoStorageValues(value.Values)).ToArray(),
            cancellationToken).ConfigureAwait(false));

    private IMongoExactAppendStorageSession RequireExactAppend(IReadOnlyList<StorageValues> values)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "append");
        ArgumentNullException.ThrowIfNull(values);
        return exactInner as IMongoExactAppendStorageSession ?? throw new NotSupportedException(
            "GW-APPEND-003: this MongoDB deployment does not advertise exact append outcomes; use a transaction-capable deployment and inspect IExactAppendStorageSession before using AppendWithOutcomes.");
    }

    private static AppendOutcomeReport ToStore(MongoAppendOutcomeReport result) =>
        new((WriteOutcomeStatus)result.Status, result.Outcomes.Select(ToStore).ToArray());

    public WriteOutcome CompareAndDelete(
        StorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        WriteOptions? options = null)
    {
        var (compareAndDelete, canonicalKey, validated) = PrepareCompareAndDelete(key, expectedValues, options);
        return ToStore(compareAndDelete.CompareAndDelete(
            new MongoStorageKey(canonicalKey.Values),
            validated,
            new MongoWriteOptions { Precondition = options?.Precondition ?? WritePrecondition.Unconditional }));
    }

    public async ValueTask<WriteOutcome> CompareAndDeleteAsync(
        StorageKey key,
        IReadOnlyDictionary<string, object?> expectedValues,
        WriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (compareAndDelete, canonicalKey, validated) = PrepareCompareAndDelete(key, expectedValues, options);
        return ToStore(await compareAndDelete.CompareAndDeleteAsync(
            new MongoStorageKey(canonicalKey.Values),
            validated,
            new MongoWriteOptions { Precondition = options?.Precondition ?? WritePrecondition.Unconditional },
            cancellationToken).ConfigureAwait(false));
    }

    private (IMongoCompareAndDeleteStorageSession Session, StorageKey Key, IReadOnlyDictionary<string, object?> Expected)
        PrepareCompareAndDelete(
            StorageKey key,
            IReadOnlyDictionary<string, object?> expectedValues,
            WriteOptions? options)
    {
        StorageAccessValidation.EnsurePointOperation(Access, "compare-and-delete");
        var canonicalKey = CompareAndDeleteValidation.CanonicalizeKey(Unit, key);
        var validated = CompareAndDeleteValidation.Validate(Unit, canonicalKey, expectedValues, options);
        if (exactInner is not IMongoCompareAndDeleteStorageSession compareAndDelete)
            throw new NotSupportedException(
                "GW-COMPARE-DELETE-001: this MongoDB deployment does not advertise transactional compare-and-delete.");
        return (compareAndDelete, canonicalKey, validated);
    }

    public StorageInspection Inspect() => RequireInspection().Inspect();

    public ValueTask<StorageInspection> InspectAsync(CancellationToken cancellationToken = default) =>
        RequireInspection().InspectAsync(cancellationToken);

    private IStorageInspectionSession RequireInspection()
    {
        StorageAccessValidation.EnsurePointOperation(Access, "inspect");
        StorageInspectionSessionExtensions.EnsureProviderSequence(Unit);
        return exactInner as IStorageInspectionSession ?? throw new NotSupportedException(
            "GW-INSPECT-001: this provider session does not advertise durable high-water inspection.");
    }

    public RetentionOperationResult ApplyRetention(OperationId operationId, RetentionExecutionOptions? options = null) =>
        RequireExactRetention().ApplyRetention(operationId, options);

    public ValueTask<RetentionOperationResult> ApplyRetentionAsync(
        OperationId operationId,
        RetentionExecutionOptions? options = null) =>
        RequireExactRetention().ApplyRetentionAsync(operationId, options);

    private IExactRetentionStorageSession RequireExactRetention()
    {
        StorageAccessValidation.EnsurePointOperation(Access, "retention");
        return exactInner as IExactRetentionStorageSession ?? throw new NotSupportedException(
            "GW-RETENTION-003: this provider session does not advertise exact retention operations.");
    }
}

internal sealed class MongoStoreUnitOfWork : IUnitOfWork
{
    private readonly IMongoUnitOfWork inner;
    private readonly BatchContext batch;
    private readonly bool exactAvailable;
    private readonly IProviderCommandObserver? commandObserver;
    private readonly IStorageProviderConnection? providerConnection;
    private readonly Dictionary<StorageUnitId, BatchStorageSession> sessions = [];
    private bool terminal;

    internal MongoStoreUnitOfWork(
        IMongoUnitOfWork inner,
        BatchWriteOptions options,
        bool exactAvailable,
        IProviderCommandObserver? commandObserver = null,
        IStorageProviderConnection? providerConnection = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        batch = new BatchContext(options ?? throw new ArgumentNullException(nameof(options)));
        this.exactAvailable = exactAvailable;
        this.commandObserver = commandObserver;
        this.providerConnection = providerConnection;
    }

    public IStorageSession OpenSession(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ThrowIfTerminal();
        if (sessions.TryGetValue(unit.Id, out var existing))
            return existing;
        var native = inner.OpenSession(unit);
        var store = exactAvailable
            ? new MongoExactStoreSession(native, commandObserver: commandObserver, providerConnection: providerConnection)
            : new MongoStoreSession(native, commandObserver: commandObserver, providerConnection: providerConnection);
        var session = BatchStorageSession.Create(store, batch);
        sessions.Add(unit.Id, session);
        batch.Register(session);
        return session;
    }

    public void Stage(RowWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ThrowIfTerminal();
        EnsureNativeActive();
        if (!sessions.ContainsKey(write.Unit.Id))
            _ = OpenSession(write.Unit);
        batch.Stage(write);
        if (batch.ReachedCap)
            batch.FlushAll();
    }

    public BatchWriteSummary Commit() =>
        BatchWriteSummary.FromOutcomes(CompleteCommit(isAsync: false, CancellationToken.None).GetAwaiter().GetResult());

    public async ValueTask<BatchWriteSummary> CommitAsync(CancellationToken cancellationToken = default) =>
        BatchWriteSummary.FromOutcomes(await CompleteCommit(isAsync: true, cancellationToken).ConfigureAwait(false));

    public BatchWriteReport CommitWithOutcomes()
    {
        ThrowIfTerminal();
        batch.RequireExactOutcomes();
        return new BatchWriteReport(CompleteCommit(isAsync: false, CancellationToken.None).GetAwaiter().GetResult());
    }

    public async ValueTask<BatchWriteReport> CommitWithOutcomesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfTerminal();
        batch.RequireExactOutcomes();
        return new BatchWriteReport(await CompleteCommit(isAsync: true, cancellationToken).ConfigureAwait(false));
    }

    private async ValueTask<IReadOnlyList<RowWriteOutcome>> CompleteCommit(bool isAsync, CancellationToken cancellationToken)
    {
        ThrowIfTerminal();
        EnsureNativeActive();
        try
        {
            if (isAsync)
            {
                await batch.FlushAllAsync(cancellationToken).ConfigureAwait(false);
                await inner.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                batch.FlushAll();
                inner.Commit();
            }
            terminal = true;
            return batch.DrainCompleted();
        }
        catch (Exception failure)
        {
            WriteFailureCleanup.Run(failure, () =>
            {
                try
                {
                    // A native unit that already ended on its own failed commit must not be rolled
                    // back again: the rollback would throw and hide the failure the caller needs.
                    if (inner is not IMongoUnitOfWorkState state || state.IsActive)
                        inner.Rollback();
                }
                finally { terminal = true; }
            });
            throw;
        }
    }

    public void Rollback()
    {
        ThrowIfTerminal();
        if (inner is IMongoUnitOfWorkState state && !state.IsActive)
        {
            terminal = true;
            return;
        }
        try { inner.Rollback(); }
        finally { terminal = true; }
    }

    public void Dispose()
    {
        if (!terminal && (inner is not IMongoUnitOfWorkState state || state.IsActive))
            Rollback();
        else
            terminal = true;
        inner.Dispose();
    }

    private void EnsureNativeActive()
    {
        if (inner is IMongoUnitOfWorkState state)
            state.EnsureActive();
    }

    private void ThrowIfTerminal()
    {
        if (terminal)
            throw new InvalidOperationException("The unit of work is already terminal.");
    }

}
