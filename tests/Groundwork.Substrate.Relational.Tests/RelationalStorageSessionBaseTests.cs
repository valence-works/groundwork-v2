using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Store;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.Substrate.Relational.Tests;

public sealed class RelationalStorageSessionBaseTests
{
    private static readonly DateTimeOffset ProviderNow = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Append_uses_the_shared_protocol_inside_the_shared_transaction()
    {
        var connection = new TrackingConnection();
        var storage = new TrackingStorageAdapter(connection);
        var append = new TrackingAppendAdapter(storage);
        var session = new TrackingSession(Unit(), connection, storage, append);

        var result = session.Append(
            new OperationId(ProviderNow, "append"),
            [Values("one")]);

        Assert.Equal(WriteOutcomeStatus.Inserted, result.Status);
        Assert.Equal(["prepare", "reclaim", "read", "claim", "payload", "complete"], append.Events);
        Assert.True(append.AllCommandsSawTransaction);
        Assert.Equal(1, connection.CommitCalls);
        Assert.Equal(0, connection.RollbackCalls);
        Assert.Null(storage.Transaction);
        Assert.Equal(1, storage.PhysicalIndexNameReads);
    }

    [Fact]
    public void Ambient_unit_of_work_transaction_is_visible_to_native_crud_commands()
    {
        var connection = new TrackingConnection();
        using var transaction = connection.BeginTransaction();
        var storage = new TrackingStorageAdapter(connection);
        var session = new TrackingSession(
            Unit(),
            connection,
            storage,
            new TrackingAppendAdapter(storage),
            transaction);

        var result = session.Insert(Values("one"));

        Assert.Equal(WriteOutcomeStatus.Inserted, result.Status);
        Assert.Same(transaction, storage.MutationTransaction);
        Assert.Equal(0, connection.CommitCalls);
    }

    [Fact]
    public void Close_refuses_append_before_any_native_driver_work()
    {
        var connection = new TrackingConnection();
        var storage = new TrackingStorageAdapter(connection);
        var append = new TrackingAppendAdapter(storage);
        var session = new TrackingSession(Unit(), connection, storage, append);
        session.Close();

        Assert.Throws<ObjectDisposedException>(() => session.Append(
            new OperationId(ProviderNow, "closed"),
            [Values("one")]));

        Assert.Empty(append.Events);
    }

    [Fact]
    public void Shared_unit_of_work_commits_its_transaction_and_closes_session_views()
    {
        var connection = new TrackingConnection();
        var transaction = connection.BeginTransaction();
        var unit = Unit();
        TrackingSession? opened = null;
        using var work = new RelationalUnitOfWork(
            [unit],
            BatchWriteOptions.Default,
            declaration =>
            {
                var storage = new TrackingStorageAdapter(connection);
                opened = new TrackingSession(
                    declaration,
                    connection,
                    storage,
                    new TrackingAppendAdapter(storage),
                    transaction);
                return new RelationalUnitOfWorkSession(opened, opened.Close);
            },
            new RelationalUnitOfWorkLifetime(
                connection,
                transaction,
                supportsAsync: true,
                disposeTransaction: true));
        _ = work.OpenSession(unit);

        var summary = work.Commit();

        Assert.Equal(0, summary.Submitted);
        Assert.Equal(1, connection.CommitCalls);
        Assert.True(opened!.Closed);
    }

    private static StorageValues Values(string payload) =>
        new(new Dictionary<string, object?> { ["id"] = "one", ["payload"] = payload });

