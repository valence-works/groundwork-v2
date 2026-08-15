using Groundwork.MongoDb;
using Groundwork.Records;
using Groundwork.Testing;
using Groundwork.Query.Model;
using KernelStorageUnit = Groundwork.Kernel.StorageUnit;

namespace Groundwork.Records.TestingAdapter;

/// <summary>
/// Opens a typed Records table on the public provider connection contracts. This companion
/// package keeps provider references out of <c>Groundwork.Records</c> itself.
/// </summary>
public static class RecordTableConnectionExtensions
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
            connection.OpenSession(table.Definition, access)));
    }

    public static RecordTableSession<T> Open<T>(
        this RecordTable<T> table,
        IMongoProviderConnection connection) =>
        Open(table, connection, MongoStorageAccess.Global);

    public static RecordTableSession<T> Open<T>(
        this RecordTable<T> table,
        IMongoProviderConnection connection,
        MongoStorageAccess access)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(access);
        return table.Open(new MongoSessionRecordStore(
            connection.OpenSession(table.Definition, access)));
    }
}

internal sealed class StorageSessionRecordStore(IStorageSession session) : IRecordStore
{
    public RecordWriteResult Insert(KernelStorageUnit unit, RowValues values, RecordWriteOptions? options = null) =>
        Convert(session.Insert(new StorageValues(values.Values), ToWriteOptions(options)));

    public RecordWriteResult Update(KernelStorageUnit unit, RowValues values, RecordWriteOptions? options = null) =>
        Convert(session.Update(new StorageValues(values.Values), ToWriteOptions(options)));

    public RecordWriteResult Upsert(KernelStorageUnit unit, RowValues values, RecordWriteOptions? options = null) =>
        Convert(session.Upsert(new StorageValues(values.Values), ToWriteOptions(options)));

    public RecordWriteResult Delete(KernelStorageUnit unit, RowValues key, RecordWriteOptions? options = null) =>
        Convert(session.Delete(new StorageKey(key.Values), ToWriteOptions(options)));

    public RecordQueryResult Query(QueryRequest request, QueryRenderOptions? options = null)
    {
        var result = session.Query(request, options);
        return new RecordQueryResult(result.Rows.Select(row => new RowValues(row)).ToArray(), result.TotalCount);
    }

    private static WriteOptions? ToWriteOptions(RecordWriteOptions? options) => options?.ExpectedVersion is { } version
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
            _ => throw new ArgumentOutOfRangeException()
        }, outcome.Version, outcome.GeneratedValues, outcome.UniqueIndexName);
}

internal sealed class MongoSessionRecordStore(IMongoStorageSession session) : IRecordStore
{
    public RecordWriteResult Insert(KernelStorageUnit unit, RowValues values, RecordWriteOptions? options = null) =>
        Convert(session.Insert(new MongoStorageValues(values.Values), ToWriteOptions(options)));

    public RecordWriteResult Update(KernelStorageUnit unit, RowValues values, RecordWriteOptions? options = null) =>
        Convert(session.Update(new MongoStorageValues(values.Values), ToWriteOptions(options)));

    public RecordWriteResult Upsert(KernelStorageUnit unit, RowValues values, RecordWriteOptions? options = null) =>
        Convert(session.Upsert(new MongoStorageValues(values.Values), ToWriteOptions(options)));

    public RecordWriteResult Delete(KernelStorageUnit unit, RowValues key, RecordWriteOptions? options = null) =>
        Convert(session.Delete(new MongoStorageKey(key.Values), ToWriteOptions(options)));

    public RecordQueryResult Query(QueryRequest request, QueryRenderOptions? options = null)
    {
        var result = session.Query(request, options);
        return new RecordQueryResult(result.Rows.Select(row => new RowValues(row)).ToArray(), result.TotalCount);
    }

    private static MongoWriteOptions? ToWriteOptions(RecordWriteOptions? options) => options?.ExpectedVersion is { } version
        ? MongoWriteOptions.IfVersion(version)
        : null;

    private static RecordWriteResult Convert(MongoWriteOutcome outcome) => new(
        outcome.Status switch
        {
            MongoWriteOutcomeStatus.Inserted => RecordWriteStatus.Inserted,
            MongoWriteOutcomeStatus.Updated => RecordWriteStatus.Updated,
            MongoWriteOutcomeStatus.Upserted => RecordWriteStatus.Upserted,
            MongoWriteOutcomeStatus.Deleted => RecordWriteStatus.Deleted,
            MongoWriteOutcomeStatus.NotFound => RecordWriteStatus.NotFound,
            MongoWriteOutcomeStatus.UniqueViolation => RecordWriteStatus.UniqueViolation,
            MongoWriteOutcomeStatus.ConcurrencyConflict => RecordWriteStatus.ConcurrencyConflict,
            _ => throw new ArgumentOutOfRangeException()
        }, outcome.Version, outcome.GeneratedValues, outcome.UniqueIndexName);
}
