using System.Diagnostics;
using Groundwork.Kernel;
using Groundwork.MongoDb.TestingAdapter;
using Groundwork.PostgreSql;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Testing;
using Groundwork.Query.Model;
using Xunit;

namespace Groundwork.StreamCapabilities.Tests;

public sealed class RetentionProofTests
{
    [Fact]
    public void InMemory_retention_keeps_newest_rows_per_partition_after_heavy_churn()
    {
        using var connection = new InMemoryProviderFactory().Create("stream-retention-inmemory-" + Guid.NewGuid().ToString("N"));
        AssertRetention(connection, "inmemory");
    }

    [Fact]
    public void SQLite_retention_uses_bounded_native_delete_and_keeps_partition_watermarks()
    {
        var path = Path.Combine(Path.GetTempPath(), "groundwork-stream-retention-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var connection = new SqliteProviderFactory().Create($"Data Source={path}");
            AssertRetention(connection, "sqlite");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [SkippableFact]
    public void PostgreSQL_retention_keeps_newest_rows_per_partition()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run the PostgreSQL retention proof.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertRetention(connection, "postgresql");
    }

    [SkippableFact]
    public void SQLServer_retention_keeps_newest_rows_per_partition()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run the SQL Server retention proof.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertRetention(connection, "sqlserver");
    }

    [SkippableFact]
    public void MongoDB_retention_uses_bounded_deleteMany_and_keeps_newest_rows_per_partition()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run the MongoDB retention proof.");
        using var connection = new MongoDbTestingFactory().Create(connectionString!);
        AssertRetention(connection, "mongodb");
    }

    [Fact]
    public void OnAppend_retention_is_bounded_and_resumable()
    {
        using var connection = new InMemoryProviderFactory().Create("stream-retention-trigger-" + Guid.NewGuid().ToString("N"));
        var unit = RetentionUnit("stream-retention-trigger-" + Guid.NewGuid().ToString("N"), RetentionTrigger.OnAppend);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var stopwatch = Stopwatch.StartNew();
        Parallel.For(0, 64, index => session.Insert(Values(index % 2 == 0 ? "a" : "b")));
        stopwatch.Stop();
        Assert.InRange(stopwatch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.Equal(6, session.Query(All(unit)).Rows.Count);

        var interruptedUnit = RetentionUnit(
            "stream-retention-resume-" + Guid.NewGuid().ToString("N"), RetentionTrigger.Explicit);
        connection.Schema.Apply(interruptedUnit);
        var interruptedSession = connection.OpenSession(interruptedUnit, StorageAccess.Global);
        for (var index = 0; index < 40; index++)
            Assert.True(interruptedSession.Insert(Values(index % 2 == 0 ? "a" : "b")).Succeeded);

        using var cancelled = new CancellationTokenSource();
        var observer = new CancelAfterFirstBatch(cancelled);
        var interrupted = Assert.Throws<OperationCanceledException>(() => interruptedSession.ApplyRetention(
            new RetentionExecutionOptions
            {
                MaxRowsPerBatch = 2,
                CancellationToken = cancelled.Token,
                Observer = observer
            }));
        Assert.NotNull(interrupted);
        var partiallyRetained = interruptedSession.Query(All(interruptedUnit)).Rows.Count;
        Assert.InRange(partiallyRetained, 7, 38);

        var resumed = interruptedSession.ApplyRetention(new RetentionExecutionOptions { MaxRowsPerBatch = 2 });
        Assert.True(resumed.DeletedRows > 0);
        Assert.Equal(6, interruptedSession.Query(All(interruptedUnit)).Rows.Count);
    }

    [Fact]
    public void Retention_refuses_nullable_order_columns_before_schema_application()
    {
        var unit = RetentionUnit("stream-retention-invalid-" + Guid.NewGuid().ToString("N"), RetentionTrigger.Explicit) with
        {
            Columns = [
                new() { Name = "id", Type = PortableType.Int64, IsNullable = true },
                new() { Name = "partition", Type = PortableType.String, MaxLength = 16, IsNullable = false }
            ]
        };
        var refusal = Assert.Single(PortabilityValidator.Validate(unit).Refusals);
        Assert.Equal("GW-PORT-007", refusal.Code);
        Assert.Contains("id", refusal.Message, StringComparison.Ordinal);

        using var connection = new InMemoryProviderFactory().Create("stream-retention-invalid-provider-" + Guid.NewGuid().ToString("N"));
        var exception = Assert.Throws<InvalidOperationException>(() => connection.Schema.Apply(unit));
        Assert.Contains("GW-PORT-007", exception.Message, StringComparison.Ordinal);
    }

    private static void AssertRetention(IStorageProviderConnection connection, string provider)
    {
        var unit = RetentionUnit("stream-retention-" + provider + "-" + Guid.NewGuid().ToString("N"), RetentionTrigger.Explicit);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        for (var index = 0; index < 120; index++)
            Assert.True(session.Insert(Values(index % 3 == 0 ? "a" : "b")).Succeeded);

        var observer = new WritePathObserver();
        var result = session.ApplyRetention(new RetentionExecutionOptions
        {
            MaxRowsPerBatch = 7,
            Observer = observer
        });
        Assert.Equal(114, result.DeletedRows);
        Assert.Equal(6, session.Query(All(unit)).Rows.Count);
        var retainedPartitions = session.Query(All(unit)).Rows
            .Select(row => (string)row["partition"]!)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "a", "a", "a", "b", "b", "b" }, retainedPartitions);
        var retentionCommands = observer.Commands.Count(command => command.Operation.Contains("retention", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(retentionCommands, 1, result.Batches);
    }

    private static StorageUnit RetentionUnit(string name, RetentionTrigger trigger) => new()
    {
        Id = new StorageUnitId(name),
        Name = name,
        Columns = [
            new() { Name = "id", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
            new() { Name = "partition", Type = PortableType.String, MaxLength = 16, IsNullable = false },
            new() { Name = "payload", Type = PortableType.String, MaxLength = 32 }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Retention = new RetentionDeclaration
        {
            KeepNewest = 3,
            OrderColumn = "id",
            PartitionColumns = ["partition"],
            Trigger = trigger
        }
    };

    private static StorageValues Values(string partition) => new(new Dictionary<string, object?>
    {
        ["partition"] = partition,
        ["payload"] = "payload"
    });

    private static QueryRequest All(StorageUnit unit) => new(
        new TableId(unit.Name), Predicate.AlwaysTrue.Instance, [], Projection.All, Paging.None);

    private sealed class CancelAfterFirstBatch(CancellationTokenSource cancellation) : IWritePathObserver
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
