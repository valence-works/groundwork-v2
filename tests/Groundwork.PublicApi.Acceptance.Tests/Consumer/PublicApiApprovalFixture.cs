using System.Data.Common;
using Groundwork.Documents;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Query.Model;
using Groundwork.Query.Linq;
using Groundwork.Query.Linq.Execution;
using Groundwork.Query.Planning;
using Groundwork.Records;
using Groundwork.Store;
using Groundwork.Substrate.Relational;
using Groundwork.Sqlite;
using Groundwork.MySql;
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
        _ = typeof(ReferenceDefinition);
        _ = typeof(ReferenceEnforcement);
        _ = typeof(CheckConstraintOperator);
        _ = typeof(CheckConstraintDefinition);
        _ = typeof(PhysicalSchemaOperationKind);
        _ = typeof(CreatePhysicalForeignKeyOperation);
        _ = typeof(CreatePhysicalCheckConstraintOperation);
        _ = typeof(Groundwork.Schema.SchemaReference);
        _ = typeof(Groundwork.Schema.SchemaCheckOperator);
        _ = typeof(Groundwork.Schema.SchemaCheck);
        _ = typeof(Groundwork.Kernel.StorageDeclarationBuilder);
        _ = typeof(PortabilityValidator);
        _ = typeof(AggregationOrderTerm);
        _ = typeof(AggregationAcceptance);
        _ = typeof(GwAllowAcceptedAggregationsAttribute);
        _ = typeof(AggregationQuery);
        _ = typeof(AggregationGroup);
        _ = typeof(AggregationGroup.Column);
        _ = typeof(AggregationGroup.TimeBucket);
        _ = typeof(AggregationTimeBucketKind);
        _ = typeof(AggregationTimeRange);
        _ = typeof(AggregationTimeBucketCalculator);
        _ = typeof(AggregationBuilder);
        _ = typeof(Aggregate.Count);
        _ = typeof(AggregationProfile);
        _ = typeof(RecordAggregationBinding<,>);
        _ = typeof(RecordAggregationResult<,>);
        _ = typeof(AggregationRowExtensions);
        _ = typeof(IRecordAggregationStore);
        _ = typeof(Groundwork.Records.StorageDeclarationBuilder);
        _ = typeof(ProviderOwnedColumns);
        _ = typeof(QueryRequest);
        _ = typeof(GwReference<,>);
        _ = typeof(GwGeneratedRowMember<>);
        _ = typeof(GwGeneratedRowAccessor<>);
        _ = typeof(GwGeneratedRows);
        _ = typeof(GwGeneratedRowValue);
        _ = typeof(QueryCoverageException);
        _ = typeof(QueryCoverageCandidates);
        _ = typeof(RecordTable);
        _ = typeof(RecordReference<,>);
        _ = typeof(RecordProjection<>);
        _ = typeof(RecordTableStoreUnitOfWork<>);
        _ = typeof(RecordWriteOptions);
        _ = typeof(BatchWriteOptions);
        _ = typeof(SchemaChangeKind);
        _ = typeof(SchemaCapabilityAdmission);
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
        _ = typeof(SetMutationOptions);
        _ = typeof(SetMutationOutcomeMode);
        _ = typeof(SetMutationOutcome);
        _ = typeof(ISetMutationStorageSession);
        _ = typeof(SetMutationResult);
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
        _ = typeof(MySqlProviderFactory);
        _ = typeof(MySqlProviderConnection);
        _ = typeof(MySqlDialect);
        _ = typeof(MySqlQueryRenderer);
        _ = typeof(MySqlSchemaToolProviderSessionFactory);
        _ = typeof(InMemoryProviderFactory);
        _ = typeof(RelationalStorageSessionBase);
        _ = typeof(RelationalStorageSessionAdapter);
        _ = typeof(RelationalAppendAdapter);
        _ = typeof(RelationalAppendCommand);
        _ = typeof(RelationalAppendLedgerState);
        _ = typeof(RelationalAppendReplayState);
        _ = typeof(RelationalRetentionAdapter);
        _ = typeof(RelationalRetentionCommand);
        _ = typeof(RelationalExactRetentionCommand);
        _ = typeof(RelationalRetentionLedgerState);
        _ = typeof(RelationalRetentionReplayState);
        _ = typeof(RelationalUnitOfWork);
        _ = typeof(RelationalUnitOfWorkSession);
        _ = typeof(RelationalUnitOfWorkLifetime);
        _ = WellKnownCapabilities.EnforcedConstraints;
    }

    public static void CompileCallableSurface()
    {
        _ = new Action<Type, int, Func<IReadOnlyDictionary<string, object?>, IReadOnlyList<string>, object?>>(
            GwGeneratedRows.RegisterProjection);
        _ = GwGeneratedRows.TryGetProjection<ApprovalRecord>(1, out _);
        _ = new Func<IReadOnlyDictionary<string, object?>, IReadOnlyList<string>, int, string>(
            GwGeneratedRowValue.ReadProjection<string>);
        _ = new Func<string, IStorageProviderConnection>(connectionString => new SqliteProviderFactory().Create(connectionString));
        _ = new Func<string, IStorageProviderConnection>(connectionString => new MySqlProviderFactory().Create(connectionString));
        _ = new Func<IEnumerable<CapabilityDescriptor>, IReadOnlyList<CapabilityDescriptor>>(
            SchemaCapabilityAdmission.AdvertiseEnforcedConstraints);
        _ = new Action<Groundwork.Kernel.StorageUnit, IEnumerable<CapabilityDescriptor>>(
            SchemaCapabilityAdmission.EnsureSupported);
        _ = new Func<Groundwork.Kernel.StorageUnit, StorageValues, RowWrite>((unit, values) => RowWrite.Upsert(unit, values));
        _ = new Func<Groundwork.Kernel.StorageDeclarationBuilder, Groundwork.Kernel.StorageDeclarationBuilder>(builder =>
            builder.UniqueIndex("by_sparse", index => index.Column("nullable").ExcludeMissingValues()));
        _ = new Func<Groundwork.Kernel.StorageDeclarationBuilder, Groundwork.Kernel.StorageDeclarationBuilder>(builder =>
            builder.Reference("customer", new StorageUnitId("customer"), "customer_id"));
        _ = new Func<Groundwork.Kernel.StorageDeclarationBuilder, Groundwork.Kernel.StorageDeclarationBuilder>(builder =>
            builder.Reference("customer", new StorageUnitId("customer"), ScopePolicy.Global, "customer_id"));
        _ = new Func<Groundwork.Kernel.StorageDeclarationBuilder, Groundwork.Kernel.StorageUnit, Groundwork.Kernel.StorageDeclarationBuilder>(
            (builder, target) => builder.PhysicalReference("fk_customer", target, "customer_id"));
        _ = new Func<Groundwork.Kernel.StorageDeclarationBuilder, Groundwork.Kernel.StorageDeclarationBuilder>(builder =>
            builder.Check("ck_quantity", "quantity", CheckConstraintOperator.GreaterThan, 0));
        _ = new Func<ColumnBuilder, ColumnBuilder>(column => column.LocaleOrder("sv-SE", 12));
        _ = new Func<Groundwork.Kernel.StorageUnit, PortabilityValidationResult>(unit => PortabilityValidator.Validate(unit));
        _ = new Func<Groundwork.Kernel.StorageUnit, PortabilityValidationResult>(unit => PortabilityValidator.ValidatePortableDefaults(unit));
        _ = new Func<Groundwork.Kernel.StorageDeclarationBuilder, Groundwork.Kernel.StorageDeclarationBuilder>(builder =>
            builder.Aggregate("summary", aggregation => aggregation.GroupBy("group").Count("count")));
        _ = new Func<string, Action<AggregationBuilder>, AggregationProfile>(AggregationProfile.Create);
        _ = new AggregationQuery("summary")
        {
            OrderByTerms = [new AggregationOrderTerm("count", SortDirection.Descending)]
        };
        _ = AggregationQuery.ForAdHoc(
            "summary",
            ["group"],
            [new Aggregate.Count("count")],
            AggregationAcceptance.Allow(
                "GW-AGG-0001", "support report", "operations",
                DateTimeOffset.UtcNow.AddDays(30), maxGroups: 100, maxInputRows: 1_000));
        _ = AggregationGroup.TimeBucket.FixedUtc("bucket", "createdAt", TimeSpan.FromHours(1));
        _ = AggregationGroup.TimeBucket.LocalCalendarDay("day", "createdAt");
        _ = PortabilityValidator.MaximumPortableIdentifierLength;
        _ = new Func<string, string, PortabilityValidationResult>((identifier, path) =>
            PortabilityValidator.ValidatePhysicalIdentifier(identifier, path));
        _ = new Action<Groundwork.Kernel.StorageUnit>(PortabilityValidator.EnsurePhysicalIdentifiers);
        _ = new Action<Groundwork.Kernel.StorageUnit>(PortabilityValidator.EnsurePortableDefaults);
        _ = new Func<Groundwork.Records.StorageDeclarationBuilder, Groundwork.Records.StorageDeclarationBuilder>(builder =>
            builder.UniqueIndex("by_sparse", index => index.Column("nullable").ExcludeMissingValues()));
        _ = new Func<Groundwork.Records.StorageDeclarationBuilder, Groundwork.Records.StorageDeclarationBuilder>(builder =>
            builder.Reference("customer", new StorageUnitId("customer"), "customer_id"));
        _ = new Func<Groundwork.Records.StorageDeclarationBuilder, Groundwork.Records.StorageDeclarationBuilder>(builder =>
            builder.Reference("customer", new StorageUnitId("customer"), ScopePolicy.Global, "customer_id"));
        _ = new Func<Groundwork.Records.StorageDeclarationBuilder, Groundwork.Kernel.StorageUnit, Groundwork.Records.StorageDeclarationBuilder>(
            (builder, target) => builder.PhysicalReference("fk_customer", target, "customer_id"));
        _ = new Func<Groundwork.Records.StorageDeclarationBuilder, Groundwork.Records.StorageDeclarationBuilder>(builder =>
            builder.Check("ck_quantity", "quantity", CheckConstraintOperator.GreaterThan, 0));
        _ = new Func<IStorageSession, OperationId, StorageValues, AppendOutcomeReport>(
            (session, operation, values) => session.AppendWithOutcomes(operation, values));
        _ = new Func<IStorageSession, StorageKey, IReadOnlyDictionary<string, object?>, WriteOutcome>(
            (session, key, expected) => session.CompareAndDelete(key, expected));
        _ = new Func<IStorageSession, OperationId, RetentionExecutionOptions, RetentionOperationResult>(
            (session, operation, options) => session.ApplyRetention(operation, options));
        _ = new RetentionExecutionOptions { KeepNewestOverride = 0 };
        _ = new Func<SetMutationOptions>(() => new SetMutationOptions
        {
            AcceptedScan = ScanAcceptance.Allow(
                "GW-SET-API",
                "clean-room public API approval",
                "groundwork",
                DateTimeOffset.UtcNow.AddMinutes(5))
        });
        _ = new Func<IStorageSession, Predicate, IReadOnlyDictionary<string, object?>, SetMutationOptions?, SetMutationResult>(
            (session, predicate, assignments, options) => session.UpdateWhere(predicate, assignments, options));
        _ = SetMutationOptions.Exact;
        _ = new Func<SetMutationResult, IReadOnlyList<SetMutationOutcome>>(result => result.Outcomes);
        _ = new Func<SetMutationResult, SetMutationOutcomeMode>(result => result.OutcomeMode);
        _ = new Func<SetMutationOutcome, StorageKey>(outcome => outcome.Key);
        _ = new Func<SetMutationOutcome, WriteOutcome>(outcome => outcome.Outcome);
        _ = new Func<IStorageSession, Predicate, IReadOnlyDictionary<string, object?>, SetMutationOptions?, CancellationToken, ValueTask<SetMutationResult>>(
            (session, predicate, assignments, options, cancellationToken) =>
                session.UpdateWhereAsync(predicate, assignments, options, cancellationToken));
        _ = new Func<IStorageSession, Predicate, SetMutationOptions?, SetMutationResult>(
            (session, predicate, options) => session.DeleteWhere(predicate, options));
        _ = new Func<IStorageSession, Predicate, SetMutationOptions?, CancellationToken, ValueTask<SetMutationResult>>(
            (session, predicate, options, cancellationToken) =>
                session.DeleteWhereAsync(predicate, options, cancellationToken));
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
        _ = new Func<Groundwork.Kernel.StorageUnit, RecordTable<ApprovalRecord>>(
            RecordTable.FromGenerated<ApprovalRecord>);
        _ = new Func<RecordTable<ApprovalRecord>, IStorageProviderConnection, RecordTableSession<ApprovalRecord>>((table, connection) => table.Open(connection));
        _ = new Func<RecordTable<ApprovalRecord>, IStorageProviderConnection, RecordTableStoreUnitOfWork<ApprovalRecord>>((table, connection) => table.BeginUnitOfWork(connection, BatchWriteOptions.Exact));
        _ = new Func<DocumentUnit<ApprovalDocument>, ApprovalDocument, RowWrite>((unit, value) => unit.Insert(value, WriteOptions.CreateOnly));
        _ = new Func<DocumentUnit<ApprovalDocument>, RowValues, DocumentReadResult<ApprovalDocument>>((unit, values) => unit.Read(values, null));
        _ = new Func<RecordTable<ApprovalRecord>, IGwQueryable<ApprovalRecord>>(table => table.Query.Where(row => row.Value == "approved"));
        var approvalOrders = new GwTableModel<ApprovalOrder>("approval_orders",
        [
            new(nameof(ApprovalOrder.Id), "id", QueryType.Guid, false),
            new(nameof(ApprovalOrder.CustomerId), "customer_id", QueryType.Guid, false)
        ]);
        var approvalCustomers = new GwTableModel<ApprovalCustomer>("approval_customers",
        [
            new(nameof(ApprovalCustomer.Id), "id", QueryType.Guid, false),
            new(nameof(ApprovalCustomer.Name), "name", QueryType.String, false)
        ]);
        var approvalCustomerJoin = new ReferenceJoin("customer", approvalCustomers.Table,
        [
            new JoinColumnPair(
                approvalOrders.Columns[nameof(ApprovalOrder.CustomerId)],
                approvalCustomers.Columns[nameof(ApprovalCustomer.Id)])
        ]);
        var approvalCustomerReference = approvalOrders.Reference(
            order => order.Customer,
            approvalCustomers,
            approvalCustomerJoin);
        _ = new Func<IGwQueryable<ApprovalOrder>, IGwQueryable<ApprovalOrder>>(
            query => query.Join(approvalCustomerReference).Where(order => order.Customer.Name == "approved"));
        var recordCustomers = RecordTable.For<ApprovalCustomer>("approval_record_customers")
            .Key(customer => customer.Id)
            .Build();
        _ = RecordTable<ApprovalCustomer>.AccessorDynamicCodeGenerationCount;
        var recordOrders = RecordTable.For<ApprovalOrder>("approval_record_orders")
            .Key(order => order.Id)
            .Index("by_customer", order => order.CustomerId)
            .Reference("customer", order => order.Customer, recordCustomers, order => order.CustomerId)
            .Build();
        var recordCustomerReference = recordOrders.Reference<ApprovalCustomer>("customer");
        var recordJoin = recordCustomerReference.Join(recordOrders.Query);
        _ = recordOrders.Select(
            recordJoin,
            recordCustomerReference,
            (order, customer) => new ApprovalOrderCustomer(order.Id, customer.Id, customer.Name));
        _ = new Func<RecordTable<ApprovalRecord>, RecordAggregationBinding<string, long>>(table => table.Aggregate<string, long>(
            "summary",
            "value",
            row => row.Get<long>("count")));
        _ = new Func<IGwQueryable<ApprovalMetric>, LinqTerminal<long?>>(query => query.Sum(row => row.Count));
        _ = new Func<IGwQueryable<ApprovalMetric>, LinqTerminal<decimal?>>(query => query.Sum(row => row.Amount));
        _ = new Func<IGwQueryable<ApprovalMetric>, LinqTerminal<string?>>(query => query.Min(row => row.Label));
        _ = new Func<IGwQueryable<ApprovalMetric>, LinqTerminal<Guid?>>(query => query.Max(row => row.Id));
        _ = new Func<IGwQueryable<ApprovalMetric>, IGwQueryExecutor, Task<long?>>(
            (query, executor) => query.SumAsync(executor, row => row.Count));
        _ = new Func<IGwQueryable<ApprovalMetric>, IGwQueryExecutor, Task<decimal?>>(
            (query, executor) => query.SumAsync(executor, row => row.Amount));
        _ = new Func<IGwQueryable<ApprovalMetric>, IGwQueryExecutor, Task<string?>>(
            (query, executor) => query.MinAsync(executor, row => row.Label));
        _ = new Func<IGwQueryable<ApprovalMetric>, IGwQueryExecutor, Task<Guid?>>(
            (query, executor) => query.MaxAsync(executor, row => row.Id));
        _ = new Func<string, IStorageProviderConnection>(connectionString => new InMemoryProviderFactory().Create(connectionString));
        _ = new Func<QueryRequest, RuntimeCoverageGate>(request => new RuntimeCoverageGate([], []).Check(request) is not null ? new RuntimeCoverageGate([], []) : throw new InvalidOperationException());
        _ = new Func<QueryRequest, QueryCoverageCandidates, QueryCoverageResult>(QueryCoverageChecker.Check);
        _ = new Func<QueryCoverageCandidates, QueryCoverageCandidates, RuntimeCoverageGate>(
            (declared, deployed) => new RuntimeCoverageGate(declared, deployed));
        _ = new Func<Groundwork.Kernel.StorageUnit, StorageAccess, DbConnection, DbTransaction, IUnitOfWork>(
            ComposeRelationalUnitOfWork);
    }

    private static IUnitOfWork ComposeRelationalUnitOfWork(
        Groundwork.Kernel.StorageUnit unit,
        StorageAccess access,
        DbConnection connection,
        DbTransaction transaction)
    {
        var lifetime = new RelationalUnitOfWorkLifetime(
            connection,
            transaction,
            supportsAsync: true,
            disposeTransaction: true);
        return new RelationalUnitOfWork(
            [unit],
            BatchWriteOptions.Default,
            declaration =>
            {
                var session = new ApprovalRelationalSession(
                    declaration,
                    access,
                    connection,
                    transaction);
                return new RelationalUnitOfWorkSession(session, session.Close);
            },
            lifetime);
    }

    private sealed class ApprovalRelationalSession(
        Groundwork.Kernel.StorageUnit unit,
        StorageAccess access,
        DbConnection connection,
        DbTransaction transaction)
        : RelationalStorageSessionBase(
            unit,
            access,
            new ApprovalRelationalAdapter(connection),
            new ApprovalAppendAdapter(),
            retentionAdapter: null,
            onAppendRetentionOwner: connection,
            transaction: transaction);

    private sealed class ApprovalRelationalAdapter(DbConnection connection)
        : RelationalStorageSessionAdapter(connection, new ApprovalDialect())
    {
        private DbCommand TransactionBoundCommand(string sql) => CreateCommand(sql);

        protected override void BindParameter(
            DbCommand command,
            string parameter,
            object? value,
            ColumnDefinition column) { }

        protected override ValueTask<WriteOutcome> Insert(
            StorageValues values,
            WriteOutcomeStatus status,
            RelationalExecution execution) => throw new NotSupportedException();

        protected override ValueTask<WriteOutcome> Update(
            StorageValues values,
            StorageKey key,
            StoredEntry? existing,
            WriteOptions? options,
            RelationalExecution execution) => throw new NotSupportedException();

        protected override ValueTask<WriteOutcome> Upsert(
            StorageValues values,
            StorageKey key,
            StoredEntry? existing,
            WriteOptions? options,
            RelationalExecution execution) => throw new NotSupportedException();

        protected override ValueTask<WriteOutcome> Delete(
            StorageKey key,
            StoredEntry? existing,
            WriteOptions? options,
            RelationalExecution execution) => throw new NotSupportedException();
    }

    private sealed class ApprovalAppendAdapter : RelationalAppendAdapter
    {
        protected override ValueTask<DateTimeOffset> PrepareLedger(
            RelationalAppendCommand operation,
            RelationalExecution execution) => throw new NotSupportedException();

        protected override ValueTask ReclaimExpired(
            RelationalAppendCommand operation,
            DateTimeOffset cutoff,
            RelationalExecution execution) => throw new NotSupportedException();

        protected override ValueTask<RelationalAppendLedgerState?> ReadLedger(
            RelationalAppendCommand operation,
            RelationalExecution execution) => throw new NotSupportedException();

        protected override ValueTask DeleteLedger(
            RelationalAppendCommand operation,
            RelationalAppendLedgerState existing,
            RelationalExecution execution) => throw new NotSupportedException();

        protected override ValueTask<bool> TryClaimLedger(
            RelationalAppendCommand operation,
            DateTimeOffset providerNow,
            RelationalExecution execution) => throw new NotSupportedException();

        protected override ValueTask<RelationalAppendReplayState?> ReadClaimWinner(
            RelationalAppendCommand operation,
            RelationalExecution execution) => throw new NotSupportedException();

        protected override ValueTask<IReadOnlyList<RowWriteOutcome>> InsertPayload(
            RelationalAppendCommand operation,
            RelationalExecution execution) => throw new NotSupportedException();

        protected override ValueTask<bool> CompleteLedger(
            RelationalAppendCommand operation,
            string serializedOutcomes,
            RelationalExecution execution) => throw new NotSupportedException();
    }

    private sealed class ApprovalDialect : RelationalDialect
    {
        public override string ProviderName => "approval";
        public override string QuoteIdentifier(string identifier) => identifier;
        public override string MapType(ColumnDefinition definition) => definition.Type.ToString();
        public override string? MapCollation(ColumnDefinition definition) => null;
        public override string? MapDefault(ColumnDefinition definition) => null;
        public override string CreateTableSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> primaryKey) => string.Empty;
        public override string AddColumnSql(string table, string column, string definition) => string.Empty;
        public override string FinalizeColumnSql(string table, string column, ColumnDefinition definition) => string.Empty;
        public override string CreateIndexSql(string table, IndexDefinition index, string? filter) => string.Empty;
        public override string DropIndexSql(string table, string index) => string.Empty;
        public override string ConditionalUpsertSql(RelationalWriteShape shape) => string.Empty;
        public override string BatchInsertSql(RelationalWriteShape shape, int batchSize) => string.Empty;
        public override object? ConvertValue(object? value, ColumnDefinition definition) => value;
        public override void Validate(ColumnDefinition definition) { }
        public override bool TryMapUniqueViolation(DbException exception, out string indexName)
        {
            indexName = string.Empty;
            return false;
        }
        public override void AcquireApplicationLock(DbConnection connection, string resource) { }
        public override void ReleaseApplicationLock(DbConnection connection, string resource) { }
        public override bool VerifyApplicationLock(DbConnection connection, string resource) => true;
        public override long ReadServerSessionId(DbConnection connection) => 0;
        public override long AcquireFence(DbConnection connection, PhysicalSchemaTargetIdentity target, string owner) => 0;
        public override void AssertFence(DbConnection connection, DbTransaction transaction, PhysicalSchemaTargetIdentity target, string owner, long fence) { }
        public override void EnsureInfrastructure(DbConnection connection) { }
        public override PhysicalSchemaHistoryState ReadHistory(DbConnection connection, PhysicalSchemaTargetIdentity target) => PhysicalSchemaHistoryState.Empty;
        public override void PublishHistory(DbConnection connection, DbTransaction transaction, PhysicalSchemaTargetIdentity target, PhysicalSchemaAppliedState state, string? expectedAppliedTargetFingerprint, string owner, long fence) { }
        public override bool TableExists(DbConnection connection, DbTransaction? transaction, string table) => false;
        public override IReadOnlyDictionary<string, RelationalColumnMetadata> ReadColumns(DbConnection connection, DbTransaction? transaction, string table) => new Dictionary<string, RelationalColumnMetadata>();
        public override RelationalIndexMetadata? ReadIndex(DbConnection connection, DbTransaction? transaction, string table, string index) => null;
    }

    private sealed record ApprovalRecord(Guid Id, string Value);
    private sealed record ApprovalMetric(Guid Id, string Label, int Count, decimal Amount);
    private sealed record ApprovalOrder(Guid Id, Guid CustomerId, ApprovalCustomer Customer);
    private sealed record ApprovalCustomer(Guid Id, string Name);
    private sealed record ApprovalOrderCustomer(Guid OrderId, Guid CustomerId, string CustomerName);
    private sealed record ApprovalDocument(Guid Id, string Value);
}
