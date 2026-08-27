using System.Collections.Concurrent;
using System.Diagnostics;
using Groundwork.Kernel;
using Groundwork.LiveDatabases;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Testing;
using Groundwork.Store;
using Groundwork.Query.Model;
using Microsoft.Data.Sqlite;
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
    public async Task PostgreSQL_OnAppend_concurrent_writes_coalesce_below_the_serial_command_baseline()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run the PostgreSQL OnAppend proof.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);

        var serial = await MeasureOnAppend(connection, "pgs", concurrent: false);
        var concurrent = await MeasureOnAppend(connection, "pgc", concurrent: true);
        AssertNativeOnAppendCoalesces(serial, concurrent, "PostgreSQL");
    }

    [SkippableFact]
    public void SQLServer_retention_keeps_newest_rows_per_partition()
    {
        var connectionString = LiveSqlServer.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run the SQL Server retention proof.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertRetention(connection, "sqlserver");
    }

    [SkippableFact]
    public async Task SQLServer_OnAppend_concurrent_writes_coalesce_below_the_serial_command_baseline()
    {
        var connectionString = LiveSqlServer.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run the SQL Server OnAppend proof.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);

        var serial = await MeasureOnAppend(connection, "sqls", concurrent: false);
        var concurrent = await MeasureOnAppend(connection, "sqlc", concurrent: true);
        AssertNativeOnAppendCoalesces(serial, concurrent, "SQL Server");
    }

    [SkippableFact]
    public async Task SQLite_OnAppend_concurrent_writes_coalesce_below_the_serial_command_baseline()
    {
        var path = Path.Combine(Path.GetTempPath(), "groundwork-s3-convoy-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var connection = new SqliteProviderFactory().Create($"Data Source={path}");
            var serial = await MeasureOnAppend(connection, "lites", concurrent: false);
            var concurrent = await MeasureOnAppend(connection, "litec", concurrent: true);
            AssertNativeOnAppendCoalesces(serial, concurrent, "SQLite");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [SkippableFact]
    public async Task SQLite_in_memory_OnAppend_serializes_the_shared_connection_and_coalesces_cleanup()
    {
        var connectionString = $"Data Source=s3-retention-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        using var keeper = new SqliteConnection(connectionString);
        keeper.Open();
        using var connection = new SqliteProviderFactory().Create(connectionString);

        var serial = await MeasureOnAppend(connection, "mems", concurrent: false);
        var concurrent = await MeasureOnAppend(connection, "memc", concurrent: true);
        AssertNativeOnAppendCoalesces(serial, concurrent, "SQLite in-memory");
    }

    [SkippableFact]
    public void MongoDB_retention_uses_bounded_deleteMany_and_keeps_newest_rows_per_partition()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run the MongoDB retention proof.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        AssertRetention(connection, "mongodb");
        AssertMongoRetentionDriftIsRefused(connection);
        AssertMongoLargePartitionUsesBoundedWatermarks(connection);
    }

    [SkippableFact]
    public async Task MongoDB_OnAppend_concurrent_writes_coalesce_below_the_serial_command_baseline()
    {
        var connectionString = LiveMongo.ConnectionString;
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run the MongoDB OnAppend proof.");
        using var connection = new MongoProviderFactory().Create(connectionString!);

        var serial = await MeasureOnAppend(connection, "mngs", concurrent: false);
        var concurrent = await MeasureOnAppend(connection, "mngc", concurrent: true);
        AssertNativeOnAppendCoalesces(serial, concurrent, "MongoDB");
    }

    [Fact]
    public async Task OnAppend_retention_is_bounded_and_resumable()
    {
        using var connection = new InMemoryProviderFactory().Create("stream-retention-trigger-" + Guid.NewGuid().ToString("N"));
        var unit = RetentionUnit("stream-retention-trigger-" + Guid.NewGuid().ToString("N"), RetentionTrigger.OnAppend);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        for (var index = 0; index < 3; index++)
            Assert.True(session.Insert(Values("a")).Succeeded);

        const int concurrentWrites = 16;
        using var start = new ManualResetEventSlim();
        using var ready = new CountdownEvent(concurrentWrites);
        using var blockingObserver = new BlockingRetentionObserver();
        var tasks = Enumerable.Range(0, concurrentWrites).Select(_ => Task.Factory.StartNew(() =>
        {
            var writer = connection.OpenSession(unit, StorageAccess.Global, blockingObserver);
            ready.Signal();
            start.Wait();
            Assert.True(writer.Insert(Values("a")).Succeeded);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(5)), "Concurrent writers did not reach the start gate.");
        start.Set();
        Assert.True(blockingObserver.FirstCleanup.Wait(TimeSpan.FromSeconds(5)), "The first cleanup did not start.");
        try
        {
            Assert.True(SpinWait.SpinUntil(
                () => tasks.Count(task => task.IsCompleted) == concurrentWrites - 1,
                TimeSpan.FromSeconds(5)),
                "Concurrent appenders waited behind the active cleanup owner.");
        }
        finally
        {
            blockingObserver.ReleaseCleanup.Set();
        }
        await Task.WhenAll(tasks);
        Assert.InRange(blockingObserver.RetentionCommands, 1, 2);
        Assert.Equal(3, session.Query(All(unit)).Rows.Count);

        var interruptedUnit = RetentionUnit(
            "stream-retention-resume-" + Guid.NewGuid().ToString("N"), RetentionTrigger.Explicit);
        connection.Schema.Apply(interruptedUnit);
        var interruptedSession = connection.OpenSession(interruptedUnit, StorageAccess.Global);
        for (var index = 0; index < 40; index++)
            Assert.True(interruptedSession.Insert(Values(index % 2 == 0 ? "a" : "b")).Succeeded);

        using var cancelled = new CancellationTokenSource();
        // Cancels on its first command, so it observes a session that does nothing but the retention pass —
        // on the seeding session above it would fire on the first insert instead.
        var interruptedRetention = connection.OpenSession(
            interruptedUnit, StorageAccess.Global, new CancelAfterFirstBatch(cancelled));
        var interrupted = Assert.Throws<OperationCanceledException>(() => interruptedRetention.ApplyRetention(
            new RetentionExecutionOptions
            {
                MaxRowsPerBatch = 2,
                CancellationToken = cancelled.Token
            }));
        Assert.NotNull(interrupted);
        var partiallyRetained = interruptedSession.Query(All(interruptedUnit)).Rows.Count;
        Assert.InRange(partiallyRetained, 7, 38);

        var resumed = interruptedSession.ApplyRetention(new RetentionExecutionOptions { MaxRowsPerBatch = 2 });
        Assert.True(resumed.DeletedRows > 0);
        Assert.Equal(6, interruptedSession.Query(All(interruptedUnit)).Rows.Count);
    }

    [Fact]
    public void InMemory_retention_partition_identity_is_structural_when_values_contain_the_legacy_delimiter()
    {
        using var connection = new InMemoryProviderFactory().Create("s3-structural-partition-" + Guid.NewGuid().ToString("N"));
        var name = "s3_structural_partition_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 8, IsNullable = false },
                new() { Name = "p1", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new() { Name = "p2", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new() { Name = "ordering", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Retention = new RetentionDeclaration
            {
                KeepNewest = 1,
                OrderColumn = "ordering",
                PartitionColumns = ["p1", "p2"]
            }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Insert("a1", "a\u001fSystem.String:b", "c", 1);
        Insert("a2", "a\u001fSystem.String:b", "c", 2);
        Insert("b1", "a", "b\u001fSystem.String:c", 1);
        Insert("b2", "a", "b\u001fSystem.String:c", 2);

        session.ApplyRetention();

        var survivors = session.Query(All(unit)).Rows
            .Select(row => (string)row["id"]!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "a2", "b2" }, survivors);

        void Insert(string id, string p1, string p2, long ordering) => Assert.True(session.Insert(
            new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["p1"] = p1,
                ["p2"] = p2,
                ["ordering"] = ordering
            })).Succeeded);
    }

    [Fact]
    public void InMemory_retention_tie_break_identity_is_structural_when_keys_contain_the_legacy_delimiter()
    {
        using var connection = new InMemoryProviderFactory().Create("s3-structural-tie-" + Guid.NewGuid().ToString("N"));
        var name = "s3_structural_tie_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new() { Name = "k1", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new() { Name = "k2", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new() { Name = "ordering", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["k1", "k2"] },
            Retention = new RetentionDeclaration { KeepNewest = 1, OrderColumn = "ordering" }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.True(session.Insert(TiedValues("a\u001fSystem.String:b", "c")).Succeeded);
        Assert.True(session.Insert(TiedValues("a", "b\u001fSystem.String:c")).Succeeded);

        session.ApplyRetention();

        var survivor = Assert.Single(session.Query(All(unit)).Rows);
        Assert.Equal("a", survivor["k1"]);
        Assert.Equal("b\u001fSystem.String:c", survivor["k2"]);

        static StorageValues TiedValues(string k1, string k2) => new(new Dictionary<string, object?>
        {
            ["k1"] = k1,
            ["k2"] = k2,
            ["ordering"] = 1L
        });
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

        var observer = new ProviderCommandObserver();
        var retentionSession = connection.OpenSession(unit, StorageAccess.Global, observer);
        var result = retentionSession.ApplyRetention(new RetentionExecutionOptions
        {
            MaxRowsPerBatch = 7
        });
        Assert.Equal(114, result.DeletedRows);
        Assert.Equal(6, session.Query(All(unit)).Rows.Count);
        var retainedPartitions = session.Query(All(unit)).Rows
            .Select(row => (string)row["partition"]!)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "a", "a", "a", "b", "b", "b" }, retainedPartitions);
        var retentionCommands = observer.Commands.Count(command =>
            command.Operation.Contains("retention", StringComparison.OrdinalIgnoreCase) &&
            !command.Operation.Contains("watermark", StringComparison.OrdinalIgnoreCase));
        Assert.InRange(retentionCommands, 1, result.Batches);

        AssertTiedOrderRetention(connection, provider);
        AssertNativeBatchOnAppend(connection, provider);
        AssertConditionalCreateOnlyOnAppend(connection, provider);
        AssertIdempotentAppendOnAppendRetention(connection, provider);
        AssertInterruptedNativeRetentionResumes(connection, provider);
    }

    private static void AssertIdempotentAppendOnAppendRetention(
        IStorageProviderConnection connection,
        string provider)
    {
        var name = "s3_idem_" + provider[..Math.Min(provider.Length, 8)] + "_" + Guid.NewGuid().ToString("N");
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
            AppendIdempotency = new AppendIdempotencyDeclaration { Window = TimeSpan.FromMinutes(10) },
            Retention = new RetentionDeclaration
            {
                KeepNewest = 3,
                OrderColumn = "ordering",
                Trigger = RetentionTrigger.OnAppend
            }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        for (var index = 0; index < 10; index++)
        {
            var outcome = session.Append(
                new OperationId(DateTimeOffset.UnixEpoch, "operation-" + index),
                [IdempotentValues(index)]);
            Assert.Equal(WriteOutcomeStatus.Inserted, outcome.Status);
        }

        Assert.Equal(new[] { "0007", "0008", "0009" }, SurvivorIds());
        var replay = session.Append(
            new OperationId(DateTimeOffset.UnixEpoch, "operation-9"),
            [new StorageValues(new Dictionary<string, object?> { ["id"] = "replayed", ["ordering"] = 100L })]);
        Assert.Equal(WriteOutcomeStatus.Replayed, replay.Status);
        Assert.Equal(new[] { "0007", "0008", "0009" }, SurvivorIds());

        var retryableOperation = new OperationId(DateTimeOffset.UnixEpoch, "operation-retry-after-failure");
        var rejected = Record.Exception(() => session.Append(
            retryableOperation,
            [new StorageValues(new Dictionary<string, object?> { ["id"] = "invalid" })]));
        Assert.NotNull(rejected);
        Assert.Equal(new[] { "0007", "0008", "0009" }, SurvivorIds());
        Assert.Equal(WriteOutcomeStatus.Inserted,
            session.Append(retryableOperation, [IdempotentValues(10)]).Status);
        Assert.Equal(new[] { "0008", "0009", "0010" }, SurvivorIds());

        using var interrupted = new CancellationTokenSource();
        interrupted.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.ApplyRetention(
            new RetentionExecutionOptions { CancellationToken = interrupted.Token, MaxRowsPerBatch = 1 }));
        session.ApplyRetention(new RetentionExecutionOptions { MaxRowsPerBatch = 1 });
        Assert.Equal(new[] { "0008", "0009", "0010" }, SurvivorIds());

        string[] SurvivorIds() => session.Query(All(unit)).Rows
            .Select(row => (string)row["id"]!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        static StorageValues IdempotentValues(int index) => new(new Dictionary<string, object?>
        {
            ["id"] = index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
            ["ordering"] = (long)index
        });
    }

    private static void AssertTiedOrderRetention(IStorageProviderConnection connection, string provider)
    {
        var name = "s3_ties_" + provider[..Math.Min(provider.Length, 8)] + "_" + Guid.NewGuid().ToString("N");
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

    private static async Task<OnAppendMeasurement> MeasureOnAppend(
        IStorageProviderConnection connection,
        string provider,
        bool concurrent)
    {
        const int writes = 32;
        var name = "s3_convoy_" + provider + "_" + Guid.NewGuid().ToString("N");
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
        var observer = new ProviderCommandObserver();
        static void Append(IStorageSession session, ProviderCommandObserver observer, int index)
        {
            var outcome = session.Insert(new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                ["ordering"] = (long)index
            }));
            Assert.True(outcome.Succeeded);
        }
        var stopwatch = new Stopwatch();
        var maxOverlap = 1;
        if (concurrent)
        {
            using var start = new ManualResetEventSlim();
            using var ready = new CountdownEvent(writes);
            var spans = new ConcurrentBag<(long StartTicks, long EndTicks)>();
            var tasks = Enumerable.Range(0, writes).Select(index => Task.Factory.StartNew(() =>
            {
                var session = connection.OpenSession(unit, StorageAccess.Global, observer);
                ready.Signal();
                start.Wait();
                var begin = stopwatch.ElapsedTicks;
                Append(session, observer, index);
                spans.Add((begin, stopwatch.ElapsedTicks));
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)), "Native writers did not reach the start gate.");
            stopwatch.Start();
            start.Set();
            await Task.WhenAll(tasks);
            maxOverlap = MaxConcurrentSpans(spans);
        }
        else
        {
            stopwatch.Start();
            for (var index = 0; index < writes; index++)
                Append(connection.OpenSession(unit, StorageAccess.Global, observer), observer, index);
        }
        stopwatch.Stop();

        var verification = connection.OpenSession(unit, StorageAccess.Global);
        var survivors = verification.Query(All(unit)).Rows.Count;
        var retentionCommands = observer.Commands.Count(command =>
            command.Operation.Contains("retention", StringComparison.OrdinalIgnoreCase));
        return new OnAppendMeasurement(stopwatch.Elapsed, retentionCommands, survivors, maxOverlap, writes);
    }

    /// <summary>
    /// The largest number of the given [start, end) spans that were simultaneously in flight, found by a
    /// sweep line over their endpoints. This is the observable stand-in for "the writers actually overlapped",
    /// which the start gate alone cannot guarantee: the gate only proves every writer reached it together, not
    /// that the scheduler kept them running together afterwards.
    /// </summary>
    private static int MaxConcurrentSpans(IReadOnlyCollection<(long StartTicks, long EndTicks)> spans)
    {
        if (spans.Count == 0)
            return 0;
        var events = new List<(long Ticks, int Delta)>(spans.Count * 2);
        foreach (var (start, end) in spans)
        {
            events.Add((start, 1));
            events.Add((end, -1));
        }
        // An end processed before a start at the same tick keeps back-to-back, non-overlapping spans from
        // being counted as overlapping.
        events.Sort((left, right) => left.Ticks != right.Ticks
            ? left.Ticks.CompareTo(right.Ticks)
            : left.Delta.CompareTo(right.Delta));
        var current = 0;
        var max = 0;
        foreach (var (_, delta) in events)
        {
            current += delta;
            if (current > max)
                max = current;
        }
        return max;
    }

    private static void AssertNativeOnAppendCoalesces(
        OnAppendMeasurement serial,
        OnAppendMeasurement concurrent,
        string provider)
    {
        var minimumConclusiveOverlap = concurrent.Writes / 2;
        Skip.If(concurrent.MaxOverlap < minimumConclusiveOverlap,
            $"{provider}: only {concurrent.MaxOverlap} of {concurrent.Writes} concurrent writers were ever in " +
            $"flight at the same instant (needed at least {minimumConclusiveOverlap}). The scheduler serialized " +
            "the writers past the start gate instead of keeping them overlapped, so this run cannot prove or " +
            "disprove OnAppend coalescing; it is inconclusive, not a pass.");

        Assert.True(concurrent.RetentionCommands * 2 <= serial.RetentionCommands,
            $"Concurrent OnAppend issued {concurrent.RetentionCommands} retention commands in {concurrent.Elapsed}; " +
            $"the serial baseline issued {serial.RetentionCommands} in {serial.Elapsed}. " +
            "The coalesced path must remove at least half of the serial cleanup commands.");
        Assert.Equal(8, concurrent.Survivors);
    }

    private static void AssertNativeBatchOnAppend(IStorageProviderConnection connection, string provider)
    {
        var name = "s3_batch_" + provider[..Math.Min(provider.Length, 8)] + "_" + Guid.NewGuid().ToString("N");
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
        var observer = new ProviderCommandObserver();
        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Default, observer, unit))
        {
            for (var index = 0; index < 12; index++)
            {
                work.Stage(RowWrite.Insert(unit, new StorageValues(new Dictionary<string, object?>
                {
                    ["id"] = index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                    ["ordering"] = (long)index
                })));
            }
            var summary = work.Commit();
            Assert.Equal(12, summary.Succeeded);
        }
        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Default, observer, unit))
        {
            for (var index = 12; index < 24; index++)
            {
                work.Stage(RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?>
                {
                    ["id"] = index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                    ["ordering"] = (long)index
                })));
            }
            var summary = work.Commit();
            Assert.Equal(12, summary.Succeeded);
        }

        var verification = connection.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(4, verification.Query(All(unit)).Rows.Count);
        Assert.Contains(observer.Commands, command =>
            command.Operation.Contains("retention", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertConditionalCreateOnlyOnAppend(
        IStorageProviderConnection connection,
        string provider)
    {
        var name = "s3_create_" + provider[..Math.Min(provider.Length, 8)] + "_" + Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 32, IsNullable = false },
                new() { Name = "ordering", Type = PortableType.Int64, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Concurrency = ConcurrencyDeclaration.Optimistic(),
            Retention = new RetentionDeclaration
            {
                KeepNewest = 3,
                OrderColumn = "ordering",
                Trigger = RetentionTrigger.OnAppend
            }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        var observer = new ProviderCommandObserver();
        var session = connection.OpenSession(unit, StorageAccess.Global, observer);
        var concurrency = Assert.IsAssignableFrom<IConcurrencyStorageSession>(session);
        for (var index = 0; index < 10; index++)
        {
            var values = new StorageValues(new Dictionary<string, object?>
            {
                ["id"] = index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture),
                ["ordering"] = (long)index
            });
            var options = new WriteOptions { Precondition = WritePrecondition.CreateOnly };
            Assert.Equal(WriteOutcomeStatus.Inserted, concurrency.ConditionalUpsert(values, options).Status);
            Assert.Equal(WriteOutcomeStatus.ConcurrencyConflict, concurrency.ConditionalUpsert(values, options).Status);
        }

        var survivors = session.Query(All(unit)).Rows
            .Select(row => (string)row["id"]!)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "0007", "0008", "0009" }, survivors);
        Assert.Contains(observer.Commands, command =>
            command.Operation.Contains("retention", StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertMongoRetentionDriftIsRefused(IStorageProviderConnection connection)
    {
        var name = "s3_mongo_drift_" + Guid.NewGuid().ToString("N");
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

        var adversarialName = "s3_mongo_parts_" + Guid.NewGuid().ToString("N");
        var combinedPartition = new StorageUnit
        {
            Id = new StorageUnitId(adversarialName),
            Name = adversarialName,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new() { Name = "ordering", Type = PortableType.Int64, IsNullable = false },
                new() { Name = "a_b", Type = PortableType.String, MaxLength = 16, IsNullable = false },
                new() { Name = "a", Type = PortableType.String, MaxLength = 16, IsNullable = false },
                new() { Name = "b", Type = PortableType.String, MaxLength = 16, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Retention = new RetentionDeclaration
            {
                KeepNewest = 2,
                OrderColumn = "ordering",
                PartitionColumns = ["a_b"]
            }
        };
        Assert.True(connection.Schema.Apply(combinedPartition).Applied);
        var splitPartitions = combinedPartition with
        {
            Retention = combinedPartition.Retention! with { PartitionColumns = ["a", "b"] }
        };
        var adversarialDrift = Assert.Throws<MongoSchemaConflictException>(() =>
            connection.Schema.Diff(splitPartitions));
        Assert.Contains("retention", adversarialDrift.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertMongoLargePartitionUsesBoundedWatermarks(IStorageProviderConnection connection)
    {
        const int rows = 2_000;
        const int batchSize = 37;
        var name = "s3_mongo_large_" + Guid.NewGuid().ToString("N");
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
                KeepNewest = 11,
                OrderColumn = "ordering",
                PartitionColumns = ["partition"]
            }
        };
        Assert.True(connection.Schema.Apply(unit).Applied);
        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, unit))
        {
            for (var index = 0; index < rows; index++)
            {
                work.Stage(RowWrite.Upsert(unit, new StorageValues(new Dictionary<string, object?>
                {
                    ["id"] = index.ToString("D6", System.Globalization.CultureInfo.InvariantCulture),
                    ["partition"] = "large",
                    ["ordering"] = (long)index
                })));
            }
            Assert.Equal(rows, work.Commit().Succeeded);
        }

        var observer = new ProviderCommandObserver();
        var session = connection.OpenSession(unit, StorageAccess.Global, observer);
        var result = session.ApplyRetention(new RetentionExecutionOptions
        {
            MaxRowsPerBatch = batchSize
        });

        Assert.Equal(rows - 11, result.DeletedRows);
        Assert.Equal(11, session.Query(All(unit)).Rows.Count);
        var watermarkCommands = observer.Commands
            .Where(command => command.Operation == "mongodb.retention-watermark-find")
            .ToArray();
        Assert.NotEmpty(watermarkCommands);
        Assert.All(watermarkCommands, command => Assert.Contains($"limit:{batchSize}", command.CommandText));
        var deleteCommands = observer.Commands
            .Where(command => command.Operation == "mongodb.retention-delete-many")
            .ToArray();
        Assert.Equal(result.Batches, deleteCommands.Length);
        Assert.All(deleteCommands, command => Assert.Contains($"ids<=:{batchSize}", command.CommandText));
    }

    private static void AssertInterruptedNativeRetentionResumes(
        IStorageProviderConnection connection,
        string provider)
    {
        var name = "s3_resume_" + provider[..Math.Min(provider.Length, 8)] + "_" + Guid.NewGuid().ToString("N");
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
        // The observer cancels on its first command, so retention runs on a session of its own: attached to
        // the seeding session above it would fire on the inserts instead of on the pass under test.
        var cancellingSession = connection.OpenSession(unit, StorageAccess.Global, new CancelAfterFirstBatch(cancellation));
        var interrupted = Assert.Throws<OperationCanceledException>(() => cancellingSession.ApplyRetention(
            new RetentionExecutionOptions
            {
                MaxRowsPerBatch = 2,
                CancellationToken = cancellation.Token
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
        Name = PhysicalName(name),
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

    private static string PhysicalName(string name) => name.Replace('-', '_');

    private static StorageValues Values(string partition) => new(new Dictionary<string, object?>
    {
        ["partition"] = partition,
        ["payload"] = "payload"
    });

    private static QueryRequest All(StorageUnit unit) => new(
        new TableId(unit.Name), Predicate.AlwaysTrue.Instance, [], Projection.All, Paging.None);

    private sealed class CancelAfterFirstBatch(CancellationTokenSource cancellation) : IProviderCommandObserver
    {
        private int batches;

        public void Observe(ProviderCommandEvent command)
        {
            if (command.Operation.Contains("retention", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Increment(ref batches) == 1)
                cancellation.Cancel();
        }
    }

    private sealed class BlockingRetentionObserver : IProviderCommandObserver, IDisposable
    {
        private int retentionCommands;

        internal ManualResetEventSlim FirstCleanup { get; } = new();

        internal ManualResetEventSlim ReleaseCleanup { get; } = new();

        internal int RetentionCommands => Volatile.Read(ref retentionCommands);

        public void Observe(ProviderCommandEvent command)
        {
            if (!command.Operation.Contains("retention", StringComparison.OrdinalIgnoreCase))
                return;
            if (Interlocked.Increment(ref retentionCommands) != 1)
                return;
            FirstCleanup.Set();
            ReleaseCleanup.Wait();
        }

        public void Dispose()
        {
            ReleaseCleanup.Set();
            FirstCleanup.Dispose();
            ReleaseCleanup.Dispose();
        }
    }

    private sealed record OnAppendMeasurement(
        TimeSpan Elapsed,
        int RetentionCommands,
        int Survivors,
        int MaxOverlap,
        int Writes);
}
