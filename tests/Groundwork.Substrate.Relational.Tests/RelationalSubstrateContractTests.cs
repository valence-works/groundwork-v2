using System.Data;
using System.Data.Common;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.Substrate.Relational.Tests;

public sealed class RelationalSubstrateContractTests
{
    [Fact]
    public void Sql_emission_is_driven_by_kernel_declaration()
    {
        var dialect = new StubDialect();
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("tickets"),
            Name = "tickets",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int64, IsNullable = false },
                new ColumnDefinition { Name = "status", Type = PortableType.String, IsNullable = false, MaxLength = 32 },
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Indexes =
            [
                new IndexDefinition
                {
                    Name = "ix_tickets_status",
                    Columns = [new IndexColumn("status")],
                    IsUnique = true,
                    MissingValues = MissingValueBehavior.Excluded
                }
            ]
        };

        var create = RelationalSql.CreateTable(dialect, unit);
        var index = RelationalSql.CreateIndex(dialect, unit.Name, unit.Indexes[0]);

        Assert.Contains("CREATE TABLE", create, StringComparison.Ordinal);
        Assert.Contains("\"id\" integer NOT NULL", create, StringComparison.Ordinal);
        Assert.Contains("\"status\" varchar(32) NOT NULL", create, StringComparison.Ordinal);
        Assert.Contains("PRIMARY KEY (\"id\")", create, StringComparison.Ordinal);
        Assert.Contains("WHERE \"status\" IS NOT NULL", index, StringComparison.Ordinal);
        Assert.Equal(2, dialect.MapTypeCalls);
    }

    [Fact]
    public void Conditional_upsert_and_batch_hooks_are_public_provider_contracts()
    {
        var dialect = new StubDialect();
        var shape = new RelationalWriteShape(
            "tickets",
            [new RelationalWriteColumn("id"), new RelationalWriteColumn("status")],
            ["id"],
            ["status"]);

        Assert.Equal("UPSERT tickets", RelationalSql.ConditionalUpsert(dialect, shape));
        Assert.Equal("BATCH tickets 3", RelationalSql.BatchInsert(dialect, shape, 3));
        Assert.Equal(1, dialect.ConditionalUpsertCalls);
        Assert.Equal(1, dialect.BatchInsertCalls);
    }

    [Fact]
    public void Executor_opens_one_connection_and_releases_it_with_the_application_lock()
    {
        var connection = new TrackingConnection();
        var dialect = new StubDialect();
        var executor = new RelationalSchemaExecutor(() => connection, dialect);
        var target = new PhysicalSchemaTargetIdentity(new StorageUnitId("tickets"), "stub");

        using (var applicationLock = executor.AcquireApplicationLock(target))
        {
            Assert.Same(target, applicationLock.Target);
            Assert.Equal(ConnectionState.Open, connection.State);
            Assert.Equal(1, dialect.AcquireLockCalls);
            Assert.Equal(1, dialect.AcquireFenceCalls);
            Assert.Equal(1, dialect.ReadServerSessionIdCalls);
            Assert.Equal(1, Assert.IsType<RelationalApplicationLock>(applicationLock).ServerSessionId);
        }

        Assert.Equal(1, dialect.ReleaseLockCalls);
        Assert.Equal(1, connection.DisposeCalls);
    }

    [Fact]
    public void Executor_releases_the_application_lock_when_fencing_fails()
    {
        var connection = new TrackingConnection();
        var dialect = new StubDialect { ThrowOnAcquireFence = true };
        var executor = new RelationalSchemaExecutor(() => connection, dialect);
        var target = new PhysicalSchemaTargetIdentity(new StorageUnitId("tickets"), "stub");

        Assert.Throws<InvalidOperationException>(() => executor.AcquireApplicationLock(target));
        Assert.Equal(1, dialect.AcquireLockCalls);
        Assert.Equal(1, dialect.ReleaseLockCalls);
        Assert.Equal(1, connection.DisposeCalls);
    }

    private sealed class StubDialect : RelationalDialect
    {
        public int MapTypeCalls { get; private set; }
        public int ConditionalUpsertCalls { get; private set; }
        public int BatchInsertCalls { get; private set; }
        public int AcquireLockCalls { get; private set; }
        public int ReleaseLockCalls { get; private set; }
        public int AcquireFenceCalls { get; private set; }
        public int ReadServerSessionIdCalls { get; private set; }
        public bool ThrowOnAcquireFence { get; init; }

        public override string ProviderName => "stub";
        public override string QuoteIdentifier(string identifier) => $"\"{identifier}\"";
        public override string MapType(ColumnDefinition definition)
        {
            MapTypeCalls++;
            return definition.Type == PortableType.Int64 ? "integer" : $"varchar({definition.MaxLength ?? 255})";
        }
        public override string? MapCollation(ColumnDefinition definition) => null;
        public override string? MapDefault(ColumnDefinition definition) => null;
        public override string CreateTableSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> primaryKey) =>
            $"CREATE TABLE {QuoteIdentifier(table)} ({string.Join(", ", columns)}, PRIMARY KEY ({string.Join(", ", primaryKey.Select(QuoteIdentifier))}))";
        public override string AddColumnSql(string table, string column, string definition) => $"ADD {table}.{column} {definition}";
        public override string FinalizeColumnSql(string table, string column) => $"FINALIZE {table}.{column}";
        public override string CreateIndexSql(string table, IndexDefinition index, string? filter) =>
            $"CREATE {(index.IsUnique ? "UNIQUE " : string.Empty)}INDEX {QuoteIdentifier(index.Name)} ON {QuoteIdentifier(table)} " +
            $"({string.Join(", ", index.Columns.Select(column => QuoteIdentifier(column.Column)))})" +
            (filter is null ? string.Empty : $" WHERE {filter}");
        public override string DropIndexSql(string table, string index) => $"DROP {table}.{index}";
        public override string ConditionalUpsertSql(RelationalWriteShape shape)
        {
            ConditionalUpsertCalls++;
            return $"UPSERT {shape.Table}";
        }
        public override string BatchInsertSql(RelationalWriteShape shape, int batchSize)
        {
            BatchInsertCalls++;
            return $"BATCH {shape.Table} {batchSize}";
        }
        public override object? ConvertValue(object? value, ColumnDefinition definition) => value;
        public override void Validate(ColumnDefinition definition) { }
        public override bool TryMapUniqueViolation(DbException exception, out string indexName)
        {
            indexName = "ix_stub";
            return true;
        }
        public override void AcquireApplicationLock(DbConnection connection, string resource) => AcquireLockCalls++;
        public override void ReleaseApplicationLock(DbConnection connection, string resource) => ReleaseLockCalls++;
        public override bool VerifyApplicationLock(DbConnection connection, string resource) => true;
        public override long ReadServerSessionId(DbConnection connection)
        {
            ReadServerSessionIdCalls++;
            return 1;
        }
        public override long AcquireFence(DbConnection connection, PhysicalSchemaTargetIdentity target, string owner)
        {
            AcquireFenceCalls++;
            if (ThrowOnAcquireFence)
                throw new InvalidOperationException("fence failed");
            return 1;
        }
        public override void AssertFence(DbConnection connection, DbTransaction transaction, PhysicalSchemaTargetIdentity target, string owner, long fence) { }
        public override void EnsureInfrastructure(DbConnection connection) { }
        public override PhysicalSchemaHistoryState ReadHistory(DbConnection connection, PhysicalSchemaTargetIdentity target) => PhysicalSchemaHistoryState.Empty;
        public override void PublishHistory(DbConnection connection, PhysicalSchemaAppliedState state) { }
        public override bool TableExists(DbConnection connection, DbTransaction transaction, string table) => false;
        public override IReadOnlyDictionary<string, RelationalColumnMetadata> ReadColumns(DbConnection connection, DbTransaction transaction, string table) =>
            new Dictionary<string, RelationalColumnMetadata>();
        public override RelationalIndexMetadata? ReadIndex(DbConnection connection, DbTransaction transaction, string table, string index) => null;
    }

    private sealed class TrackingConnection : DbConnection
    {
        private ConnectionState state = ConnectionState.Closed;

        public int DisposeCalls { get; private set; }
#pragma warning disable CS8765
        public override string ConnectionString { get; set; } = string.Empty;
#pragma warning restore CS8765
        public override string Database => "stub";
        public override string DataSource => "stub";
        public override string ServerVersion => "1";
        public override ConnectionState State => state;
        public override void ChangeDatabase(string databaseName) { }
        public override void Open() => state = ConnectionState.Open;
        public override void Close() => state = ConnectionState.Closed;
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeCalls++;
                Close();
            }
            base.Dispose(disposing);
        }
    }
}
