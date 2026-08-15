using Groundwork.Kernel;
using Groundwork.MongoDb;
using Groundwork.Query.Model;
using Groundwork.Testing;

namespace Groundwork.MongoDb.TestingAdapter;

/// <summary>Adapts the provider-native MongoDB contract to Groundwork.Testing.</summary>
public sealed class MongoDbTestingFactory : IStorageProviderFactory
{
    public IStorageProviderConnection Create(string connectionString) =>
        new MongoTestingConnection(new MongoDbProviderFactory().Create(connectionString));
}

internal sealed class MongoTestingConnection(IMongoProviderConnection inner) : IStorageProviderConnection
{
    public IProviderCatalog Catalog { get; } = new MongoTestingCatalog(inner.Catalog);

    public ISchemaCoordinator Schema { get; } = new MongoTestingSchema(inner.Schema);

    public IReadOnlyList<CapabilityDescriptor> Capabilities => BatchWriteCapabilities.ForProvider(
        "MongoDB", nativeBatch: true,
        exactOutcomeCost: "one FindOneAndUpdate per coalesced row",
        batchCost: "uses unordered BulkWrite for aggregate commits")
        .Select(descriptor => descriptor.Id == BatchWriteCapabilities.ProviderSequence
            ? MongoCapabilities.ProviderSequenceDescriptor
            : descriptor)
        .ToArray();

    public IStorageSession OpenSession(StorageUnit unit, StorageAccess access) =>
        new MongoTestingSession(inner.OpenSession(unit, ToNative(access)));

    public IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units) =>
        BeginUnitOfWork(access, BatchWriteOptions.Default, units);

    public IUnitOfWork BeginUnitOfWork(
        StorageAccess access,
        BatchWriteOptions options,
        params StorageUnit[] units) =>
        new MongoTestingUnitOfWork(inner.BeginUnitOfWork(ToNative(access), units), options);

    public void Dispose() => inner.Dispose();

    private static MongoStorageAccess ToNative(StorageAccess access) => access.Policy == ScopePolicy.Global
        ? MongoStorageAccess.Global
        : MongoStorageAccess.Scoped(access.Scope ?? throw new InvalidOperationException(
            "A scoped access context requires a scope."));
}

internal sealed class MongoTestingCatalog(IMongoProviderCatalog inner) : IProviderCatalog
{
    public IReadOnlyList<ProviderIndex> ReadIndexes(StorageUnitId storageUnitId) =>
        inner.ReadIndexes(storageUnitId).Select(index => new ProviderIndex(
            index.Name,
            index.Columns.Select(column => new ProviderIndexColumn(column.Column, column.Direction)).ToArray(),
            index.IsUnique,
            index.MissingValues,
            index.SchemaVersion)).ToArray();
}

internal sealed class MongoTestingSchema(IMongoSchemaCoordinator inner) : ISchemaCoordinator
{
    public SchemaDiff Diff(StorageUnit desired) => ToTesting(inner.Diff(desired));

    public SchemaApplyResult Apply(StorageUnit desired)
    {
        var result = inner.Apply(desired);
        return new SchemaApplyResult(ToTesting(result.Diff), result.Applied);
    }

    private static SchemaDiff ToTesting(MongoSchemaDiff diff) => new(diff.Changes.Select(change =>
        new SchemaChange((SchemaChangeKind)change.Kind, change.Identity)).ToArray());
}

internal sealed class MongoTestingSession(
    IMongoStorageSession inner,
    Action<StorageKey>? beforeRead = null) : IStorageSession, IConcurrencyStorageSession, IBatchedStorageSession
{
    public StorageUnit Unit => inner.Unit;

    public StorageAccess Access => inner.Access.Policy == ScopePolicy.Global
        ? StorageAccess.Global
        : StorageAccess.Scoped(inner.Access.Scope ?? throw new InvalidOperationException(
            "A scoped provider session requires a scope."));

    public StoredEntry? Read(StorageKey key)
    {
        beforeRead?.Invoke(key);
        return ToTesting(inner.Read(new MongoStorageKey(key.Values)));
    }

    public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null) =>
        inner.Query(request, options);

    public WriteOutcome Insert(StorageValues values, WriteOptions? options = null) =>
        ToTesting(inner.Insert(new MongoStorageValues(values.Values), ToNative(options)));

    public WriteOutcome Update(StorageValues values, WriteOptions? options = null) =>
        ToTesting(inner.Update(new MongoStorageValues(values.Values), ToNative(options)));

    public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null) =>
        ToTesting(inner.Upsert(new MongoStorageValues(values.Values), ToNative(options)));

    public WriteOutcome ConditionalUpsert(StorageValues values, WriteOptions? options = null) =>
        ToTesting(inner.ConditionalUpsert(new MongoStorageValues(values.Values), ToNative(options)), values, options);

    public WriteOutcome Delete(StorageKey key, WriteOptions? options = null) =>
        ToTesting(inner.Delete(new MongoStorageKey(key.Values), ToNative(options)));

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
            RowWriteMode.Upsert when write.Options.ExpectedVersion is not null => ConditionalUpsert(write.Values!, write.Options),
            RowWriteMode.Upsert => Upsert(write.Values!, write.Options),
            RowWriteMode.ConditionalUpsert => ConditionalUpsert(write.Values!, write.Options),
            RowWriteMode.Delete => Delete(write.Key!, write.Options),
            _ => throw new ArgumentOutOfRangeException(nameof(write.Mode), write.Mode, null)
        })).ToArray();
    }

    private static MongoWriteOptions? ToNative(WriteOptions? options) => options is null
        ? null
        : new MongoWriteOptions { ExpectedVersion = options.ExpectedVersion, Observer = options.Observer };

    private static WriteOutcome ToTesting(MongoWriteOutcome result) =>
        new((WriteOutcomeStatus)result.Status, result.Version, result.UniqueIndexName,
            result.GeneratedValues);

    private WriteOutcome ToTesting(MongoWriteOutcome result, StorageValues values, WriteOptions? options)
    {
        if (result.Status != MongoWriteOutcomeStatus.ConcurrencyConflict)
            return ToTesting(result);

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

    private static StoredEntry? ToTesting(MongoStoredEntry? entry) => entry is null
        ? null
        : new StoredEntry(new StorageValues(entry.Values.Values), entry.Version);
}

internal sealed class MongoTestingUnitOfWork : IUnitOfWork
{
    private readonly IMongoUnitOfWork inner;
    private readonly BatchContext batch;
    private readonly Dictionary<StorageUnitId, BatchStorageSession> sessions = [];
    private bool terminal;

    internal MongoTestingUnitOfWork(IMongoUnitOfWork inner, BatchWriteOptions options)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        batch = new BatchContext(options ?? throw new ArgumentNullException(nameof(options)));
    }

    public IStorageSession OpenSession(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ThrowIfTerminal();
        if (sessions.TryGetValue(unit.Id, out var existing))
            return existing;
        var session = new BatchStorageSession(new MongoTestingSession(inner.OpenSession(unit)), batch);
        sessions.Add(unit.Id, session);
        batch.Register(session);
        return session;
    }

    public void Stage(RowWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ThrowIfTerminal();
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
        try { inner.Rollback(); }
        finally { terminal = true; }
    }

    public void Dispose()
    {
        if (!terminal)
            Rollback();
        inner.Dispose();
    }

    private void ThrowIfTerminal()
    {
        if (terminal)
            throw new InvalidOperationException("The unit of work is already terminal.");
    }

}
