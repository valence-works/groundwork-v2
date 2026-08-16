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

internal sealed class MongoStoreConnection(IMongoProviderConnection inner) : IStorageProviderConnection
{
    public IProviderCatalog Catalog { get; } = new MongoStoreCatalog(inner.Catalog);

    public ISchemaCoordinator Schema { get; } = new MongoStoreSchema(inner.Schema);

    public IReadOnlyList<CapabilityDescriptor> Capabilities
    {
        get
        {
            var descriptors = BatchWriteCapabilities.ForProvider(
                "MongoDB", nativeBatch: true,
                exactOutcomeCost: "one FindOneAndUpdate per coalesced row",
                batchCost: "uses unordered BulkWrite for aggregate commits");
            return descriptors
                .Where(descriptor => descriptor.Id != BatchWriteCapabilities.AppendIdempotency ||
                                     inner.ProviderSequenceFit is ProviderFit.Supported)
                .Where(descriptor => descriptor.Id != BatchWriteCapabilities.ExactAppendOutcomes ||
                                     inner.ProviderSequenceFit is ProviderFit.Supported)
                .Where(descriptor => descriptor.Id != BatchWriteCapabilities.ProviderSequence ||
                                     inner.ProviderSequenceFit is ProviderFit.Supported)
                .Select(descriptor => descriptor.Id == BatchWriteCapabilities.ProviderSequence
                    ? MongoCapabilities.ProviderSequenceDescriptor
                    : descriptor)
                .ToArray();
        }
    }

    public IStorageSession OpenSession(StorageUnit unit, StorageAccess access)
    {
        var session = inner.OpenSession(unit, ToNative(access));
        return inner.ProviderSequenceFit is ProviderFit.Supported
            ? new MongoExactStoreSession(session)
            : new MongoStoreSession(session);
    }

