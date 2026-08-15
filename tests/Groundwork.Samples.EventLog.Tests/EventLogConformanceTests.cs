using Groundwork.Kernel;
using Groundwork.MongoDb.TestingAdapter;
using Groundwork.PostgreSql;
using Groundwork.Samples.EventLog;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Testing;
using Groundwork.Query.Model;
using Xunit;

namespace Groundwork.Samples.EventLog.Tests;

/// <summary>
/// The second-family proof exercises the public declaration and provider-neutral runtime contract
/// together. It deliberately keeps the fixture here instead of teaching Records about event logs.
/// </summary>
public sealed class EventLogConformanceTests
{
    [Fact]
    public void InMemory_event_log_runs_schema_sequence_idempotency_retention_aggregation_and_scope_contracts()
    {
        AssertEventLog(new InMemoryProviderFactory(), "event-log-inmemory");
    }

    [Fact]
    public void SQLite_event_log_runs_schema_sequence_idempotency_retention_aggregation_and_scope_contracts()
    {
        var path = Path.Combine(Path.GetTempPath(), "groundwork-event-log-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            AssertEventLog(new SqliteProviderFactory(), $"Data Source={path}");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [SkippableFact]
    public void PostgreSQL_event_log_runs_schema_sequence_idempotency_retention_aggregation_and_scope_contracts()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION for event-log conformance.");
        AssertEventLog(new PostgreSqlProviderFactory(), connectionString!);
    }

    [SkippableFact]
    public void SQLServer_event_log_runs_schema_sequence_idempotency_retention_aggregation_and_scope_contracts()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION for event-log conformance.");
        AssertEventLog(new SqlServerProviderFactory(), connectionString!);
    }

    [SkippableFact]
    public void MongoDB_event_log_runs_schema_sequence_idempotency_retention_aggregation_and_scope_contracts()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION for event-log conformance.");
        try
        {
            AssertEventLog(new MongoDbTestingFactory(), connectionString!);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("transaction", StringComparison.OrdinalIgnoreCase))
        {
            Skip.If(true, "MongoDB event-log conformance requires a transaction-capable deployment.");
        }
    }

    private static void AssertEventLog(IStorageProviderFactory factory, string connectionString)
    {
        using var connection = factory.Create(connectionString);
        var identity = "event_log_" + Guid.NewGuid().ToString("N");
        var unit = EventLogDeclaration.LogRecords with
        {
            Id = new StorageUnitId(identity),
            Name = identity,
            Retention = EventLogDeclaration.LogRecords.Retention! with
            {
                KeepNewest = 1,
                Trigger = RetentionTrigger.Explicit
            }
        };

        var applied = connection.Schema.Apply(unit);
        Assert.True(applied.Applied);
        Assert.True(connection.Schema.Apply(unit).IsNoOp);
        Assert.Equal(unit.Indexes.Count, connection.Catalog.ReadIndexes(unit.Id).Count);

        var scopeA = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-a")));
        var scopeB = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-b")));

        var first = scopeA.Insert(Row("trace-a", "info", "first", 1));
        var second = scopeA.Insert(Row("trace-a", "warn", "second", 2));
        Assert.Equal(WriteOutcomeStatus.Inserted, first.Status);
        Assert.Equal(WriteOutcomeStatus.Inserted, second.Status);
        Assert.True(first.GeneratedValue<long>("seq") < second.GeneratedValue<long>("seq"));

        var operation = new OperationId(DateTimeOffset.UtcNow, "event-log-operation");
        Assert.Equal(WriteOutcomeStatus.Inserted, scopeA.Append(operation, Row("trace-a", "error", "appended", 3)).Status);
        Assert.Equal(WriteOutcomeStatus.Replayed, scopeA.Append(operation, Row("trace-a", "error", "replay", 4)).Status);
        Assert.Equal(WriteOutcomeStatus.Inserted, scopeB.Insert(Row("trace-b", "info", "other-scope", 5)).Status);

        var aggregate = scopeA.Aggregate(new AggregationQuery("by-trace-summary"));
        var summary = Assert.Single(aggregate.Rows);
        Assert.Equal("trace-a", summary["traceId"]);
        Assert.Equal("first", summary["firstMessage"]);
        Assert.Equal(
            new[] { "error", "info", "warn" },
            ((IEnumerable<string>)summary["levels"]!).OrderBy(value => value, StringComparer.Ordinal));

        var retention = scopeA.ApplyRetention();
        Assert.Equal(2, retention.DeletedRows);
        Assert.Single(scopeA.Query(All(unit)).Rows);
        Assert.Single(scopeB.Query(All(unit)).Rows);
    }

    private static StorageValues Row(string traceId, string level, string message, int second)
    {
        return new StorageValues(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["traceId"] = traceId,
            ["level"] = level,
            ["occurredAt"] = DateTimeOffset.UnixEpoch.AddSeconds(second),
            ["message"] = message,
            ["attributes"] = new Dictionary<string, object?> { ["attempt"] = second }
        });
    }

    private static QueryRequest All(StorageUnit unit) => new(
        new TableId(unit.Name),
        Predicate.AlwaysTrue.Instance,
        [],
        Projection.All,
        Paging.None);
}
