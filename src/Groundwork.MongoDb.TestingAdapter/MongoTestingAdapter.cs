using Groundwork.Kernel;
using Groundwork.MongoDb;
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

    public IStorageSession OpenSession(StorageUnit unit, StorageAccess access) =>
        new MongoTestingSession(inner.OpenSession(unit, ToNative(access)));

    public IUnitOfWork BeginUnitOfWork(StorageAccess access, params StorageUnit[] units) =>
        new MongoTestingUnitOfWork(inner.BeginUnitOfWork(ToNative(access), units));

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

internal sealed class MongoTestingSession(IMongoStorageSession inner) : IStorageSession, IConcurrencyStorageSession
{
    public StorageUnit Unit => inner.Unit;

    public StorageAccess Access => inner.Access.Policy == ScopePolicy.Global
        ? StorageAccess.Global
        : StorageAccess.Scoped(inner.Access.Scope ?? throw new InvalidOperationException(
            "A scoped provider session requires a scope."));

    public StoredEntry? Read(StorageKey key) =>
        ToTesting(inner.Read(new MongoStorageKey(key.Values)));

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

    private static MongoWriteOptions? ToNative(WriteOptions? options) => options is null
        ? null
        : new MongoWriteOptions { ExpectedVersion = options.ExpectedVersion, Observer = options.Observer };

    private static WriteOutcome ToTesting(MongoWriteOutcome result) =>
        new((WriteOutcomeStatus)result.Status, result.Version, result.UniqueIndexName);

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

internal sealed class MongoTestingUnitOfWork(IMongoUnitOfWork inner) : IUnitOfWork
{
    public IStorageSession OpenSession(StorageUnit unit) =>
        new MongoTestingSession(inner.OpenSession(unit));

    public void Commit() => inner.Commit();

    public void Rollback() => inner.Rollback();

    public void Dispose() => inner.Dispose();
}
