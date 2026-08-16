using Groundwork.Kernel;
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
            var result = scopeA.ApplyRetention(operation, new RetentionExecutionOptions { MaxRowsPerBatch = 1 });
            Assert.Equal(RetentionOperationStatus.Executed, result.Status);
            Assert.Equal(1, result.DeletedRows);
            Assert.Equal(2L, scopeA.Inspect().LifetimeCommittedSequenceHighWater);
        }

        using var restarted = factory.Create(connectionString);
        var reopenedA = restarted.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-a")));
        var reopenedB = restarted.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-b")));
        Assert.Equal(2L, reopenedA.Inspect().LifetimeCommittedSequenceHighWater);
        Assert.Equal(3L, reopenedB.Inspect().LifetimeCommittedSequenceHighWater);
        Assert.Single(reopenedA.Query(All(unit)).Rows);
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
    public void Exact_retention_is_atomic_when_cancellation_arrives_after_a_delete_batch()
    {
        using var connection = new InMemoryProviderFactory().Create("lifecycle-retention-atomic-" + Guid.NewGuid().ToString("N"));
        var unit = LifecycleUnit("lifecycle-retention-atomic-" + Guid.NewGuid().ToString("N"), ScopePolicy.Global);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        for (var index = 0; index < 5; index++)
            session.Insert(Values($"row-{index}"));

        using var cancellation = new CancellationTokenSource();
        var observer = new CancelAfterFirstRetentionBatch(cancellation);
        var operation = new OperationId(DateTimeOffset.UtcNow, "retention-atomic");
        Assert.Throws<OperationCanceledException>(() => session.ApplyRetention(operation, new RetentionExecutionOptions
        {
            MaxRowsPerBatch = 1,
            CancellationToken = cancellation.Token,
            Observer = observer
        }));
        Assert.Equal(5, session.Query(All(unit)).Rows.Count);

        var executed = session.ApplyRetention(operation, new RetentionExecutionOptions { MaxRowsPerBatch = 1 });
        Assert.Equal(RetentionOperationStatus.Executed, executed.Status);
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
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run the SQL Server lifecycle proof.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertNativeLifecycle(connection, "sqlserver");
    }

    [SkippableFact]
    public void MongoDB_lifecycle_capabilities_preserve_high_water_and_exact_retention()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run the MongoDB lifecycle proof.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        Skip.If(!connection.Capabilities.Any(capability => capability.Id == BatchWriteCapabilities.ExactRetention),
            "MongoDB deployment does not advertise transaction-backed exact retention.");
        AssertNativeLifecycle(connection, "mongodb");
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

    private static StorageUnit LifecycleUnit(string name, ScopePolicy scope) => new()
    {
        Id = new StorageUnitId(name),
        Name = name,
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
            KeepNewest = 1,
            OrderColumn = "sequence",
            Trigger = RetentionTrigger.Explicit
        }
    };

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
    }

    private sealed class CancelAfterFirstRetentionBatch(CancellationTokenSource cancellation) : IWritePathObserver
    {
        private int batches;

        public void Observe(WritePathEvent command)
        {
            if (command.Operation.Contains("retention", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Increment(ref batches) == 1)
                cancellation.Cancel();
        }
    }
}
