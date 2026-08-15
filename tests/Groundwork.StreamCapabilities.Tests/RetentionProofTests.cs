using System.Diagnostics;
using Groundwork.Kernel;
using Groundwork.MongoDb;
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
    public void PostgreSQL_OnAppend_concurrent_writes_coalesce_below_the_serial_command_baseline()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run the PostgreSQL OnAppend proof.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);

        var serial = MeasureOnAppend(connection, "pgs", concurrent: false);
        var concurrent = MeasureOnAppend(connection, "pgc", concurrent: true);

        Assert.True(concurrent.RetentionCommands * 2 <= serial.RetentionCommands,
            $"Concurrent OnAppend issued {concurrent.RetentionCommands} retention commands in {concurrent.Elapsed}; " +
            $"the serial baseline issued {serial.RetentionCommands} in {serial.Elapsed}. " +
            "The coalesced path must remove at least half of the serial cleanup commands.");
        Assert.Equal(8, concurrent.Survivors);
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
        AssertMongoRetentionDriftIsRefused(connection);
    }

    [Fact]
    public void OnAppend_retention_is_bounded_and_resumable()
    {
        using var connection = new InMemoryProviderFactory().Create("stream-retention-trigger-" + Guid.NewGuid().ToString("N"));
        var unit = RetentionUnit("stream-retention-trigger-" + Guid.NewGuid().ToString("N"), RetentionTrigger.OnAppend);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Parallel.For(0, 64, index => session.Insert(Values(index % 2 == 0 ? "a" : "b")));
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

        AssertTiedOrderRetention(connection, provider);
        AssertNativeBatchOnAppend(connection, provider);
        AssertInterruptedNativeRetentionResumes(connection, provider);
    }

    private static void AssertTiedOrderRetention(IStorageProviderConnection connection, string provider)
    {
        var name = "s3-ties-" + provider[..Math.Min(provider.Length, 8)] + "-" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 16, IsNullable = false },
                new() { Name = "partition", Type = PortableType.String, MaxLength = 16, IsNullable = false },
                new() { Name = "ordering", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Retention = new RetentionDeclaration
            {
                KeepNewest = 2,
                OrderColumn = "ordering",
                PartitionColumns = ["partition"]
            }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        foreach (var key in new[] { "d", "c", "b", "a" })
        {
            Assert.True(session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = key,
                ["partition"] = "one",
                ["ordering"] = 42L
            })).Succeeded);
        }

        session.ApplyRetention(new RetentionExecutionOptions { MaxRowsPerBatch = 1 });
        var survivors = session.Query(All(unit)).Rows
            .Select(row => (string)row["id"]!)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "a", "b" }, survivors);
    }

    private static OnAppendMeasurement MeasureOnAppend(
        IStorageProviderConnection connection,
        string provider,
        bool concurrent)
    {
        const int writes = 32;
        var name = "s3-convoy-" + provider + "-" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new() { Name = "ordering", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Retention = new RetentionDeclaration
            {
                KeepNewest = 8,
                OrderColumn = "ordering",
                Trigger = RetentionTrigger.OnAppend
            }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var observer = new WritePathObserver();
        var stopwatch = Stopwatch.StartNew();
        Action<int> append = index =>
        {
            var session = connection.OpenSession(unit, StorageAccess.Global);
            var outcome = session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                ["ordering"] = (long)index
            }), new WriteOptions { Observer = observer });
            Assert.True(outcome.Succeeded);
        };
        if (concurrent)
            Parallel.For(0, writes, append);
        else
            for (var index = 0; index < writes; index++) append(index);
        stopwatch.Stop();

        var verification = connection.OpenSession(unit, StorageAccess.Global);
        var survivors = verification.Query(All(unit)).Rows.Count;
        var retentionCommands = observer.Commands.Count(command =>
            command.Operation.Contains("retention", StringComparison.OrdinalIgnoreCase));
        return new OnAppendMeasurement(stopwatch.Elapsed, retentionCommands, survivors);
    }

    private static void AssertNativeBatchOnAppend(IStorageProviderConnection connection, string provider)
    {
        var name = "s3-batch-" + provider[..Math.Min(provider.Length, 8)] + "-" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new() { Name = "ordering", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Retention = new RetentionDeclaration
            {
                KeepNewest = 4,
                OrderColumn = "ordering",
                Trigger = RetentionTrigger.OnAppend
            }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var observer = new WritePathObserver();
        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, unit))
        {
            for (var index = 0; index < 12; index++)
            {
                work.Stage(RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?>
                {
                    ["id"] = index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                    ["ordering"] = (long)index
                }), new WriteOptions { Observer = observer }));
            }
            var summary = work.Commit();
            Assert.Equal(12, summary.Succeeded);
        }
        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, unit))
        {
            for (var index = 12; index < 24; index++)
            {
                work.Stage(RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?>
                {
                    ["id"] = index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                    ["ordering"] = (long)index
                }), new WriteOptions { Observer = observer }));
            }
            var summary = work.Commit();
            Assert.Equal(12, summary.Succeeded);
        }

        var verification = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(4, verification.Query(All(unit)).Rows.Count);
        Assert.Contains(observer.Commands, command =>
            command.Operation.Contains("retention", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertMongoRetentionDriftIsRefused(IStorageProviderConnection connection)
    {
        var name = "s3-mongo-drift-" + Guid.NewGuid().ToString("N");
        var original = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new() { Name = "ordering", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Retention = new RetentionDeclaration { KeepNewest = 2, OrderColumn = "ordering" }
        };
        Assert.True(connection.Schema.Apply(original).Applied);
        var changed = original with
        {
            Retention = original.Retention! with { KeepNewest = 3 }
        };

        var diff = Assert.Throws<MongoSchemaConflictException>(() => connection.Schema.Diff(changed));
        Assert.Contains("retention", diff.Message, StringComparison.OrdinalIgnoreCase);
        var apply = Assert.Throws<MongoSchemaConflictException>(() => connection.Schema.Apply(changed));
        Assert.Contains("retention", apply.Message, StringComparison.OrdinalIgnoreCase);

        var originalSession = connection.OpenSession(original, StorageAccess.Global);
        Assert.Equal(2, originalSession.Unit.Retention!.KeepNewest);
    }

    private static void AssertInterruptedNativeRetentionResumes(
        IStorageProviderConnection connection,
        string provider)
    {
        var name = "s3-resume-" + provider[..Math.Min(provider.Length, 8)] + "-" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new() { Name = "partition", Type = PortableType.String, MaxLength = 8, IsNullable = false },
                new() { Name = "ordering", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Retention = new RetentionDeclaration
            {
                KeepNewest = 3,
                OrderColumn = "ordering",
                PartitionColumns = ["partition"]
            }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        for (var index = 0; index < 40; index++)
        {
            Assert.True(session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                ["partition"] = index % 2 == 0 ? "a" : "b",
                ["ordering"] = (long)index
            })).Succeeded);
        }

        using var cancellation = new CancellationTokenSource();
        var interrupted = Assert.Throws<OperationCanceledException>(() => session.ApplyRetention(
            new RetentionExecutionOptions
            {
                MaxRowsPerBatch = 2,
                CancellationToken = cancellation.Token,
                Observer = new CancelAfterFirstBatch(cancellation)
            }));
        Assert.NotNull(interrupted);
        var consistentIntermediateCount = session.Query(All(unit)).Rows.Count;
        Assert.InRange(consistentIntermediateCount, 6, 40);

        var resumed = session.ApplyRetention(new RetentionExecutionOptions { MaxRowsPerBatch = 2 });
        Assert.True(resumed.DeletedRows > 0);
        var survivors = session.Query(All(unit)).Rows
            .Select(row => (string)row["id"]!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "0034", "0035", "0036", "0037", "0038", "0039" }, survivors);
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

    private sealed record OnAppendMeasurement(TimeSpan Elapsed, int RetentionCommands, int Survivors);
}
