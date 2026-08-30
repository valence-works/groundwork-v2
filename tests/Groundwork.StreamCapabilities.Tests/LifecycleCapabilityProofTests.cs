using Microsoft.Data.SqlClient;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.LiveDatabases;
using Groundwork.Query.Model;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Store;
using Groundwork.Testing;
using Xunit;

namespace Groundwork.StreamCapabilities.Tests;

public sealed class LifecycleCapabilityProofTests
{
    [Fact]
    public void Empty_inspection_is_distinct_from_a_committed_zero_high_water()
    {
        using var connection = new InMemoryProviderFactory().Create("lifecycle-empty-" + Guid.NewGuid().ToString("N"));
        var unit = LifecycleUnit("lifecycle-empty-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        Assert.True(connection.Schema.Apply(unit).Applied);

        var inspection = connection.OpenSession(unit, StorageAccess.Global).Inspect();

        Assert.Null(inspection.LifetimeCommittedSequenceHighWater);
    }

    [Fact]
    public void Inspection_requires_a_provider_sequence_column()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("lifecycle-inspection-no-sequence-" + Guid.NewGuid().ToString("N")),
            Name = PhysicalName("lifecycle_inspection_no_sequence_" + Guid.NewGuid().ToString("N")),
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 100 },
                new() { Name = "payload", Type = PortableType.String, IsNullable = false, MaxLength = 100 }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };

        using var inMemory = new InMemoryProviderFactory().Create(unit.Name);
        Assert.True(inMemory.Schema.Apply(unit).Applied);
        var inMemoryRefusal = Assert.Throws<NotSupportedException>(() =>
            inMemory.OpenSession(unit, StorageAccess.Global).Inspect());
        Assert.StartsWith("GW-INSPECT-002", inMemoryRefusal.Message, StringComparison.Ordinal);

        using var sqlite = new SqliteProviderFactory().Create("Data Source=:memory:");
        Assert.True(sqlite.Schema.Apply(unit).Applied);
        var sqliteRefusal = Assert.Throws<NotSupportedException>(() =>
            sqlite.OpenSession(unit, StorageAccess.Global).Inspect());
        Assert.StartsWith("GW-INSPECT-002", sqliteRefusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SQL_Server_schema_admission_rejects_retention_idempotency_without_retention_before_provider_io()
    {
        var name = "lifecycle_retention_idempotency_without_retention_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = PhysicalName(name),
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 100 }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Concurrency = ConcurrencyDeclaration.None,
            RetentionIdempotency = new RetentionIdempotencyDeclaration
            {
                Window = TimeSpan.FromMinutes(1)
            }
        };

        using var connection = new SqlServerProviderFactory().Create(
            "Server=invalid-host.invalid,1433;Database=master;User Id=sa;Password=Groundwork!2026;Encrypt=False;TrustServerCertificate=True");

        var refusal = Assert.Throws<ArgumentException>(() => connection.Schema.Diff(unit));

        Assert.StartsWith("GW-RETENTION-004", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("Declare Retention", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspection_capability_refusal_precedes_unit_shape_validation()
    {
        var name = "lifecycle_inspection_capability_order_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = PhysicalName(name),
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 100 }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };

        using var connection = new InMemoryProviderFactory().Create(name);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = new CapabilityHidingSession(connection.OpenSession(unit, StorageAccess.Global));

        var refusal = Assert.Throws<NotSupportedException>(() => session.Inspect());

        Assert.StartsWith("GW-INSPECT-001", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InMemory_rejects_multiple_provider_sequence_columns_without_retention()
    {
        var name = "lifecycle_multiple_sequences_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = PhysicalName(name),
            Columns =
            [
                new() { Name = "sequence_a", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
                new() { Name = "sequence_b", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
                new() { Name = "payload", Type = PortableType.String, IsNullable = false, MaxLength = 100 }
            ],
            Key = new KeyDefinition { Columns = ["sequence_a"] }
        };

        using var connection = new InMemoryProviderFactory().Create(name);
        var refusal = Assert.Throws<InvalidOperationException>(() => connection.Schema.Apply(unit));
        Assert.Contains("GW-PORT-005", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rolled_back_generated_sequence_does_not_advance_durable_high_water()
    {
        using var connection = new InMemoryProviderFactory().Create("lifecycle-rollback-" + Guid.NewGuid().ToString("N"));
        var unit = LifecycleUnit("lifecycle-rollback-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        Assert.True(connection.Schema.Apply(unit).Applied);

        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit))
        {
            Assert.Equal(WriteOutcomeStatus.Inserted, work.OpenSession(unit).Insert(Values("rolled-back")).Status);
            work.Rollback();
        }

        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Null(session.Inspect().LifetimeCommittedSequenceHighWater);
        Assert.Equal(1L, session.Insert(Values("committed")).GeneratedValue<long>("sequence"));
        Assert.Equal(1L, session.Inspect().LifetimeCommittedSequenceHighWater);
    }

    [Fact]
    public void InMemory_inspection_preserves_scoped_high_water_after_retention_and_restart()
    {
        var factory = new InMemoryProviderFactory();
        var connectionString = "lifecycle-inspection-" + Guid.NewGuid().ToString("N");
        var unit = LifecycleUnit("lifecycle-inspection-" + Guid.NewGuid().ToString("N"), ScopePolicy.Scoped);

        using (var first = factory.Create(connectionString))
        {
            Assert.True(first.Schema.Apply(unit).Applied);
            var scopeA = first.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-a")));
            var scopeB = first.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-b")));

            Assert.Equal(1L, scopeA.Insert(Values("a-1")).GeneratedValue<long>("sequence"));
            Assert.Equal(2L, scopeA.Insert(Values("a-2")).GeneratedValue<long>("sequence"));
            Assert.Equal(3L, scopeB.Insert(Values("b-1")).GeneratedValue<long>("sequence"));
            Assert.Equal(2L, scopeA.Inspect().LifetimeCommittedSequenceHighWater);
            Assert.Equal(3L, scopeB.Inspect().LifetimeCommittedSequenceHighWater);

            var operation = new OperationId(DateTimeOffset.UtcNow, "scope-a-retention");
            var result = scopeA.ApplyRetention(operation, new RetentionExecutionOptions
            {
                MaxRowsPerBatch = 1,
                KeepNewestOverride = 0
            });
            Assert.Equal(RetentionOperationStatus.Executed, result.Status);
            Assert.Equal(2, result.DeletedRows);
            Assert.Equal(2L, scopeA.Inspect().LifetimeCommittedSequenceHighWater);
        }

        using var restarted = factory.Create(connectionString);
        var reopenedA = restarted.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-a")));
        var reopenedB = restarted.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-b")));
        Assert.Equal(2L, reopenedA.Inspect().LifetimeCommittedSequenceHighWater);
        Assert.Equal(3L, reopenedB.Inspect().LifetimeCommittedSequenceHighWater);
        Assert.Empty(reopenedA.Query(All(unit)).Rows);
    }

    [Fact]
    public void InMemory_exact_retention_replays_result_and_refuses_changed_request()
    {
        using var connection = new InMemoryProviderFactory().Create("lifecycle-retention-" + Guid.NewGuid().ToString("N"));
        var unit = LifecycleUnit("lifecycle-retention-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(Values("first"));
        session.Insert(Values("second"));
        session.Insert(Values("third"));

        var operation = new OperationId(DateTimeOffset.UtcNow, "retention-replay");
        var executed = session.ApplyRetention(operation, new RetentionExecutionOptions { MaxRowsPerBatch = 1 });
        Assert.Equal(RetentionOperationStatus.Executed, executed.Status);
        Assert.Equal(2, executed.DeletedRows);

        var replayed = session.ApplyRetention(operation, new RetentionExecutionOptions { MaxRowsPerBatch = 1 });
        Assert.Equal(RetentionOperationStatus.Replayed, replayed.Status);
        Assert.Equal(executed.DeletedRows, replayed.DeletedRows);
        Assert.Equal(executed.Batches, replayed.Batches);

        var conflict = Assert.Throws<RetentionIdempotencyConflictException>(() =>
            session.ApplyRetention(operation, new RetentionExecutionOptions { MaxRowsPerBatch = 2 }));
        Assert.StartsWith(RetentionIdempotencyConflictException.DiagnosticCode, conflict.Message, StringComparison.Ordinal);
        Assert.Single(session.Query(All(unit)).Rows);
    }

    [Fact]
    public void InMemory_exact_retention_with_zero_keep_deletes_all_rows_and_replays()
    {
        using var connection = new InMemoryProviderFactory().Create("lifecycle-retention-delete-all-" + Guid.NewGuid().ToString("N"));
        var unit = LifecycleUnit("lifecycle-retention-delete-all-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global, keepNewest: 0);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(Values("first"));
        session.Insert(Values("second"));
        session.Insert(Values("third"));
        var before = session.Inspect().LifetimeCommittedSequenceHighWater;

        var operation = new OperationId(DateTimeOffset.UtcNow, "retention-delete-all");
        var executed = session.ApplyRetention(operation, new RetentionExecutionOptions { MaxRowsPerBatch = 1 });
        var replayed = session.ApplyRetention(operation, new RetentionExecutionOptions { MaxRowsPerBatch = 1 });

        Assert.Equal(3L, before);
        Assert.Equal(RetentionOperationStatus.Executed, executed.Status);
        Assert.Equal(3, executed.DeletedRows);
        Assert.Equal(3, executed.Batches);
        Assert.Equal(RetentionOperationStatus.Replayed, replayed.Status);
        Assert.Equal(executed with { Status = RetentionOperationStatus.Replayed }, replayed);
        var conflict = Assert.Throws<RetentionIdempotencyConflictException>(() => session.ApplyRetention(
            operation,
            new RetentionExecutionOptions { MaxRowsPerBatch = 2 }));
        Assert.StartsWith(RetentionIdempotencyConflictException.DiagnosticCode, conflict.Message, StringComparison.Ordinal);
        Assert.Equal(before, session.Inspect().LifetimeCommittedSequenceHighWater);
        Assert.Empty(session.Query(All(unit)).Rows);
    }

    [Fact]
    public void InMemory_exact_retention_honors_a_positive_runtime_keep_override()
    {
        using var connection = new InMemoryProviderFactory().Create("lifecycle-retention-override-" + Guid.NewGuid().ToString("N"));
        var unit = LifecycleUnit("lifecycle-retention-override-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        for (var index = 0; index < 4; index++)
            session.Insert(Values($"override-{index}"));

        var operation = new OperationId(DateTimeOffset.UtcNow, "retention-override");
        var options = new RetentionExecutionOptions { KeepNewestOverride = 2, MaxRowsPerBatch = 1 };
        var executed = session.ApplyRetention(operation, options);
        var replayed = session.ApplyRetention(operation, options);

        Assert.Equal(RetentionOperationStatus.Executed, executed.Status);
        Assert.Equal(2, executed.DeletedRows);
        Assert.Equal(RetentionOperationStatus.Replayed, replayed.Status);
        Assert.Equal(executed with { Status = RetentionOperationStatus.Replayed }, replayed);
        Assert.Throws<RetentionIdempotencyConflictException>(() => session.ApplyRetention(
            operation,
            options with { KeepNewestOverride = 1 }));
        Assert.Equal(4L, session.Inspect().LifetimeCommittedSequenceHighWater);
        Assert.Equal(2, session.Query(All(unit)).Rows.Count);
    }

    [Fact]
    public void InMemory_exact_retention_zero_runtime_override_deletes_all_even_when_declaration_keeps_one()
    {
        using var connection = new InMemoryProviderFactory().Create("lifecycle-retention-override-zero-" + Guid.NewGuid().ToString("N"));
        var unit = LifecycleUnit("lifecycle-retention-override-zero-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        for (var index = 0; index < 3; index++)
            session.Insert(Values($"override-zero-{index}"));

        var result = session.ApplyRetention(
            new OperationId(DateTimeOffset.UtcNow, "retention-override-zero"),
            new RetentionExecutionOptions { KeepNewestOverride = 0, MaxRowsPerBatch = 1 });

        Assert.Equal(RetentionOperationStatus.Executed, result.Status);
        Assert.Equal(3, result.DeletedRows);
        Assert.Equal(3L, session.Inspect().LifetimeCommittedSequenceHighWater);
        Assert.Empty(session.Query(All(unit)).Rows);
    }

    [Fact]
    public void Reference_retention_honors_a_runtime_keep_override()
    {
        using var connection = new InMemoryProviderFactory().Create("lifecycle-retention-reference-override-" + Guid.NewGuid().ToString("N"));
        var unit = LifecycleUnit("lifecycle-retention-reference-override-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var native = connection.OpenSession(unit, StorageAccess.Global);
        for (var index = 0; index < 4; index++)
            native.Insert(Values($"reference-override-{index}"));

        var reference = new CapabilityHidingSession(native);
        var result = reference.ApplyRetention(new RetentionExecutionOptions { KeepNewestOverride = 2 });

        Assert.Equal(2, result.DeletedRows);
        Assert.Equal(2, reference.Query(All(unit)).Rows.Count);
    }

    [Fact]
    public void Negative_runtime_keep_override_is_rejected_before_provider_dispatch()
    {
        using var connection = new InMemoryProviderFactory().Create("lifecycle-retention-invalid-override-" + Guid.NewGuid().ToString("N"));
        var unit = LifecycleUnit("lifecycle-retention-invalid-override-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = new CapabilityHidingSession(connection.OpenSession(unit, StorageAccess.Global));

        Assert.Throws<ArgumentOutOfRangeException>(() => session.ApplyRetention(
            new RetentionExecutionOptions { KeepNewestOverride = -1 }));
    }

    [Fact]
    public void InMemory_on_append_zero_keep_deletes_all_rows_automatically()
    {
        using var connection = new InMemoryProviderFactory().Create("lifecycle-retention-on-append-delete-all-" + Guid.NewGuid().ToString("N"));
        var unit = LifecycleUnit("lifecycle-retention-on-append-delete-all-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global, keepNewest: 0, trigger: RetentionTrigger.OnAppend);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);

        session.Insert(Values("first"));
        session.Insert(Values("second"));

        Assert.Empty(session.Query(All(unit)).Rows);
        Assert.Equal(2L, session.Inspect().LifetimeCommittedSequenceHighWater);
    }

    [Fact]
    public void Exact_retention_is_atomic_when_cancellation_arrives_after_a_delete_batch()
    {
        using var connection = new InMemoryProviderFactory().Create("lifecycle-retention-atomic-" + Guid.NewGuid().ToString("N"));
        var unit = LifecycleUnit("lifecycle-retention-atomic-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global, keepNewest: 0);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        for (var index = 0; index < 5; index++)
            session.Insert(Values($"row-{index}"));

        using var cancellation = new CancellationTokenSource();
        var observer = new CancelAfterFirstRetentionBatch(cancellation);
        var operation = new OperationId(DateTimeOffset.UtcNow, "retention-atomic");
        // The observer cancels on its first command, so it gets a session of its own: attached to the one
        // above it would fire on the seeding inserts instead of on the retention pass under test.
        var observedSession = connection.OpenSession(unit, StorageAccess.Global, observer);
        Assert.Throws<OperationCanceledException>(() => observedSession.ApplyRetention(operation, new RetentionExecutionOptions
        {
            MaxRowsPerBatch = 1,
            CancellationToken = cancellation.Token
        }));
        Assert.Equal(5, session.Query(All(unit)).Rows.Count);

        var executed = session.ApplyRetention(operation, new RetentionExecutionOptions { MaxRowsPerBatch = 1 });
        Assert.Equal(RetentionOperationStatus.Executed, executed.Status);
        Assert.Equal(5, executed.DeletedRows);
        var replayed = session.ApplyRetention(operation, new RetentionExecutionOptions { MaxRowsPerBatch = 1 });
        Assert.Equal(executed.DeletedRows, replayed.DeletedRows);
        Assert.Equal(RetentionOperationStatus.Replayed, replayed.Status);
    }

    [Fact]
    public void Exact_retention_composes_with_a_batched_unit_of_work()
    {
        using var connection = new InMemoryProviderFactory().Create("lifecycle-retention-uow-" + Guid.NewGuid().ToString("N"));
        var unit = LifecycleUnit("lifecycle-retention-uow-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        Assert.True(connection.Schema.Apply(unit).Applied);

        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        var session = work.OpenSession(unit);
        session.Insert(Values("first"));
        session.Insert(Values("second"));
        var operation = new OperationId(DateTimeOffset.UtcNow, "retention-uow");
        var executed = session.ApplyRetention(operation, new RetentionExecutionOptions { MaxRowsPerBatch = 1 });
        Assert.Equal(RetentionOperationStatus.Executed, executed.Status);
        work.Commit();
        // The unit-of-work assertion above proves the wrapper dispatches exact retention inside
        // the transaction; direct replay is covered by the provider restart proof.
        Assert.Equal(1, executed.DeletedRows);
    }

    [Fact]
    public void Exact_retention_is_atomic_across_multiple_units_in_a_unit_of_work()
    {
        using var connection = new InMemoryProviderFactory().Create("lifecycle-retention-multi-uow-" + Guid.NewGuid().ToString("N"));
        var first = LifecycleUnit("lifecycle-retention-multi-a-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        var second = LifecycleUnit("lifecycle-retention-multi-b-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        Assert.True(connection.Schema.Apply(first).Applied);
        Assert.True(connection.Schema.Apply(second).Applied);

        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, first, second))
        {
            var firstSession = work.OpenSession(first);
            var secondSession = work.OpenSession(second);
            firstSession.Insert(Values("a-1"));
            firstSession.Insert(Values("a-2"));
            secondSession.Insert(Values("b-1"));
            secondSession.Insert(Values("b-2"));
            Assert.Equal(RetentionOperationStatus.Executed, firstSession.ApplyRetention(
                new OperationId(DateTimeOffset.UtcNow, "multi-a"), new RetentionExecutionOptions { MaxRowsPerBatch = 1 }).Status);
            Assert.Equal(RetentionOperationStatus.Executed, secondSession.ApplyRetention(
                new OperationId(DateTimeOffset.UtcNow, "multi-b"), new RetentionExecutionOptions { MaxRowsPerBatch = 1 }).Status);
            work.Commit();
        }

        Assert.Single(connection.OpenSession(first, StorageAccess.Global).Query(All(first)).Rows);
        Assert.Single(connection.OpenSession(second, StorageAccess.Global).Query(All(second)).Rows);
    }

    [Fact]
    public void SQLite_lifecycle_capabilities_preserve_high_water_and_exact_retention_across_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), "groundwork-lifecycle-" + Guid.NewGuid().ToString("N") + ".db");
        var unit = LifecycleUnit("lifecycle-sqlite-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        try
        {
            using (var connection = new SqliteProviderFactory().Create($"Data Source={path}"))
            {
                Assert.True(connection.Schema.Apply(unit).Applied);
                Assert.Contains(connection.Capabilities, capability => capability.Id == BatchWriteCapabilities.DurableHighWaterInspection);
                Assert.Contains(connection.Capabilities, capability => capability.Id == BatchWriteCapabilities.ExactRetention);
                var session = connection.OpenSession(unit, StorageAccess.Global);
                session.Insert(Values("first"));
                session.Insert(Values("second"));
                session.Insert(Values("third"));
                Assert.Equal(3L, session.Inspect().LifetimeCommittedSequenceHighWater);

                var operation = new OperationId(DateTimeOffset.UtcNow, "sqlite-retention-replay");
                var executed = session.ApplyRetention(operation, new RetentionExecutionOptions { MaxRowsPerBatch = 1 });
                var replayed = session.ApplyRetention(operation, new RetentionExecutionOptions { MaxRowsPerBatch = 1 });
                Assert.Equal(RetentionOperationStatus.Executed, executed.Status);
                Assert.Equal(RetentionOperationStatus.Replayed, replayed.Status);
                Assert.Equal(executed.DeletedRows, replayed.DeletedRows);
                AssertDeleteAllLifecycle(connection, "sqlite");
            }

            using var restarted = new SqliteProviderFactory().Create($"Data Source={path}");
            var reopened = restarted.OpenSession(unit, StorageAccess.Global);
            Assert.Equal(3L, reopened.Inspect().LifetimeCommittedSequenceHighWater);
            Assert.Single(reopened.Query(All(unit)).Rows);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [SkippableFact]
    public void PostgreSQL_lifecycle_capabilities_preserve_high_water_and_exact_retention()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run the PostgreSQL lifecycle proof.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertNativeLifecycle(connection, "postgresql");
    }

    [SkippableFact]
    public void SQLServer_lifecycle_capabilities_preserve_high_water_and_exact_retention()
    {
        var connectionString = LiveSqlServer.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run the SQL Server lifecycle proof.");
        using var database = SqlServerDatabaseLease.Create(connectionString!);
        AssertSqlServerLegacyLifecycleRefusal(database.ConnectionString);
        using var connection = new SqlServerProviderFactory().Create(database.ConnectionString);
        AssertNativeLifecycle(connection, "sqlserver");
    }

    [SkippableFact]
    public void MongoDB_lifecycle_capabilities_preserve_high_water_and_exact_retention()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run the MongoDB lifecycle proof.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        Skip.If(!connection.Capabilities.Any(capability => capability.Id == BatchWriteCapabilities.ExactRetention),
            "MongoDB deployment does not advertise transaction-backed exact retention.");
        AssertNativeLifecycle(connection, "mongodb");
    }

    [SkippableFact]
    public void MongoDB_unit_of_work_inspection_reads_transactional_high_water()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run the MongoDB transactional inspection proof.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        Skip.If(!connection.Capabilities.Any(capability => capability.Id == BatchWriteCapabilities.DurableHighWaterInspection),
            "MongoDB deployment does not advertise transaction-backed high-water inspection.");

        var unit = LifecycleUnit("lifecycle-mongo-uow-inspection-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        Assert.True(connection.Schema.Apply(unit).Applied);
        using var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        var session = work.OpenSession(unit);
        Assert.Equal(1L, session.Insert(Values("transactional")).GeneratedValue<long>("sequence"));
        Assert.Equal(1L, session.Inspect().LifetimeCommittedSequenceHighWater);
        work.Rollback();

        Assert.Null(connection.OpenSession(unit, StorageAccess.Global).Inspect().LifetimeCommittedSequenceHighWater);
    }

    [SkippableFact]
    public void MongoDB_without_transaction_fit_refuses_lifecycle_capabilities_before_dispatch()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_STANDALONE_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_STANDALONE_CONNECTION to run the MongoDB lifecycle capability refusal proof.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        Skip.If(connection.Capabilities.Any(capability =>
                capability.Id == BatchWriteCapabilities.DurableHighWaterInspection ||
                capability.Id == BatchWriteCapabilities.ExactRetention),
            "The configured MongoDB deployment supports transaction-backed lifecycle capabilities; this proof targets standalone refusal.");

        var unit = LifecycleUnit("lifecycle-mongo-unsupported-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global) with
        {
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, IsNullable = false },
                new() { Name = "payload", Type = PortableType.String, IsNullable = false, MaxLength = 200 }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Retention = new RetentionDeclaration
            {
                KeepNewest = 1,
                OrderColumn = "id",
                Trigger = RetentionTrigger.Explicit
            }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);

        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.False(session is IStorageInspectionSession);
        Assert.False(session is IExactRetentionStorageSession);

        var inspectionRefusal = Assert.Throws<NotSupportedException>(() => session.Inspect());
        Assert.StartsWith("GW-INSPECT-001", inspectionRefusal.Message, StringComparison.Ordinal);

        var retentionRefusal = Assert.Throws<NotSupportedException>(() => session.ApplyRetention(
            new OperationId(DateTimeOffset.UtcNow, "unsupported-lifecycle"),
            new RetentionExecutionOptions()));
        Assert.StartsWith("GW-RETENTION-003", retentionRefusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Lifecycle_capabilities_are_advertised_as_distinct_optional_contracts()
    {
        using var connection = new InMemoryProviderFactory().Create("lifecycle-capabilities-" + Guid.NewGuid().ToString("N"));
        var unit = LifecycleUnit("lifecycle-capabilities-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);

        Assert.IsAssignableFrom<IStorageInspectionSession>(session);
        Assert.IsAssignableFrom<IExactRetentionStorageSession>(session);
        Assert.Contains(connection.Capabilities, capability => capability.Id == BatchWriteCapabilities.DurableHighWaterInspection);
        Assert.Contains(connection.Capabilities, capability => capability.Id == BatchWriteCapabilities.ExactRetention);
    }

    [Fact]
    public void Retention_idempotency_window_and_ledger_are_schema_identity()
    {
        using var connection = new InMemoryProviderFactory().Create("lifecycle-retention-drift-" + Guid.NewGuid().ToString("N"));
        var unit = LifecycleUnit("lifecycle-retention-drift-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var changed = unit with
        {
            RetentionIdempotency = unit.RetentionIdempotency! with { Window = TimeSpan.FromHours(2), LedgerName = "retention_other" }
        };
        var conflict = Assert.Throws<PhysicalSchemaPlanRefusedException>(() => connection.Schema.Apply(changed));
        Assert.Contains("retention idempotency", conflict.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static StorageUnit LifecycleUnit(
        string name,
        ScopePolicy scope,
        int keepNewest = 1,
        RetentionTrigger trigger = RetentionTrigger.Explicit) => new()
    {
        Id = new StorageUnitId(name),
        Name = PhysicalName(name),
        Scope = scope,
        Columns =
        [
            new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
            new() { Name = "payload", Type = PortableType.String, IsNullable = false, MaxLength = 200 }
        ],
        Key = new KeyDefinition { Columns = ["sequence"] },
        AppendIdempotency = new AppendIdempotencyDeclaration { Window = TimeSpan.FromMinutes(10) },
        RetentionIdempotency = new RetentionIdempotencyDeclaration { Window = TimeSpan.FromMinutes(10) },
        Retention = new RetentionDeclaration
        {
            KeepNewest = keepNewest,
            OrderColumn = "sequence",
            Trigger = trigger
        }
    };

    private static string PhysicalName(string name)
    {
        var normalized = name.Replace('-', '_');
        return normalized.Length <= PortabilityValidator.MaximumPortableIdentifierLength
            ? normalized
            : normalized[..30] + "_" + normalized[^32..];
    }

    private static StorageValues Values(string payload) =>
        new(new Dictionary<string, object?> { ["payload"] = payload });

    private static QueryRequest All(StorageUnit unit) => new(
        new TableId(unit.Name),
        Predicate.AlwaysTrue.Instance,
        [],
        Projection.All,
        Paging.None);

    private static void AssertNativeLifecycle(IStorageProviderConnection connection, string provider)
    {
        var unit = LifecycleUnit($"lifecycle-{provider}-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        Assert.True(connection.Schema.Apply(unit).Applied);
        Assert.Contains(connection.Capabilities, capability => capability.Id == BatchWriteCapabilities.DurableHighWaterInspection);
        Assert.Contains(connection.Capabilities, capability => capability.Id == BatchWriteCapabilities.ExactRetention);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(Values("first"));
        session.Insert(Values("second"));
        session.Insert(Values("third"));
        Assert.Equal(3L, session.Inspect().LifetimeCommittedSequenceHighWater);
        var operation = new OperationId(DateTimeOffset.UtcNow, $"{provider}-retention");
        var executed = session.ApplyRetention(operation, new RetentionExecutionOptions { MaxRowsPerBatch = 1 });
        var replayed = session.ApplyRetention(operation, new RetentionExecutionOptions { MaxRowsPerBatch = 1 });
        Assert.Equal(RetentionOperationStatus.Executed, executed.Status);
        Assert.Equal(RetentionOperationStatus.Replayed, replayed.Status);
        Assert.Equal(executed.DeletedRows, replayed.DeletedRows);
        Assert.Single(session.Query(All(unit)).Rows);
        AssertDeleteAllLifecycle(connection, provider);
    }

    private static void AssertDeleteAllLifecycle(IStorageProviderConnection connection, string provider)
    {
        var unit = LifecycleUnit($"s7-{provider}-override-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Insert(Values("first"));
        session.Insert(Values("second"));
        session.Insert(Values("third"));
        session.Insert(Values("fourth"));
        var highWater = session.Inspect().LifetimeCommittedSequenceHighWater;

        var operation = new OperationId(DateTimeOffset.UtcNow, $"{provider}-retention-override");
        var commandObserver = new RecordingRetentionObserver();
        var options = new RetentionExecutionOptions { KeepNewestOverride = 2, MaxRowsPerBatch = 1 };
        // Observed on its own session so the recording covers the retention passes, not the seeding inserts.
        var retentionSession = connection.OpenSession(unit, StorageAccess.Global, commandObserver);
        var executed = retentionSession.ApplyRetention(operation, options);
        var replayed = retentionSession.ApplyRetention(operation, options);

        Assert.Equal(4L, highWater);
        Assert.Equal(RetentionOperationStatus.Executed, executed.Status);
        Assert.Equal(2, executed.DeletedRows);
        Assert.Equal(RetentionOperationStatus.Replayed, replayed.Status);
        Assert.Equal(executed with { Status = RetentionOperationStatus.Replayed }, replayed);
        Assert.Throws<RetentionIdempotencyConflictException>(() => session.ApplyRetention(
            operation,
            options with { KeepNewestOverride = 1 }));
        Assert.InRange(commandObserver.Events.Count, 2, 5);
        Assert.All(commandObserver.Events, item =>
            Assert.Contains("retention", item.Operation, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(commandObserver.Events, item => item.Operation.Contains("query", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(highWater, session.Inspect().LifetimeCommittedSequenceHighWater);
        Assert.Equal(2, session.Query(All(unit)).Rows.Count);

        var cancellationUnit = LifecycleUnit($"s7-{provider}-cancel-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        Assert.True(connection.Schema.Apply(cancellationUnit).Applied);
        var cancellationSession = connection.OpenSession(cancellationUnit, StorageAccess.Global);
        for (var index = 0; index < 5; index++)
            cancellationSession.Insert(Values($"cancel-{index}"));
        using var cancellation = new CancellationTokenSource();
        var observer = new CancelAfterFirstRetentionBatch(cancellation);
        var observedCancellationSession = connection.OpenSession(cancellationUnit, StorageAccess.Global, observer);
        var cancellationOperation = new OperationId(DateTimeOffset.UtcNow, $"{provider}-retention-cancel");
        Assert.Throws<OperationCanceledException>(() => observedCancellationSession.ApplyRetention(
            cancellationOperation,
            new RetentionExecutionOptions
            {
                MaxRowsPerBatch = 1,
                KeepNewestOverride = 0,
                CancellationToken = cancellation.Token
            }));
        Assert.Equal(5, cancellationSession.Query(All(cancellationUnit)).Rows.Count);
        var resumed = cancellationSession.ApplyRetention(
            cancellationOperation,
            new RetentionExecutionOptions { MaxRowsPerBatch = 1, KeepNewestOverride = 0 });
        var resumedReplay = cancellationSession.ApplyRetention(
            cancellationOperation,
            new RetentionExecutionOptions { MaxRowsPerBatch = 1, KeepNewestOverride = 0 });
        Assert.Equal(5, resumed.DeletedRows);
        Assert.Equal(RetentionOperationStatus.Replayed, resumedReplay.Status);
        Assert.Equal(resumed.DeletedRows, resumedReplay.DeletedRows);
        Assert.Empty(cancellationSession.Query(All(cancellationUnit)).Rows);
    }

    private static void AssertSqlServerLegacyLifecycleRefusal(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE [__groundwork_sequence_high_waters]
                (
                    [unit] nvarchar(450) NOT NULL,
                    [scope] nvarchar(128) NOT NULL,
                    [lifetime_sequence_high_water] bigint NOT NULL,
                    PRIMARY KEY NONCLUSTERED ([unit], [scope])
                );
                """;
            create.ExecuteNonQuery();
        }

        var unit = LifecycleUnit("lifecycle-sqlserver-legacy-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        using (var provider = new SqlServerProviderFactory().Create(connectionString))
        {
            Assert.True(provider.Schema.Apply(unit).Applied);
            var refusal = Assert.Throws<InvalidOperationException>(() =>
                provider.OpenSession(unit, StorageAccess.Global).Inspect());
            Assert.StartsWith("GW-SQLSERVER-LIFECYCLE-001", refusal.Message, StringComparison.Ordinal);
        }

        using var drop = connection.CreateCommand();
        drop.CommandText = "DROP TABLE [__groundwork_sequence_high_waters];";
        drop.ExecuteNonQuery();
    }

    private sealed class SqlServerDatabaseLease : IDisposable
    {
        private readonly string masterConnectionString;
        private bool disposed;

        private SqlServerDatabaseLease(string connectionString, string masterConnectionString, string name)
        {
            ConnectionString = connectionString;
            this.masterConnectionString = masterConnectionString;
            Name = name;
        }

        public string ConnectionString { get; }

        private string Name { get; }

        public static SqlServerDatabaseLease Create(string baseConnectionString)
        {
            var name = "groundwork_s7_" + Guid.NewGuid().ToString("N");
            var master = new SqlConnectionStringBuilder(baseConnectionString)
            {
                InitialCatalog = "master"
            };
            using (var connection = new SqlConnection(master.ConnectionString))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"CREATE DATABASE [{name}];";
                command.ExecuteNonQuery();
            }

            var database = new SqlConnectionStringBuilder(baseConnectionString)
            {
                InitialCatalog = name
            };
            return new SqlServerDatabaseLease(database.ConnectionString, master.ConnectionString, name);
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            try
            {
                using var connection = new SqlConnection(masterConnectionString);
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = $"ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}];";
                command.ExecuteNonQuery();
            }
            catch (SqlException)
            {
                // The live service may reclaim an isolated database during teardown.
            }
        }
    }

    private sealed class CancelAfterFirstRetentionBatch(CancellationTokenSource cancellation) : IProviderCommandObserver
    {
        private int batches;

        public void Observe(ProviderCommandEvent command)
        {
            if (command.Operation.Contains("retention", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Increment(ref batches) == 1)
                cancellation.Cancel();
        }
    }

    private sealed class RecordingRetentionObserver : IProviderCommandObserver
    {
        public List<ProviderCommandEvent> Events { get; } = [];

        public void Observe(ProviderCommandEvent command) => Events.Add(command);
    }

    private sealed class CapabilityHidingSession(IStorageSession inner) : IStorageSession
    {
        public StorageUnit Unit => inner.Unit;
        public StorageAccess Access => inner.Access;
        public StoredEntry? Read(StorageKey key) => inner.Read(key);
        public ValueTask<StoredEntry?> ReadAsync(StorageKey key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null) => inner.Query(request, options);
        public ValueTask<QueryMaterializedResult> QueryAsync(QueryRequest request, QueryRenderOptions? options = null, CancellationToken cancellationToken = default) => inner.QueryAsync(request, options, cancellationToken);
        public AggregationResult Aggregate(AggregationQuery query) => inner.Aggregate(query);
        public ValueTask<AggregationResult> AggregateAsync(AggregationQuery query, CancellationToken cancellationToken = default) => inner.AggregateAsync(query, cancellationToken);
        public WriteOutcome Insert(StorageValues values, WriteOptions? options = null) => inner.Insert(values, options);
        public ValueTask<WriteOutcome> InsertAsync(StorageValues values, WriteOptions? options = null, CancellationToken cancellationToken = default) => inner.InsertAsync(values, options, cancellationToken);
        public WriteOutcome Update(StorageValues values, WriteOptions? options = null) => inner.Update(values, options);
        public ValueTask<WriteOutcome> UpdateAsync(StorageValues values, WriteOptions? options = null, CancellationToken cancellationToken = default) => inner.UpdateAsync(values, options, cancellationToken);
        public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null) => inner.Upsert(values, options);
        public ValueTask<WriteOutcome> UpsertAsync(StorageValues values, WriteOptions? options = null, CancellationToken cancellationToken = default) => inner.UpsertAsync(values, options, cancellationToken);
        public WriteOutcome Delete(StorageKey key, WriteOptions? options = null) => inner.Delete(key, options);
        public ValueTask<WriteOutcome> DeleteAsync(StorageKey key, WriteOptions? options = null, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, options, cancellationToken);
        public WriteOutcome Append(OperationId operationId, IReadOnlyList<StorageValues> values) => inner.Append(operationId, values);
        public ValueTask<WriteOutcome> AppendAsync(OperationId operationId, IReadOnlyList<StorageValues> values, CancellationToken cancellationToken = default) => inner.AppendAsync(operationId, values, cancellationToken);
    }
}