    public IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units) =>
        BeginUnitOfWork(access, BatchWriteOptions.Default, units);

    public IUnitOfWork BeginUnitOfWork(
        StorageAccess access,
        BatchWriteOptions options,
        params StorageUnit[] units) =>
        new MongoStoreUnitOfWork(
            inner.BeginUnitOfWork(ToNative(access), units),
            options,
            inner.ProviderSequenceFit is ProviderFit.Supported);

    public void Dispose() => inner.Dispose();

    private static MongoStorageAccess ToNative(StorageAccess access) => access.Policy == ScopePolicy.Global
        ? MongoStorageAccess.Global
        : MongoStorageAccess.Scoped(access.Scope ?? throw new InvalidOperationException(
            "A scoped access context requires a scope."));
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
    Action<StorageKey>? beforeRead = null) : IStorageSession, IConcurrencyStorageSession, IBatchedStorageSession, IRetentionStorageSession
{
    public StorageUnit Unit => inner.Unit;

    public StorageAccess Access => inner.Access.Policy == ScopePolicy.Global
        ? StorageAccess.Global
        : StorageAccess.Scoped(inner.Access.Scope ?? throw new InvalidOperationException(
            "A scoped provider session requires a scope."));

    public StoredEntry? Read(StorageKey key)
    {
        beforeRead?.Invoke(key);
        return ToStore(inner.Read(new MongoStorageKey(key.Values)));
    }

    public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null) =>
        inner.Query(request, options);

    public AggregationResult Aggregate(AggregationQuery query) =>
        inner.Aggregate(query);

    public WriteOutcome Insert(StorageValues values, WriteOptions? options = null)
    {
        WritePreconditionValidator.ValidateSystemOwnedValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.Insert, options);
        return ToStore(inner.Insert(new MongoStorageValues(values.Values), ToNative(options)));
    }

    public WriteOutcome Update(StorageValues values, WriteOptions? options = null)
    {
        WritePreconditionValidator.ValidateSystemOwnedValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.Update, options);
        return ToStore(inner.Update(new MongoStorageValues(values.Values), ToNative(options)));
    }

    public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null)
    {
        WritePreconditionValidator.ValidateSystemOwnedValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.Upsert, options);
        return ToStore(inner.Upsert(new MongoStorageValues(values.Values), ToNative(options)));
    }

    public WriteOutcome ConditionalUpsert(StorageValues values, WriteOptions? options = null)
    {
        WritePreconditionValidator.ValidateSystemOwnedValues(Unit, values.Values);
        WritePreconditionValidator.Validate(Unit, WriteOperation.ConditionalUpsert, options);
        return ToStore(inner.ConditionalUpsert(new MongoStorageValues(values.Values), ToNative(options)), values, options);
    }

    public WriteOutcome Delete(StorageKey key, WriteOptions? options = null)
    {
        WritePreconditionValidator.Validate(Unit, WriteOperation.Delete, options);
        return ToStore(inner.Delete(new MongoStorageKey(key.Values), ToNative(options)));
    }

    public RetentionResult ApplyRetention(RetentionExecutionOptions? options = null)
    {
        if (inner is IRetentionStorageSession native)
            return native.ApplyRetention(options);
        return RetentionSessionExtensions.ApplyRetention(this, options);
    }

    public WriteOutcome Append(OperationId operationId, IReadOnlyList<StorageValues> values)
    {
        var declaration = IdempotencyRules.RequireDeclaration(Unit);
        IdempotencyRules.ValidateOperation(Unit, operationId, values);
        var native = values.Select(value => new MongoStorageValues(value.Values)).ToArray();
        return ToStore(inner.Append(operationId, native));
    }

    public IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes)
        => ApplyBatch(writes, exactOutcomes: false);

    public IReadOnlyList<RowWriteOutcome> ApplyBatch(IReadOnlyList<RowWrite> writes, bool exactOutcomes)
    {
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
            _ => throw new ArgumentOutOfRangeException(nameof(write.Mode), write.Mode, null)
        })).ToArray();
    }

    private static MongoWriteOptions? ToNative(WriteOptions? options) => options is null
        ? null
        : new MongoWriteOptions { Precondition = options.Precondition, Observer = options.Observer };

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
                options?.Observer?.Observe(new WritePathEvent(
                    "mongodb.write-probe",
                    "MongoDB.FindOne(identity)",
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

internal sealed class MongoExactStoreSession : MongoStoreSession, IExactAppendStorageSession
{
    private readonly IMongoStorageSession exactInner;

    internal MongoExactStoreSession(IMongoStorageSession inner)
        : base(inner) => exactInner = inner;

    public AppendOutcomeReport AppendWithOutcomes(OperationId operationId, IReadOnlyList<StorageValues> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (exactInner is not IMongoExactAppendStorageSession exact)
            throw new NotSupportedException(
                "GW-APPEND-003: this MongoDB deployment does not advertise exact append outcomes; use a transaction-capable deployment and inspect IExactAppendStorageSession before using AppendWithOutcomes.");
        var native = values.Select(value => new MongoStorageValues(value.Values)).ToArray();
        var result = exact.AppendWithOutcomes(operationId, native);
        return new AppendOutcomeReport(
            (WriteOutcomeStatus)result.Status,
            result.Outcomes.Select(ToStore).ToArray());
    }
}

internal sealed class MongoStoreUnitOfWork : IUnitOfWork
{
    private readonly IMongoUnitOfWork inner;
    private readonly BatchContext batch;
    private readonly bool exactAvailable;
    private readonly Dictionary<StorageUnitId, BatchStorageSession> sessions = [];
    private bool terminal;

    internal MongoStoreUnitOfWork(IMongoUnitOfWork inner, BatchWriteOptions options, bool exactAvailable)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        batch = new BatchContext(options ?? throw new ArgumentNullException(nameof(options)));
        this.exactAvailable = exactAvailable;
    }

    public IStorageSession OpenSession(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ThrowIfTerminal();
        if (sessions.TryGetValue(unit.Id, out var existing))
            return existing;
        var native = inner.OpenSession(unit);
        var store = exactAvailable
            ? new MongoExactStoreSession(native)
            : new MongoStoreSession(native);
        var session = new BatchStorageSession(store, batch);
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
        EnsureNativeActive();
        try
        {
            batch.FlushAll();
            inner.Commit();
            terminal = true;
            return batch.DrainCompleted();
        }
        catch
        {
            try { inner.Rollback(); }
            finally { terminal = true; }
            throw;
        }
    }

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
