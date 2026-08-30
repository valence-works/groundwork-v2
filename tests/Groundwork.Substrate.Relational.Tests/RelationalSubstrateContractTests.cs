using System.Data;
using System.Data.Common;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.Substrate.Relational.Tests;

public sealed class RelationalSubstrateContractTests
{
    private const string ExpectedSearchKeyAlgorithmId =
        "groundwork-unicode-ordinal-ignore-case-v1-3206f759667cb9cc764ec243dfb3d322a39970184efab619e80163c36d86818f";

    [Theory]
    [InlineData("stale-search-key-v0")]
    [InlineData("prefix-groundwork-ascii-lower-v1-suffix")]
    public void Search_key_catalog_refuses_unknown_or_malformed_algorithm_before_sql(string algorithmId)
    {
        using var connection = new TrackingConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var definition = new ProviderPhysicalSchemaDefinition(
            "stub",
            new StorageUnitId("tickets"),
            RelationalDialect.SearchKeyDefinitionKind,
            "tickets" + RelationalDialect.SearchKeyDefinitionSeparator + "name_folded",
            algorithmId);

        var failure = Assert.Throws<InvalidOperationException>(() => RelationalSearchKeyCatalog.Apply(
            connection,
            transaction,
            definition,
            "UPSERT"));

        Assert.Contains("algorithm", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rebuild", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, connection.CommandCalls);
    }

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
    public void Shared_interop_view_emission_refuses_before_sql_when_catalog_inspection_is_not_supported()
    {
        var unit = StorageUnit.Declare("tickets", "tickets")
            .Int64("id", column => column.Required())
            .Key("id")
            .InteropView("reporting_tickets")
            .Build();
        var definition = Assert.IsType<ProviderPhysicalSchemaDefinition>(
            RelationalInteropViewDefinition.Create("stub", unit));
        using var connection = new TrackingConnection();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var failure = Assert.Throws<InvalidOperationException>(() =>
            new StubDialect().ApplyProviderDefinition(connection, transaction, definition));

        Assert.Contains("cannot inspect", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, connection.CommandCalls);
    }

    [Fact]
    public void Write_shape_validates_members_and_snapshots_inputs()
    {
        var columns = new List<RelationalWriteColumn>
        {
            new("id", "id_parameter"),
            new("status", "status_parameter")
        };
        var keys = new List<string> { "id" };
        var updates = new List<string> { "status" };
        var shape = new RelationalWriteShape("tickets", columns, keys, updates);

        columns.Clear();
        keys.Clear();
        updates.Clear();

        Assert.Equal(["id", "status"], shape.Columns.Select(column => column.Name));
        Assert.Equal(["id"], shape.KeyColumns);
        Assert.Equal(["status"], shape.UpdateColumns);
        Assert.Throws<ArgumentException>(() => new RelationalWriteShape(
            "tickets",
            [new RelationalWriteColumn("id"), new RelationalWriteColumn("id")],
            ["id"],
            []));
        Assert.Throws<ArgumentException>(() => new RelationalWriteShape(
            "tickets",
            [new RelationalWriteColumn("id")],
            ["missing"],
            []));
        Assert.Throws<ArgumentException>(() => new RelationalWriteShape(
            "tickets",
            [new RelationalWriteColumn("id")],
            ["id"],
            ["id"]));
        Assert.Throws<ArgumentException>(() => new RelationalWriteShape(
            "tickets",
            [new RelationalWriteColumn("id"), new RelationalWriteColumn("status")],
            ["id", "id"],
            ["status"]));
    }

    [Fact]
    public void Finalize_column_hook_receives_the_complete_declaration()
    {
        var dialect = new StubDialect();
        var column = new ColumnDefinition
        {
            Name = "status",
            Type = PortableType.String,
            IsNullable = false,
            MaxLength = 32
        };

        Assert.Equal("FINALIZE tickets.status String", RelationalSql.FinalizeColumn(dialect, "tickets", column));
        Assert.Same(column, dialect.FinalizedColumn);
    }

    [Fact]
    public void Executor_applies_a_batch_in_one_durable_transaction()
    {
        var connection = new TrackingConnection();
        var dialect = new StubDialect { TableExistsResult = true };
        dialect.CatalogColumns["id"] = new("id", "integer", true, null, null, 1);
        var executor = new RelationalSchemaExecutor(() => connection, dialect);
        var target = CreateTarget();
        var plan = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, DateTimeOffset.UtcNow);
        var operations = plan.Operations
            .Where(operation => operation.Kind != PhysicalSchemaOperationKind.PublishAppliedState)
            .ToArray();

        using var applicationLock = executor.AcquireApplicationLock(target.Identity);
        var acknowledgements = executor.ApplyOperationBatch(target.Identity, operations, applicationLock);

        Assert.Equal(operations.Length, acknowledgements.Count);
        Assert.Equal(1, connection.BeginTransactionCalls);
        Assert.Equal(1, connection.CommitCalls);
        Assert.Equal(0, connection.RollbackCalls);
        Assert.Equal(operations.Count(operation => operation.Kind is not PhysicalSchemaOperationKind.ValidatePhysicalSchema), connection.CommandCalls);
        Assert.Equal(operations.Length + 2, dialect.AssertFenceCalls);
    }

