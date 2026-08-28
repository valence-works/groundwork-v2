using Groundwork.Kernel;
using Groundwork.Store;
using KernelStorageUnit = Groundwork.Kernel.StorageUnit;

namespace Groundwork.Records;

/// <summary>
/// Opens a typed Records table on the production provider contract. The
/// Groundwork.Records.Store integration package owns this bridge so ordinary consumers do not
/// need a provider-specific helper.
/// </summary>
public static class RecordTableStoreExtensions
{
    public static RecordTableSession<T> Open<T>(
        this RecordTable<T> table,
        IStorageProviderConnection connection) =>
        Open(table, connection, StorageAccess.Global);

    public static RecordTableSession<T> Open<T>(
        this RecordTable<T> table,
        IStorageProviderConnection connection,
        StorageAccess access)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(access);
        return table.Open(new StorageSessionRecordStore(
            connection,
            access,
            table.Definition));
    }

    /// <summary>Begins a typed staged unit of work for one Records declaration.</summary>
    public static RecordTableStoreUnitOfWork<T> BeginUnitOfWork<T>(
        this RecordTable<T> table,
        IStorageProviderConnection connection,
        BatchWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(connection);
        return new RecordTableStoreUnitOfWork<T>(
            table,
            connection.BeginUnitOfWork(
                StorageAccess.Global,
                options ?? BatchWriteOptions.Default,
                table.Definition));
    }

    public static RecordTableStoreUnitOfWork<T> BeginUnitOfWork<T>(
        this RecordTable<T> table,
        IStorageProviderConnection connection,
        StorageAccess access,
        BatchWriteOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(access);
        return new RecordTableStoreUnitOfWork<T>(
            table,
            connection.BeginUnitOfWork(
                access,
                options ?? BatchWriteOptions.Default,
                table.Definition));
    }
}

internal sealed class StorageSessionRecordStore : IRecordStore, IRecordAggregationStore
{
    private readonly IStorageSession session;

    internal StorageSessionRecordStore(
        IStorageProviderConnection connection,
        StorageAccess access,
        Groundwork.Kernel.StorageUnit unit)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(access);
        session = connection.OpenSession(unit, access);
    }

    public RecordWriteResult Insert(Groundwork.Kernel.StorageUnit unit, RowValues values, RecordWriteOptions? options = null) =>
        Convert(session.Insert(new StorageValues(values.Values), ToWriteOptions(options)));

    public RecordWriteResult Update(Groundwork.Kernel.StorageUnit unit, RowValues values, RecordWriteOptions? options = null) =>
        Convert(session.Update(new StorageValues(values.Values), ToWriteOptions(options)));

    public RecordWriteResult Upsert(Groundwork.Kernel.StorageUnit unit, RowValues values, RecordWriteOptions? options = null) =>
        Convert(session.Upsert(new StorageValues(values.Values), ToWriteOptions(options)));

    public RecordWriteResult Delete(Groundwork.Kernel.StorageUnit unit, RowValues key, RecordWriteOptions? options = null) =>
        Convert(session.Delete(new StorageKey(key.Values), ToWriteOptions(options)));

    public RecordQueryResult Query(
        Groundwork.Query.Model.QueryRequest request,
        Groundwork.Query.Model.QueryRenderOptions? options = null)
    {
        var result = session.Query(request, options);
        return new RecordQueryResult(
            result.Rows.Select(row => new RowValues(row)).ToArray(),
            result.TotalCount);
    }

    public AggregationResult Aggregate(KernelStorageUnit unit, AggregationQuery query) =>
        session.Aggregate(query ?? throw new ArgumentNullException(nameof(query)));

    public ValueTask<AggregationResult> AggregateAsync(
        KernelStorageUnit unit,
        AggregationQuery query,
        CancellationToken cancellationToken = default) =>
        session.AggregateAsync(query ?? throw new ArgumentNullException(nameof(query)), cancellationToken);

    private static WriteOptions? ToWriteOptions(RecordWriteOptions? options) =>
        options?.ExpectedVersion is { } version
            ? WriteOptions.IfVersion(version)
            : null;

    private static RecordWriteResult Convert(WriteOutcome outcome) => new(
        outcome.Status switch
        {
            WriteOutcomeStatus.Inserted => RecordWriteStatus.Inserted,
            WriteOutcomeStatus.Updated => RecordWriteStatus.Updated,
            WriteOutcomeStatus.Upserted => RecordWriteStatus.Upserted,
            WriteOutcomeStatus.Deleted => RecordWriteStatus.Deleted,
            WriteOutcomeStatus.NotFound => RecordWriteStatus.NotFound,
            WriteOutcomeStatus.UniqueViolation => RecordWriteStatus.UniqueViolation,
            WriteOutcomeStatus.ConcurrencyConflict => RecordWriteStatus.ConcurrencyConflict,
            _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome.Status, null)
        },
        outcome.Version,
        outcome.GeneratedValues,
        outcome.UniqueIndexName);
}

/// <summary>
/// Typed staged writes for one Records declaration. This wrapper owns the underlying provider unit
/// of work and its sessions until commit, rollback, or disposal reaches a terminal state.
/// </summary>
public sealed class RecordTableStoreUnitOfWork<T> : IDisposable
{
    private readonly RecordTable<T> table;
    private readonly IUnitOfWork inner;

    internal RecordTableStoreUnitOfWork(RecordTable<T> table, IUnitOfWork inner)
    {
        this.table = table ?? throw new ArgumentNullException(nameof(table));
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public void Insert(T value, RecordWriteOptions? options = null) =>
        Stage(RowWrite.Insert(table.Definition, new StorageValues(table.ToRowValues(value).Values),
            ToWriteOptions(options)));

    public void Update(T value, RecordWriteOptions? options = null) =>
        Stage(RowWrite.Update(table.Definition, new StorageValues(table.ToRowValues(value).Values),
            ToWriteOptions(options)));

    public void Upsert(T value, RecordWriteOptions? options = null) =>
        Stage(RowWrite.Upsert(table.Definition, new StorageValues(table.ToRowValues(value).Values),
            ToWriteOptions(options)));

    public void Delete(T value, RecordWriteOptions? options = null) =>
        Stage(RowWrite.Delete(table.Definition, ToKey(value), ToWriteOptions(options)));

    public BatchWriteSummary Commit() => inner.Commit();

    public BatchWriteReport CommitWithOutcomes() => inner.CommitWithOutcomes();

    public void Rollback() => inner.Rollback();

    public void Dispose() => inner.Dispose();

    private void Stage(RowWrite write) => inner.Stage(write);

    private StorageKey ToKey(T value)
    {
        var mapped = table.ToRowValues(value);
        return new StorageKey(table.Definition.Key.Columns.ToDictionary(
            name => name,
            name => mapped.Values[name],
            StringComparer.Ordinal));
    }

    private static WriteOptions? ToWriteOptions(RecordWriteOptions? options) =>
        options?.ExpectedVersion is { } version
            ? WriteOptions.IfVersion(version)
            : null;
}
