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
        _ = typeof(LocaleSortKeyDefinition);
        _ = typeof(PortableLocaleOrdering);
        _ = typeof(MissingValueBehavior);
        _ = typeof(IndexBuilder);
        _ = typeof(Groundwork.Kernel.StorageDeclarationBuilder);
        _ = typeof(PortabilityValidator);
        _ = typeof(AggregationOrderTerm);
        _ = typeof(AggregationQuery);
        _ = typeof(AggregationGroup);
        _ = typeof(AggregationGroup.Column);
        _ = typeof(AggregationGroup.TimeBucket);
        _ = typeof(AggregationTimeBucketKind);
        _ = typeof(AggregationTimeRange);
        _ = typeof(AggregationTimeBucketCalculator);
        _ = typeof(AggregationBuilder);
        _ = typeof(Aggregate.Count);
        _ = typeof(Groundwork.Records.StorageDeclarationBuilder);
        _ = typeof(ProviderOwnedColumns);
        _ = typeof(QueryRequest);
        _ = typeof(QueryCoverageException);
        _ = typeof(RecordTable);
        _ = typeof(RecordTableStoreUnitOfWork<>);
        _ = typeof(RecordWriteOptions);
        _ = typeof(BatchWriteOptions);
        _ = typeof(RowWrite);
        _ = typeof(AppendOutcomeReport);
        _ = typeof(IExactAppendStorageSession);
        _ = typeof(ICompareAndDeleteStorageSession);
        _ = typeof(AppendIdempotencyConflictException);
        _ = typeof(RetentionIdempotencyDeclaration);
        _ = typeof(RetentionOperationResult);
        _ = typeof(RetentionExecutionOptions);
        _ = typeof(IStorageInspectionSession);
        _ = typeof(IExactRetentionStorageSession);
        _ = typeof(StorageAccess);
        _ = typeof(StorageAccessAudit);
        _ = typeof(StorageAccessEvent);
        _ = typeof(IStorageAccessObserver);
        _ = typeof(IPrivilegedCrossScopeQuerySession);
        _ = typeof(CrossScopeQueryResult);
        _ = typeof(CrossScopeQueryRow);
        _ = typeof(StorageKey);
        _ = typeof(StorageValues);
        _ = typeof(StorageUnitQueryRenderOptions);
        _ = typeof(QueryAdmissionProfile);
        _ = typeof(KeyedBatchReadRow);
        _ = typeof(KeyedBatchReadResult);
        _ = typeof(KeyedBatchReadRequest);
        _ = typeof(KeyedBatchReadSessionExtensions);
        _ = typeof(SqliteProviderFactory);
        _ = typeof(InMemoryProviderFactory);
    }

    public static void CompileCallableSurface()
    {
        _ = new Func<string, IStorageProviderConnection>(connectionString => new SqliteProviderFactory().Create(connectionString));
        _ = new Func<Groundwork.Kernel.StorageUnit, StorageValues, RowWrite>((unit, values) => RowWrite.Upsert(unit, values));
        _ = new Func<Groundwork.Kernel.StorageDeclarationBuilder, Groundwork.Kernel.StorageDeclarationBuilder>(builder =>
            builder.UniqueIndex("by_sparse", index => index.Column("nullable").ExcludeMissingValues()));
        _ = new Func<ColumnBuilder, ColumnBuilder>(column => column.LocaleOrder("sv-SE", 12));
        _ = new Func<Groundwork.Kernel.StorageUnit, PortabilityValidationResult>(unit => PortabilityValidator.Validate(unit));
        _ = new Func<Groundwork.Kernel.StorageDeclarationBuilder, Groundwork.Kernel.StorageDeclarationBuilder>(builder =>
            builder.Aggregate("summary", aggregation => aggregation.GroupBy("group").Count("count")));
        _ = new AggregationQuery("summary")
        {
            OrderByTerms = [new AggregationOrderTerm("count", SortDirection.Descending)]
        };
        _ = AggregationGroup.TimeBucket.FixedUtc("bucket", "createdAt", TimeSpan.FromHours(1));
        _ = AggregationGroup.TimeBucket.LocalCalendarDay("day", "createdAt");
        _ = PortabilityValidator.MaximumPortableIdentifierLength;
        _ = new Func<string, string, PortabilityValidationResult>((identifier, path) =>
            PortabilityValidator.ValidatePhysicalIdentifier(identifier, path));
        _ = new Action<Groundwork.Kernel.StorageUnit>(PortabilityValidator.EnsurePhysicalIdentifiers);
        _ = new Func<Groundwork.Records.StorageDeclarationBuilder, Groundwork.Records.StorageDeclarationBuilder>(builder =>
            builder.UniqueIndex("by_sparse", index => index.Column("nullable").ExcludeMissingValues()));
        _ = new Func<IStorageSession, OperationId, StorageValues, AppendOutcomeReport>(
            (session, operation, values) => session.AppendWithOutcomes(operation, values));
        _ = new Func<IStorageSession, StorageKey, IReadOnlyDictionary<string, object?>, WriteOutcome>(
            (session, key, expected) => session.CompareAndDelete(key, expected));
        _ = new Func<IStorageSession, OperationId, RetentionExecutionOptions, RetentionOperationResult>(
            (session, operation, options) => session.ApplyRetention(operation, options));
        _ = new RetentionExecutionOptions { KeepNewestOverride = 0 };
        _ = new Func<IStorageSession, StorageInspection>(session => session.Inspect());
        _ = new Func<StorageAccessAudit, StorageAccess>(StorageAccess.PrivilegedAcrossScopes);
        _ = new Func<IStorageSession, QueryRequest, CrossScopeQueryResult>(
            (session, request) => session.QueryAcrossScopes(request));
        _ = new Func<IStorageSession, KeyedBatchReadRequest, IStorageProviderConnection?, KeyedBatchReadResult>(
            (session, request, connection) => session.BatchRead(request, connection));
        _ = new Func<IStorageSession, KeyedBatchReadRequest, IStorageProviderConnection?, CancellationToken, ValueTask<KeyedBatchReadResult>>(
            (session, request, connection, cancellationToken) => session.BatchReadAsync(request, connection, cancellationToken));
        _ = new QueryAdmissionProfile
        {
            MaximumBatchReadKeys = 999,
            MaximumBatchReadPayloadBytes = 1_000_000
        };
        _ = new Func<Groundwork.Kernel.StorageUnit, string, QueryRenderOptions>(
            (unit, selectedIndex) => unit.CreateQueryRenderOptions(selectedIndex));
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
