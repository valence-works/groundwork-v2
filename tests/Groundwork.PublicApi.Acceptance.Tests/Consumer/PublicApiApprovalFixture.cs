using Groundwork.Documents;
using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.Query.Linq;
using Groundwork.Query.Planning;
using Groundwork.Records;
using Groundwork.Store;
using Groundwork.Sqlite;
using Groundwork.Testing;

namespace Groundwork.PublicApi.Consumer;

// This is an intentionally boring compile-time approval fixture. Removing or renaming any
// supported symbol below is a review-visible API change, while the consumer remains free of
// reflection, internals, friend access, and test adapters.
internal static class PublicApiApprovalFixture
{
    public static void Touch()
    {
        _ = typeof(DocumentUnit);
        _ = typeof(DocumentReadResult<>);
        _ = typeof(PortableType);
        _ = typeof(QueryRequest);
        _ = typeof(QueryCoverageException);
        _ = typeof(RecordTable);
        _ = typeof(RecordTableStoreUnitOfWork<>);
        _ = typeof(RecordWriteOptions);
        _ = typeof(BatchWriteOptions);
        _ = typeof(RowWrite);
        _ = typeof(AppendOutcomeReport);
        _ = typeof(IExactAppendStorageSession);
        _ = typeof(AppendIdempotencyConflictException);
        _ = typeof(RetentionIdempotencyDeclaration);
        _ = typeof(RetentionOperationResult);
        _ = typeof(IStorageInspectionSession);
        _ = typeof(IExactRetentionStorageSession);
        _ = typeof(StorageAccess);
        _ = typeof(StorageKey);
        _ = typeof(StorageValues);
        _ = typeof(SqliteProviderFactory);
        _ = typeof(InMemoryProviderFactory);
    }

    public static void CompileCallableSurface()
    {
        _ = new Func<string, IStorageProviderConnection>(connectionString => new SqliteProviderFactory().Create(connectionString));
        _ = new Func<Groundwork.Kernel.StorageUnit, StorageValues, RowWrite>((unit, values) => RowWrite.Upsert(unit, values));
        _ = new Func<IStorageSession, OperationId, StorageValues, AppendOutcomeReport>(
            (session, operation, values) => session.AppendWithOutcomes(operation, values));
        _ = new Func<IStorageSession, OperationId, RetentionExecutionOptions, RetentionOperationResult>(
            (session, operation, options) => session.ApplyRetention(operation, options));
        _ = new Func<IStorageSession, StorageInspection>(session => session.Inspect());
        _ = new Func<RecordTable<ApprovalRecord>, IStorageProviderConnection, RecordTableSession<ApprovalRecord>>((table, connection) => table.Open(connection));
        _ = new Func<RecordTable<ApprovalRecord>, IStorageProviderConnection, RecordTableStoreUnitOfWork<ApprovalRecord>>((table, connection) => table.BeginUnitOfWork(connection, BatchWriteOptions.Exact));
        _ = new Func<DocumentUnit<ApprovalDocument>, ApprovalDocument, RowWrite>((unit, value) => unit.Insert(value, WriteOptions.CreateOnly));
        _ = new Func<DocumentUnit<ApprovalDocument>, RowValues, DocumentReadResult<ApprovalDocument>>((unit, values) => unit.Read(values, null));
        _ = new Func<RecordTable<ApprovalRecord>, IGwQueryable<ApprovalRecord>>(table => table.Query.Where(row => row.Value == "approved"));
        _ = new Func<string, IStorageProviderConnection>(connectionString => new InMemoryProviderFactory().Create(connectionString));
        _ = new Func<QueryRequest, RuntimeCoverageGate>(request => new RuntimeCoverageGate([], []).Check(request) is not null ? new RuntimeCoverageGate([], []) : throw new InvalidOperationException());
    }

    private sealed record ApprovalRecord(Guid Id, string Value);
    private sealed record ApprovalDocument(Guid Id, string Value);
}