    private static StorageUnit Unit() => new()
    {
        Id = new StorageUnitId("public-relational-base"),
        Name = "public_relational_base",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
            new ColumnDefinition { Name = "payload", Type = PortableType.String, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        AppendIdempotency = new AppendIdempotencyDeclaration { Window = TimeSpan.FromMinutes(5) }
    };

    private sealed class TrackingSession(
        StorageUnit unit,
        TrackingConnection connection,
        TrackingStorageAdapter storage,
        TrackingAppendAdapter append,
        DbTransaction? transaction = null)
        : RelationalStorageSessionBase(
            unit,
            StorageAccess.Global,
            storage,
            append,
            retentionAdapter: null,
            onAppendRetentionOwner: connection,
            transaction: transaction)
    {
        internal bool Closed => IsClosed;
    }

    private sealed class TrackingStorageAdapter(TrackingConnection connection)
        : RelationalStorageSessionAdapter(connection, new TrackingDialect())
    {
        internal DbTransaction? MutationTransaction { get; private set; }
        internal int PhysicalIndexNameReads { get; private set; }

        public override IReadOnlyDictionary<string, string> PhysicalIndexNames(StorageUnit unit)
        {
            PhysicalIndexNameReads++;
            return base.PhysicalIndexNames(unit);
        }

        protected override void BindParameter(
            DbCommand command,
            string parameter,
            object? value,
            ColumnDefinition column) { }

        protected override ValueTask<WriteOutcome> Insert(
            StorageValues values,
            WriteOutcomeStatus status,
            RelationalExecution execution)
        {
            MutationTransaction = Transaction;
            return ValueTask.FromResult(new WriteOutcome(status));
        }

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

    private sealed class TrackingAppendAdapter(TrackingStorageAdapter storage) : RelationalAppendAdapter
    {
        internal List<string> Events { get; } = [];
        internal bool AllCommandsSawTransaction { get; private set; } = true;

        protected override ValueTask<DateTimeOffset> PrepareLedger(
            RelationalAppendCommand operation,
            RelationalExecution execution)
        {
            Observe("prepare");
            return ValueTask.FromResult(ProviderNow);
        }

        protected override ValueTask ReclaimExpired(
            RelationalAppendCommand operation,
            DateTimeOffset cutoff,
            RelationalExecution execution)
        {
            Observe("reclaim");
            return default;
        }

        protected override ValueTask<RelationalAppendLedgerState?> ReadLedger(
            RelationalAppendCommand operation,
            RelationalExecution execution)
        {
            Observe("read");
            return ValueTask.FromResult<RelationalAppendLedgerState?>(null);
        }

        protected override ValueTask DeleteLedger(
            RelationalAppendCommand operation,
            RelationalAppendLedgerState existing,
            RelationalExecution execution) => throw new UnreachableException();

        protected override ValueTask<bool> TryClaimLedger(
            RelationalAppendCommand operation,
            DateTimeOffset providerNow,
            RelationalExecution execution)
        {
            Observe("claim");
            return ValueTask.FromResult(true);
        }

        protected override ValueTask<RelationalAppendReplayState?> ReadClaimWinner(
            RelationalAppendCommand operation,
            RelationalExecution execution) => throw new UnreachableException();

        protected override ValueTask<IReadOnlyList<RowWriteOutcome>> InsertPayload(
            RelationalAppendCommand operation,
            RelationalExecution execution)
        {
            Observe("payload");
            var values = Assert.Single(operation.Values);
            return ValueTask.FromResult<IReadOnlyList<RowWriteOutcome>>(
                [new RowWriteOutcome(RowWrite.Insert(operation.Unit, values), new WriteOutcome(WriteOutcomeStatus.Inserted))]);
        }

        protected override ValueTask<bool> CompleteLedger(
            RelationalAppendCommand operation,
            string serializedOutcomes,
            RelationalExecution execution)
        {
            Observe("complete");
            return ValueTask.FromResult(true);
        }

        private void Observe(string operation)
        {
            Events.Add(operation);
            AllCommandsSawTransaction &= storage.Transaction is not null;
        }
    }

    private sealed class TrackingConnection : DbConnection
    {
        internal int CommitCalls { get; private set; }
        internal int RollbackCalls { get; private set; }

#pragma warning disable CS8765
        public override string ConnectionString { get; set; } = string.Empty;
#pragma warning restore CS8765
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "1";
        public override ConnectionState State => ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            new TrackingTransaction(this, isolationLevel);

        private sealed class TrackingTransaction(
            TrackingConnection connection,
            IsolationLevel isolationLevel) : DbTransaction
        {
            public override IsolationLevel IsolationLevel => isolationLevel;
            protected override DbConnection DbConnection => connection;
            public override void Commit() => connection.CommitCalls++;
            public override void Rollback() => connection.RollbackCalls++;
        }
    }

    private sealed class TrackingDialect : RelationalDialect
    {
        public override string ProviderName => "tracking";
        public override RelationalQueryRenderer CreateQueryRenderer() => new TrackingQueryRenderer(this);
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

    private sealed class TrackingQueryRenderer(RelationalDialect dialect)
        : RelationalQueryRenderer(dialect, dialect.ParameterBudget, supportsIndexHints: false)
    {
        protected override string ProviderName => "tracking";
    }
}
