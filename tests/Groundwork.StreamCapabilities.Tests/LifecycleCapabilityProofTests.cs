using Groundwork.Kernel;
using Groundwork.Query.Model;
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
}