    [Fact]
    public void Executor_rolls_back_the_complete_batch_when_an_operation_fails()
    {
        var connection = new TrackingConnection { ThrowOnCommand = true };
        var dialect = new StubDialect { TableExistsResult = true };
        dialect.CatalogColumns["id"] = new("id", "integer", true, null, null, 1);
        var executor = new RelationalSchemaExecutor(() => connection, dialect);
        var target = CreateTarget();
        var operations = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, DateTimeOffset.UtcNow)
            .Operations.Where(operation => operation.Kind != PhysicalSchemaOperationKind.PublishAppliedState)
            .ToArray();

        using var applicationLock = executor.AcquireApplicationLock(target.Identity);
        Assert.Throws<InvalidOperationException>(() => executor.ApplyOperationBatch(target.Identity, operations, applicationLock));

        Assert.Equal(1, connection.BeginTransactionCalls);
        Assert.Equal(0, connection.CommitCalls);
        Assert.Equal(1, connection.RollbackCalls);
    }

    [Fact]
    public void Executor_validates_catalog_before_provider_specific_target_validation()
    {
        var connection = new TrackingConnection();
        var dialect = new StubDialect { TableExistsResult = true };
        dialect.CatalogColumns["id"] = new("id", "integer", true, null, null, 1);
        var executor = new RelationalSchemaExecutor(() => connection, dialect);
        var target = CreateTarget();
        var validation = PhysicalSchemaDiffPlanner.Plan(target, PhysicalSchemaHistoryState.Empty, DateTimeOffset.UtcNow)
            .Operations.Single(operation => operation.Kind == PhysicalSchemaOperationKind.ValidatePhysicalSchema);

        using var applicationLock = executor.AcquireApplicationLock(target.Identity);
        executor.ApplyOperation(target.Identity, validation, applicationLock);

        Assert.Equal(1, dialect.TableExistsCalls);
        Assert.Equal(1, dialect.ReadColumnsCalls);
        Assert.Equal(1, dialect.ValidateTargetCalls);
    }

    [Fact]
    public void Publish_forwards_previous_cas_and_uses_a_transaction_and_fence()
    {
        var connection = new TrackingConnection();
        var dialect = new StubDialect { TableExistsResult = true };
        dialect.CatalogColumns["id"] = new("id", "integer", true, null, null, 1);
        var executor = new RelationalSchemaExecutor(() => connection, dialect);
        var target = CreateTarget();
        var applied = PhysicalSchemaApplication.Apply(target, executor, DateTimeOffset.UtcNow);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, applied.Outcome);
        Assert.Null(dialect.LastExpectedAppliedTargetFingerprint);
        Assert.True(dialect.LastPublishHadTransaction);

        using var applicationLock = executor.AcquireApplicationLock(target.Identity);
        executor.PublishAppliedState(applied.AppliedState!, "previous-fingerprint", applicationLock);

        Assert.Equal("previous-fingerprint", dialect.LastExpectedAppliedTargetFingerprint);
        Assert.Equal(2, dialect.PublishHistoryCalls);
        Assert.Equal(3, connection.CommitCalls);
    }

    [Fact]
    public void Inspect_history_reports_catalog_drift_instead_of_always_claiming_validity()
    {
        var connection = new TrackingConnection();
        var dialect = new StubDialect { TableExistsResult = true };
        dialect.CatalogColumns["id"] = new("id", "integer", true, null, null, 1);
        var executor = new RelationalSchemaExecutor(() => connection, dialect);
        var target = CreateTarget();
        var applied = PhysicalSchemaApplication.Apply(target, executor, DateTimeOffset.UtcNow).AppliedState!;
        dialect.History = PhysicalSchemaHistoryState.FromApplied(applied);

        Assert.True(executor.InspectHistory(target).IsAppliedSchemaValid);
        dialect.CatalogColumns.Remove("id");
        Assert.False(executor.InspectHistory(target).IsAppliedSchemaValid);
    }

    [Fact]
    public void Inspect_history_reports_each_concrete_column_difference()
    {
        var connection = new TrackingConnection();
        var dialect = new StubDialect { TableExistsResult = true };
        dialect.CatalogColumns["id"] = new("id", "integer", true, null, null, 1);
        var executor = new RelationalSchemaExecutor(() => connection, dialect);
        var target = CreateTarget();
        var applied = PhysicalSchemaApplication.Apply(target, executor, DateTimeOffset.UnixEpoch).AppliedState!;
        dialect.History = PhysicalSchemaHistoryState.FromApplied(applied);
        dialect.CatalogColumns["id"] = new(
            "id",
            "varchar(255)",
            false,
            "wrong-default",
            "wrong-collation",
            0,
            IsComputed: true,
            IsPersisted: true,
            ComputedDefinition: "generated",
            Generation: ColumnGeneration.ProviderSequence);

        var inspection = executor.InspectHistory(target);

        var message = inspection.ColumnDrift.Single().Message;
        Assert.Contains("type", message, StringComparison.Ordinal);
        Assert.Contains("nullability", message, StringComparison.Ordinal);
        Assert.Contains("default", message, StringComparison.Ordinal);
        Assert.Contains("collation", message, StringComparison.Ordinal);
        Assert.Contains("primary-key order", message, StringComparison.Ordinal);
        Assert.Contains("generation", message, StringComparison.Ordinal);
        Assert.Contains("computed", message, StringComparison.Ordinal);
        Assert.Contains("columns.id", inspection.ColumnDrift.Single().Path, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_history_checks_persisted_search_key_algorithm_and_provider_invariants()
    {
        var connection = new TrackingConnection();
        var dialect = new StubDialect { TableExistsResult = true };
        dialect.CatalogColumns["id"] = new("id", "integer", true, null, null, 1);
        dialect.CatalogColumns["name"] = new("name", "varchar(255)", true, null, null, 0);
        dialect.CatalogColumns["name_folded"] = new("name_folded", "varchar(255)", false, null, null, 0);
        var executor = new RelationalSchemaExecutor(() => connection, dialect);
        var target = new PhysicalSchemaTarget(
            new SchemaSubject(new StorageUnit
            {
                Id = new StorageUnitId("tickets-derived"),
                Name = "tickets_derived",
                Columns =
                [
                    new() { Name = "id", Type = PortableType.Int64 },
                    new() { Name = "name", Type = PortableType.String },
                    new() { Name = "name_folded", Type = PortableType.String, IsNullable = false }
                ],
                DerivedColumns =
                [
                    new()
                    {
                        Name = "name_folded",
                        SourceColumn = "name",
                        Projection = PortableProjection.BoundarySearchKey
                    }
                ],
                Key = new KeyDefinition { Columns = ["id"] }
            }),
            new ProviderIdentity("stub", "1"));

        dialect.DerivedSearchKeyAlgorithms["name_folded"] = PortableStringComparison.SearchKeyAlgorithmId;
        var applied = PhysicalSchemaApplication.Apply(target, executor, DateTimeOffset.UnixEpoch).AppliedState!;
        dialect.History = PhysicalSchemaHistoryState.FromApplied(applied);
        dialect.DerivedSearchKeyAlgorithms["name_folded"] = "old-search-key-v1";

        var inspection = executor.InspectHistory(target);

        Assert.Contains(inspection.ColumnDrift, refusal =>
            refusal.Path == "columns.name_folded.searchKeyAlgorithm" &&
            refusal.Message.Contains(PortableStringComparison.SearchKeyAlgorithmId, StringComparison.Ordinal));

        dialect.DerivedSearchKeyAlgorithms["name_folded"] = PortableStringComparison.SearchKeyAlgorithmId;
        dialect.ThrowOnValidateTarget = true;
        var providerInspection = executor.InspectHistory(target);

        Assert.Contains(providerInspection.ColumnDrift, refusal =>
            refusal.Path == "provider" &&
            refusal.Message.Contains("provider invariant", StringComparison.Ordinal));
        Assert.Equal(2, dialect.ValidateTargetCalls);
    }

    [Fact]
    public void Runtime_admission_blocks_a_missing_column_and_names_it()
    {
        var dialect = new StubDialect { TableExistsResult = true };
        SeedRuntimeCatalog(dialect);
        var target = RuntimeTarget();
        var (executor, history) = Applied(dialect, target);
        dialect.CatalogColumns.Remove("status");

        var inspection = executor.InspectHistory(target);
        var admission = new GroundworkRuntimeSchemaAdmissionResult(
            inspection,
            PhysicalSchemaDiffPlanner.Plan(target, history, DateTimeOffset.UnixEpoch));

        Assert.False(admission.IsReady);
        var refusal = Assert.Single(admission.Refusals);
        Assert.Equal("GW-RUNTIME-001", refusal.Code);
        Assert.Equal("columns.status", refusal.Path);
        Assert.Equal("Relational schema table 'tickets' is missing column 'status'.", refusal.Message);
    }

    [Fact]
    public void Runtime_admission_degrades_for_a_missing_index_without_blocking_startup()
    {
        var dialect = new StubDialect { TableExistsResult = true };
        SeedRuntimeCatalog(dialect);
        var target = RuntimeTarget();
        var (executor, history) = Applied(dialect, target);
        dialect.CatalogIndexes.Remove("ix_status");

        var inspection = executor.InspectHistory(target);
        var admission = new GroundworkRuntimeSchemaAdmissionResult(
            inspection,
            PhysicalSchemaDiffPlanner.Plan(target, history, DateTimeOffset.UnixEpoch));

        Assert.True(inspection.IsAppliedSchemaValid);
        Assert.True(inspection.HasIndexDrift);
        Assert.True(admission.IsReady);
        var refusal = Assert.Single(admission.Refusals);
        Assert.Equal("GW-RUNTIME-002", refusal.Code);
        Assert.Equal("indexes.ix_status", refusal.Path);
        Assert.Equal("Relational schema table 'tickets' is missing index 'ix_status'.", refusal.Message);
    }

    [Fact]
    public void Inspection_classifies_collation_search_key_and_index_shape_drift()
    {
        var dialect = SearchDialect();
        var target = SearchTarget();
        var (executor, _) = Applied(dialect, target);
        dialect.CatalogColumns["status"] = new("status", "varchar(255)", true, null, "Ordinal", 1);
        dialect.DerivedSearchKeyAlgorithms["status_folded"] = "old-fold-v1";
        dialect.CatalogIndexes["ix_status"] = new(
            true,
            [new RelationalIndexColumnMetadata("status", SortDirection.Descending)],
            null);

        var inspection = executor.InspectHistory(target);

        var collationRefusal = Assert.Single(inspection.ColumnDrift, refusal => refusal.Path == "columns.status");
        Assert.Equal("GW-RUNTIME-001", collationRefusal.Code);
        Assert.Equal(
            "Relational schema column 'search.status' differs: collation 'Ordinal' != 'OrdinalIgnoreCase'.",
            collationRefusal.Message);
        var searchKeyRefusal = Assert.Single(
            inspection.ColumnDrift,
            refusal => refusal.Path == "columns.status_folded.searchKeyAlgorithm");
        Assert.Equal("GW-RUNTIME-001", searchKeyRefusal.Code);
        Assert.Equal(
            "Relational persisted search-key algorithm for derived column 'search.status_folded' differs: " +
            "'old-fold-v1' != 'groundwork-unicode-ordinal-ignore-case-v1-" +
            "3206f759667cb9cc764ec243dfb3d322a39970184efab619e80163c36d86818f'.",
            searchKeyRefusal.Message);
        var indexRefusal = Assert.Single(inspection.IndexDrift);
        Assert.Equal("GW-RUNTIME-002", indexRefusal.Code);
        Assert.Equal("indexes.ix_status", indexRefusal.Path);
        Assert.Equal(
            "Relational schema index 'search.ix_status' does not match its declaration.",
            indexRefusal.Message);
        Assert.False(inspection.IsAppliedSchemaValid);
    }

    [Fact]
    public void No_change_apply_tolerates_index_only_drift_but_still_refuses_column_drift()
    {
        var dialect = SearchDialect();
        var target = SearchTarget();
        var (executor, history) = Applied(dialect, target);
        dialect.CatalogIndexes["ix_status"] = new(
            true,
            [new RelationalIndexColumnMetadata("status", SortDirection.Descending)],
            null);

        var inspection = executor.InspectHistory(target);
        var plan = PhysicalSchemaDiffPlanner.Plan(target, history, DateTimeOffset.UnixEpoch);
        var application = PhysicalSchemaApplication.Apply(target, executor, DateTimeOffset.UnixEpoch);

        Assert.True(inspection.HasIndexDrift);
        Assert.False(inspection.HasColumnDrift);
        Assert.Empty(plan.Operations);
        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, application.Outcome);

        dialect.CatalogColumns["status"] = new("status", "varchar(255)", true, null, "Ordinal", 1);
        var refusal = Assert.Throws<InvalidOperationException>(() =>
            PhysicalSchemaApplication.Apply(target, executor, DateTimeOffset.UnixEpoch));
        Assert.Contains("collation 'Ordinal' != 'OrdinalIgnoreCase'", refusal.Message, StringComparison.Ordinal);
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
        public bool ThrowOnValidateTarget { get; set; }
        public bool TableExistsResult { get; init; }
        public string? MappedCollation { get; init; }
        public int TableExistsCalls { get; private set; }
        public int ReadColumnsCalls { get; private set; }
        public int ValidateTargetCalls { get; private set; }
        public int AssertFenceCalls { get; private set; }
        public int PublishHistoryCalls { get; private set; }
        public string? LastExpectedAppliedTargetFingerprint { get; private set; }
        public bool LastPublishHadTransaction { get; private set; }
        public ColumnDefinition? FinalizedColumn { get; private set; }
        public Dictionary<string, RelationalColumnMetadata> CatalogColumns { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, RelationalIndexMetadata> CatalogIndexes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> DerivedSearchKeyAlgorithms { get; } = new(StringComparer.Ordinal);
        public PhysicalSchemaHistoryState History { get; set; } = PhysicalSchemaHistoryState.Empty;

        public override string ProviderName => "stub";
        public override string QuoteIdentifier(string identifier) => $"\"{identifier}\"";
        public override string MapType(ColumnDefinition definition)
        {
            MapTypeCalls++;
            return definition.Type == PortableType.Int64 ? "integer" : $"varchar({definition.MaxLength ?? 255})";
        }
        public override string? MapCollation(ColumnDefinition definition) =>
            definition.Collation is null ? null : MappedCollation;
        public override string? MapDefault(ColumnDefinition definition) => null;
        public override string CreateTableSql(string table, IReadOnlyList<string> columns, IReadOnlyList<string> primaryKey) =>
            $"CREATE TABLE {QuoteIdentifier(table)} ({string.Join(", ", columns)}, PRIMARY KEY ({string.Join(", ", primaryKey.Select(QuoteIdentifier))}))";
        public override string AddColumnSql(string table, string column, string definition) => $"ADD {table}.{column} {definition}";
        public override string FinalizeColumnSql(string table, string columnName, ColumnDefinition column)
        {
            FinalizedColumn = column;
            return $"FINALIZE {table}.{columnName} {column.Type}";
        }
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
        public override void AssertFence(DbConnection connection, DbTransaction transaction, PhysicalSchemaTargetIdentity target, string owner, long fence) => AssertFenceCalls++;
        public override void EnsureInfrastructure(DbConnection connection) { }
        public override PhysicalSchemaHistoryState ReadHistory(DbConnection connection, PhysicalSchemaTargetIdentity target) => History;
        public override void PublishHistory(
            DbConnection connection,
            DbTransaction transaction,
            PhysicalSchemaTargetIdentity target,
            PhysicalSchemaAppliedState state,
            string? expectedAppliedTargetFingerprint,
            string owner,
            long fence)
        {
            PublishHistoryCalls++;
            LastExpectedAppliedTargetFingerprint = expectedAppliedTargetFingerprint;
            LastPublishHadTransaction = transaction is not null;
        }
        public override bool TableExists(DbConnection connection, DbTransaction? transaction, string table)
        {
            TableExistsCalls++;
            return TableExistsResult;
        }
        public override IReadOnlyDictionary<string, RelationalColumnMetadata> ReadColumns(DbConnection connection, DbTransaction? transaction, string table)
        {
            ReadColumnsCalls++;
            return CatalogColumns;
        }
        public override IReadOnlyDictionary<string, string> ReadDerivedSearchKeyAlgorithms(DbConnection connection, DbTransaction? transaction, string table) =>
            DerivedSearchKeyAlgorithms;
        public override RelationalIndexMetadata? ReadIndex(DbConnection connection, DbTransaction? transaction, string table, string index) =>
            CatalogIndexes.GetValueOrDefault(index);
        public override void ValidateTarget(DbConnection connection, DbTransaction? transaction, PhysicalSchemaTarget target)
        {
            ValidateTargetCalls++;
            if (ThrowOnValidateTarget)
                throw new InvalidOperationException("stub provider invariant failed");
        }

        public override string? BackfillColumnSql(string table, ColumnDefinition column) => $"BACKFILL {table}.{column.Name}";
    }

    private sealed class TrackingConnection : DbConnection
    {
        private ConnectionState state = ConnectionState.Closed;

        public int DisposeCalls { get; private set; }
        public int BeginTransactionCalls { get; private set; }
        public int CommitCalls { get; private set; }
        public int RollbackCalls { get; private set; }
        public int CommandCalls { get; private set; }
        public bool ThrowOnCommand { get; init; }
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
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        {
            BeginTransactionCalls++;
            return new TrackingTransaction(this);
        }
        protected override DbCommand CreateDbCommand() => new TrackingCommand(this);

        private sealed class TrackingTransaction(TrackingConnection connection) : DbTransaction
        {
            protected override DbConnection DbConnection => connection;
            public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
            public override void Commit() => connection.CommitCalls++;
            public override void Rollback() => connection.RollbackCalls++;
        }

        private sealed class TrackingCommand(TrackingConnection connection) : DbCommand
        {
#pragma warning disable CS8765
            public override string CommandText { get; set; } = string.Empty;
#pragma warning restore CS8765
            public override int CommandTimeout { get; set; }
            public override CommandType CommandType { get; set; }
            public override bool DesignTimeVisible { get; set; }
            public override UpdateRowSource UpdatedRowSource { get; set; }
            protected override DbConnection? DbConnection { get; set; } = connection;
            protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
            protected override DbTransaction? DbTransaction { get; set; }
            public override void Cancel() { }
            public override int ExecuteNonQuery()
            {
                connection.CommandCalls++;
                if (connection.ThrowOnCommand)
                    throw new InvalidOperationException("command failed");
                return 1;
            }
            public override object? ExecuteScalar() => throw new NotSupportedException();
            public override void Prepare() { }
            protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
        }
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

    private static PhysicalSchemaTarget CreateTarget() => new(
        new SchemaSubject(new StorageUnit
        {
            Id = new StorageUnitId("tickets"),
            Name = "tickets",
            Columns = [new ColumnDefinition { Name = "id", Type = PortableType.Int64, IsNullable = true }],
            Key = new KeyDefinition { Columns = ["id"] }
        }),
        new ProviderIdentity("stub", "1"));

    private static PhysicalSchemaTarget RuntimeTarget() => new(
        new SchemaSubject(new StorageUnit
        {
            Id = new StorageUnitId("tickets"),
            Name = "tickets",
            Columns =
            [
                new ColumnDefinition { Name = "status", Type = PortableType.String },
                new ColumnDefinition { Name = "assignee", Type = PortableType.String }
            ],
            Key = new KeyDefinition { Columns = ["status"] },
            Indexes = [new IndexDefinition { Name = "ix_status", Columns = [new IndexColumn("status")] }]
        }),
        new ProviderIdentity("stub", "1"));

    private static PhysicalSchemaTarget SearchTarget() => new(
        new SchemaSubject(new StorageUnit
        {
            Id = new StorageUnitId("search"),
            Name = "search",
            Columns =
            [
                new ColumnDefinition
                {
                    Name = "status",
                    Type = PortableType.String,
                    Collation = PortableCollation.OrdinalIgnoreCase
                },
                new ColumnDefinition { Name = "status_folded", Type = PortableType.String, IsNullable = false }
            ],
            DerivedColumns = [new DerivedColumnDefinition
            {
                Name = "status_folded",
                SourceColumn = "status",
                Projection = PortableProjection.UnicodeFold
            }],
            Key = new KeyDefinition { Columns = ["status"] },
            Indexes = [new IndexDefinition
            {
                Name = "ix_status",
                Columns = [new IndexColumn("status", SortDirection.Ascending)],
                IsUnique = true,
                MissingValues = MissingValueBehavior.Excluded
            }]
        }),
        new ProviderIdentity("stub", "1"));

    private static StubDialect SearchDialect()
    {
        var dialect = new StubDialect
        {
            TableExistsResult = true,
            MappedCollation = "OrdinalIgnoreCase"
        };
        dialect.CatalogColumns["status"] = new("status", "varchar(255)", true, null, "OrdinalIgnoreCase", 1);
        dialect.CatalogColumns["status_folded"] = new("status_folded", "varchar(255)", false, null, null, 0);
        dialect.DerivedSearchKeyAlgorithms["status_folded"] = ExpectedSearchKeyAlgorithmId;
        dialect.CatalogIndexes["ix_status"] = new(
            true,
            [new RelationalIndexColumnMetadata("status", SortDirection.Ascending)],
            "\"status\" IS NOT NULL");
        return dialect;
    }

    private static (RelationalSchemaExecutor Executor, PhysicalSchemaHistoryState History) Applied(
        StubDialect dialect,
        PhysicalSchemaTarget target)
    {
        var executor = new RelationalSchemaExecutor(() => new TrackingConnection(), dialect);
        var application = PhysicalSchemaApplication.Apply(target, executor, DateTimeOffset.UnixEpoch);
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, application.Outcome);
        var history = PhysicalSchemaHistoryState.FromApplied(application.AppliedState!);
        dialect.History = history;
        return (executor, history);
    }

    private static void SeedRuntimeCatalog(StubDialect dialect)
    {
        dialect.CatalogColumns["status"] = new("status", "varchar(255)", true, null, null, 1);
        dialect.CatalogColumns["assignee"] = new("assignee", "varchar(255)", true, null, null, 0);
        dialect.CatalogIndexes["ix_status"] = new(
            false,
            [new RelationalIndexColumnMetadata("status", SortDirection.Ascending)],
            null);
    }
}
