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

    public IReadOnlyList<CapabilityDescriptor> Capabilities => BatchWriteCapabilities.All;

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
    {
        ArgumentNullException.ThrowIfNull(writes);
        if (inner is IBatchedStorageSession native)
            return native.ApplyBatch(writes);
        return writes.Select(write => new RowWriteOutcome(write, write.Mode switch
        {
            RowWriteMode.Insert => Insert(write.Values!, write.Options),
            RowWriteMode.Update => Update(write.Values!, write.Options),
            RowWriteMode.Upsert => Upsert(write.Values!, write.Options),
            RowWriteMode.Delete => Delete(write.Key!, write.Options),
            _ => throw new ArgumentOutOfRangeException(nameof(write.Mode), write.Mode, null)
        })).ToArray();
    }

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

internal sealed class MongoTestingUnitOfWork : IUnitOfWork
{
    private readonly IMongoUnitOfWork inner;
    private readonly BatchWriteOptions options;
    private readonly Dictionary<StorageUnitId, MongoTestingSession> sessions = [];
    private readonly List<RowWrite> staged = [];
    private readonly List<RowWriteOutcome> completed = [];
    private bool terminal;

    internal MongoTestingUnitOfWork(IMongoUnitOfWork inner, BatchWriteOptions options)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.MaxRowsPerFlush <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MaxRowsPerFlush));
    }

    public IStorageSession OpenSession(StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ThrowIfTerminal();
        if (sessions.TryGetValue(unit.Id, out var existing))
            return existing;
        var session = new MongoTestingSession(inner.OpenSession(unit), key => FlushFor(unit, key));
        sessions.Add(unit.Id, session);
        return session;
    }

    public void Stage(RowWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        ThrowIfTerminal();
        if (!sessions.ContainsKey(write.Unit.Id))
            _ = OpenSession(write.Unit);
        staged.Add(write);
        if (staged.Count >= options.MaxRowsPerFlush)
            FlushAll();
    }

    public void Commit() => _ = CommitWithOutcomes();

    public BatchWriteSummary CommitWithOutcomes()
    {
        ThrowIfTerminal();
        try
        {
            FlushAll();
            inner.Commit();
            terminal = true;
            return new BatchWriteSummary(completed.ToArray());
        }
        catch
        {
            try { inner.Rollback(); }
            finally { terminal = true; }
            throw;
        }
    }

    public ValueTask<BatchWriteSummary> CommitWithOutcomesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(CommitWithOutcomes());
    }

    public ValueTask<BatchWriteSummary> CommitAsync(CancellationToken cancellationToken = default) =>
        CommitWithOutcomesAsync(cancellationToken);

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

    private void FlushFor(StorageUnit unit, StorageKey key)
    {
        var writes = staged.Where(write => write.Unit.Id == unit.Id && write.Matches(key)).ToArray();
        if (writes.Length == 0)
            return;
        Flush(writes);
    }

    private void FlushAll()
    {
        if (staged.Count != 0)
            Flush(staged.ToArray());
    }

    private void Flush(IReadOnlyList<RowWrite> writes)
    {
        foreach (var group in writes.GroupBy(write => (write.Unit.Id, write.Mode, write.ColumnSet)))
        {
            var session = sessions[group.Key.Id];
            var groupWrites = group.ToArray()
                .GroupBy(write => write.Identity, StringComparer.Ordinal)
                .Select(group => group.Last())
                .ToArray();
            var groupOutcomes = session.ApplyBatch(groupWrites);
            if (groupOutcomes.Count != groupWrites.Length)
                throw new InvalidOperationException($"The provider returned {groupOutcomes.Count} outcomes for a batch of {groupWrites.Length} writes.");
            foreach (var outcome in groupOutcomes)
            {
                foreach (var original in writes.Where(item => item.Identity == outcome.Write.Identity))
                    completed.Add(new RowWriteOutcome(original, outcome.Outcome));
                if (!outcome.Outcome.Succeeded)
                    throw new BatchWriteException(
                        $"A staged row write failed ({outcome.Write.Unit.Id.Value}/{DescribeKey(outcome.Write)}: {outcome.Outcome.Status}); the unit of work must be rolled back.",
                        completed);
            }
        }
        staged.RemoveAll(write => writes.Contains(write));
    }

    private void ThrowIfTerminal()
    {
        if (terminal)
            throw new InvalidOperationException("The unit of work is already terminal.");
    }

    private static string DescribeKey(RowWrite write)
    {
        var values = write.Key?.Values ?? write.Values!.Values;
        return string.Join(",", write.Unit.Key.Columns.Select(column =>
            $"{column}={values.GetValueOrDefault(column)}"));
    }
}
