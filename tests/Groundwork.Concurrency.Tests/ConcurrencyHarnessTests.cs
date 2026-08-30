using System.Collections.Concurrent;
using Groundwork.MongoDb;
using Groundwork.Kernel;
using Groundwork.MySql;
using Groundwork.PostgreSql;
using Groundwork.Sqlite;
using Groundwork.SqlServer;
using Groundwork.Testing;
using Groundwork.Store;
using MongoDB.Driver;
using Npgsql;
using Groundwork.LiveDatabases;
using Xunit;

namespace Groundwork.Concurrency.Tests;

[Trait("Category", "Concurrency")]
public sealed class ConcurrencyHarnessTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void In_memory_reference_holds_every_named_invariant_for_key_cardinality(int keyCount)
    {
        var options = new ConcurrencyProbeOptions
        {
            WriterCount = 32,
            KeyCount = keyCount,
            RepeatCount = 2,
            Seed = 245,
            Concurrency = ConcurrencyKind.Optimistic,
            IncludePartialUniqueIndex = true
        };

        var report = ConcurrencyHarness.Run(
            new StorageProviderConcurrencyFactory("memory", new InMemoryProviderFactory()),
            "memory://w2-invariants",
            options);

        Assert.True(report.Passed, Describe(report));
        Assert.All(report.Scenarios, scenario => Assert.True(scenario.MachineLoad.ProcessorCount > 0));
        Assert.All(report.Scenarios.SelectMany(scenario => scenario.Invariants), invariant =>
            Assert.True(invariant.Passed, $"{invariant.Name}: {invariant.Detail}"));
        Assert.Contains(report.Scenarios.SelectMany(scenario => scenario.Outcomes),
            outcome => outcome.Status == ConcurrencyWriteOutcomeStatus.ConcurrencyConflict);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Sqlite_holds_every_named_invariant_for_key_cardinality(int keyCount)
    {
        using var store = TemporarySqliteStore.Create();
        var report = ConcurrencyHarness.Run(
            new StorageProviderConcurrencyFactory("sqlite", new SqliteProviderFactory()),
            store.ConnectionString,
            new ConcurrencyProbeOptions
            {
                WriterCount = 32,
                KeyCount = keyCount,
                RepeatCount = 2,
                Seed = 3245,
                Concurrency = ConcurrencyKind.Optimistic,
                IncludePartialUniqueIndex = true
            });

        Assert.True(report.Passed, Describe(report));
        Assert.All(report.Scenarios.SelectMany(scenario => scenario.Invariants), invariant =>
            Assert.True(invariant.Passed, $"{invariant.Name}: {invariant.Detail}"));
    }

    [Fact]
    public void Sqlite_none_mode_covers_the_non_versioned_index_shape()
    {
        using var store = TemporarySqliteStore.Create();
        var report = ConcurrencyHarness.Run(
            new StorageProviderConcurrencyFactory("sqlite", new SqliteProviderFactory()),
            store.ConnectionString,
            new ConcurrencyProbeOptions
            {
                WriterCount = 32,
                KeyCount = 1,
                RepeatCount = 2,
                Seed = 4245,
                Concurrency = ConcurrencyKind.None,
                IncludePartialUniqueIndex = false
            });

        Assert.True(report.Passed, Describe(report));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Sqlite_holds_every_named_invariant_on_the_async_surface(int keyCount)
    {
        using var store = TemporarySqliteStore.Create();
        var report = ConcurrencyHarness.Run(
            new StorageProviderConcurrencyFactory("sqlite", new SqliteProviderFactory()),
            store.ConnectionString,
            new ConcurrencyProbeOptions
            {
                WriterCount = 32,
                KeyCount = keyCount,
                RepeatCount = 2,
                Seed = 5245,
                Concurrency = ConcurrencyKind.Optimistic,
                IncludePartialUniqueIndex = true,
                Surface = ConcurrencySurface.Asynchronous
            });

        Assert.True(report.Passed, Describe(report));
        Assert.All(report.Scenarios.SelectMany(scenario => scenario.Invariants), invariant =>
            Assert.True(invariant.Passed, $"{invariant.Name}: {invariant.Detail}"));
        Assert.Contains(report.Scenarios.SelectMany(scenario => scenario.Outcomes),
            outcome => outcome.Status == ConcurrencyWriteOutcomeStatus.ConcurrencyConflict);
    }

    [Fact]
    public void Sqlite_holds_every_named_invariant_when_writes_commit_through_an_async_unit_of_work()
    {
        using var store = TemporarySqliteStore.Create();
        var report = ConcurrencyHarness.Run(
            new StorageProviderConcurrencyFactory(
                "sqlite", new SqliteProviderFactory(), commitThroughUnitOfWork: true),
            store.ConnectionString,
            new ConcurrencyProbeOptions
            {
                WriterCount = 8,
                KeyCount = 1,
                RepeatCount = 2,
                Seed = 6245,
                Concurrency = ConcurrencyKind.Optimistic,
                Surface = ConcurrencySurface.Asynchronous
            });

        Assert.True(report.Passed, Describe(report));
    }

    [Fact]
    public void In_memory_batched_upsert_preserves_atomic_concurrency_and_created_at()
    {
        AssertBatchedProvider(new InMemoryProviderFactory(), "memory://w3-batched-memory", "memory");
    }

    [Fact]
    public void Sqlite_batched_upsert_preserves_atomic_concurrency_and_created_at()
    {
        using var store = TemporarySqliteStore.Create();
        AssertBatchedProvider(new SqliteProviderFactory(), store.ConnectionString, "sqlite");
    }

    [SkippableFact]
    public void PostgreSql_batched_upsert_preserves_atomic_concurrency_and_created_at()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_POSTGRES_CONNECTION to run the live PostgreSQL batched W3 proof.");
        AssertBatchedProvider(new PostgreSqlProviderFactory(), connectionString!, "postgresql");
    }

    [SkippableFact]
    public void SqlServer_batched_upsert_preserves_atomic_concurrency_and_created_at()
    {
        var connectionString = LiveSqlServer.Required();
        AssertBatchedProvider(new SqlServerProviderFactory(), connectionString, "sqlserver");
    }

    [SkippableFact]
    public void Mongo_batched_upsert_preserves_atomic_concurrency_and_created_at()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run the live MongoDB batched W3 proof.");
        var isolatedConnection = new MongoUrlBuilder(connectionString!)
        {
            DatabaseName = "w3_batched_" + Guid.NewGuid().ToString("N")
        }.ToMongoUrl().ToString();
        AssertBatchedProvider(new MongoProviderFactory(), isolatedConnection, "mongodb");
    }

    [SkippableFact]
    public void MySql_batched_upsert_preserves_atomic_concurrency_and_created_at()
    {
        using var store = LiveMySqlDatabase.OpenOrSkip();
        AssertBatchedProvider(new MySqlProviderFactory(), store.ConnectionString, "mysql");
    }

    [SkippableTheory]
    [InlineData(1, false, false)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    [InlineData(1, true, true)]
    [InlineData(1000, false, false)]
    [InlineData(1000, false, true)]
    [InlineData(1000, true, false)]
    [InlineData(1000, true, true)]
    public void PostgreSql_holds_the_named_invariants_for_each_live_shape(
        int keyCount,
        bool includePartialUniqueIndex,
        bool optimistic)
    {
        using var store = PostgreSqlStore.OpenOrSkip();
        var report = ConcurrencyHarness.Run(
            new StorageProviderConcurrencyFactory("postgresql", new PostgreSqlProviderFactory()),
            store.ConnectionString,
            new ConcurrencyProbeOptions
            {
                WriterCount = 32,
                KeyCount = keyCount,
                RepeatCount = 2,
                Seed = 9245 + (keyCount == 1000 ? 100 : 0) +
                    (includePartialUniqueIndex ? 10 : 0) + (optimistic ? 1 : 0),
                Concurrency = optimistic ? ConcurrencyKind.Optimistic : ConcurrencyKind.None,
                IncludePartialUniqueIndex = includePartialUniqueIndex
            });

        Assert.True(report.Passed, Describe(report));
        Assert.All(report.Scenarios.SelectMany(scenario => scenario.Invariants), invariant =>
            Assert.True(invariant.Passed, $"{invariant.Name}: {invariant.Detail}"));
    }

    [SkippableTheory]
    [InlineData(1, false, false)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    [InlineData(1, true, true)]
    [InlineData(1000, false, false)]
    [InlineData(1000, false, true)]
    [InlineData(1000, true, false)]
    [InlineData(1000, true, true)]
    public void MySql_holds_the_named_invariants_for_each_live_shape(
        int keyCount,
        bool includePartialUniqueIndex,
        bool optimistic)
    {
        using var store = LiveMySqlDatabase.OpenOrSkip();
        var report = ConcurrencyHarness.Run(
            new StorageProviderConcurrencyFactory("mysql", new MySqlProviderFactory()),
            store.ConnectionString,
            new ConcurrencyProbeOptions
            {
                WriterCount = 32,
                KeyCount = keyCount,
                RepeatCount = 2,
                Seed = 17900 + (keyCount == 1000 ? 100 : 0) +
                    (includePartialUniqueIndex ? 10 : 0) + (optimistic ? 1 : 0),
                Concurrency = optimistic ? ConcurrencyKind.Optimistic : ConcurrencyKind.None,
                IncludePartialUniqueIndex = includePartialUniqueIndex
            });

        Assert.True(report.Passed, Describe(report));
        Assert.All(report.Scenarios.SelectMany(scenario => scenario.Invariants), invariant =>
            Assert.True(invariant.Passed, $"{invariant.Name}: {invariant.Detail}"));
    }

    [Fact]
    public void None_mode_is_deterministic_and_reports_logical_versions()
    {
        var options = new ConcurrencyProbeOptions
        {
            WriterCount = 8,
            KeyCount = 4,
            RepeatCount = 2,
            Seed = 1245,
            Concurrency = ConcurrencyKind.None,
            IncludePartialUniqueIndex = true
        };

        var first = ConcurrencyHarness.Run(
            new StorageProviderConcurrencyFactory("memory", new InMemoryProviderFactory()),
            "memory://w2-none-first",
            options);
        var second = ConcurrencyHarness.Run(
            new StorageProviderConcurrencyFactory("memory", new InMemoryProviderFactory()),
            "memory://w2-none-second",
            options);

        Assert.True(first.Passed, Describe(first));
        Assert.True(second.Passed, Describe(second));
        Assert.Equal(
            first.Scenarios.Select(scenario =>
                (scenario.Seed, scenario.AcceptedWrites.Count, scenario.Invariants.Count)),
            second.Scenarios.Select(scenario =>
                (scenario.Seed, scenario.AcceptedWrites.Count, scenario.Invariants.Count)));
        Assert.All(first.Scenarios.Concat(second.Scenarios), scenario =>
            Assert.All(scenario.Invariants, invariant => Assert.True(invariant.Passed,
                $"{invariant.Name}: {invariant.Detail}")));
    }

    [Fact]
    public void Broken_upsert_fails_the_insert_count_invariant_deterministically()
    {
        var report = ConcurrencyHarness.Run(
            new BrokenConcurrencyFactory(),
            "broken://w2",
            new ConcurrencyProbeOptions
            {
                WriterCount = 1,
                KeyCount = 1,
                RepeatCount = 1,
                MaxAttemptsPerWrite = 2,
                Concurrency = ConcurrencyKind.None
            });

        Assert.False(report.Passed);
        var invariant = report.Scenarios.Single().Invariants.Single(item =>
            item.Name == "inserted-count-equals-distinct-keys");
        Assert.False(invariant.Passed);
    }

    [SkippableFact]
    public void Mongo_replica_set_holds_the_optimistic_invariants()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run the live MongoDB W2 harness.");

        var isolatedConnection = new MongoUrlBuilder(connectionString!)
        {
            DatabaseName = "w2_" + Guid.NewGuid().ToString("N")
        }.ToMongoUrl().ToString();
        var report = ConcurrencyHarness.Run(
            new StorageProviderConcurrencyFactory("mongodb", new MongoProviderFactory()),
            isolatedConnection,
            new ConcurrencyProbeOptions
            {
                WriterCount = 32,
                KeyCount = 1,
                RepeatCount = 2,
                Seed = 9245,
                Concurrency = ConcurrencyKind.Optimistic,
                IncludePartialUniqueIndex = true
            });

        Assert.True(report.Passed, Describe(report));
    }

    private static string Describe(ConcurrencyHarnessReport report) =>
        string.Join(Environment.NewLine, report.Scenarios.SelectMany(scenario =>
            scenario.Invariants.Select(invariant =>
                $"seed={scenario.Seed} {invariant.Name}: {invariant.Passed} ({invariant.Detail})")));

    private static void AssertBatchedProvider(
        IStorageProviderFactory factory,
        string connectionString,
        string providerName)
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("w3-batched-" + providerName + "-" + Guid.NewGuid().ToString("N")),
            Name = "w3_batched_" + providerName + "_" + Guid.NewGuid().ToString("N"),
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 450, IsNullable = false },
                new() { Name = "value", Type = PortableType.String, MaxLength = 256 },
                new() { Name = "createdAt", Type = PortableType.DateTimeOffset, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Concurrency = ConcurrencyDeclaration.Optimistic()
        };
        using var connection = factory.Create(connectionString);
        connection.Schema.Apply(unit);
        var firstCreatedAt = DateTimeOffset.UnixEpoch.AddDays(1);
        var secondCreatedAt = DateTimeOffset.UnixEpoch.AddDays(2);
        var thirdCreatedAt = DateTimeOffset.UnixEpoch.AddDays(3);

        using (var first = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit))
        {
            first.Stage(RowWrite.Upsert(unit, Values("same", "first", firstCreatedAt)));
            var report = first.CommitWithOutcomes();
            Assert.True(report.IsSuccessful);
            Assert.Equal(1, report.Outcomes.Single().Outcome.Version);
        }

        using (var second = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit))
        {
            second.Stage(RowWrite.Upsert(unit, Values("same", "second", secondCreatedAt), WriteOptions.IfVersion(1)));
            var report = second.CommitWithOutcomes();
            Assert.True(report.IsSuccessful);
            Assert.Equal(2, report.Outcomes.Single().Outcome.Version);
        }

        var read = connection.OpenSession(unit, StorageAccess.Global)
            .Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "same" }));
        Assert.NotNull(read);
        Assert.Equal("second", read!.Values.Values["value"]);
        Assert.Equal(firstCreatedAt, read.Values.Values["createdAt"]);
        Assert.Equal(2, read.Version);

        using var stale = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, unit);
        stale.Stage(RowWrite.Upsert(unit, Values("same", "stale", thirdCreatedAt), WriteOptions.IfVersion(1)));
        var error = Assert.Throws<BatchWriteException>(() => stale.CommitWithOutcomes());
        Assert.Equal(WriteOutcomeStatus.ConcurrencyConflict, error.Outcomes.Single().Outcome.Status);
        Assert.Equal("second", connection.OpenSession(unit, StorageAccess.Global)
            .Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "same" }))!.Values.Values["value"]);
    }

    /// <summary>
    /// One session, many concurrent callers — the shape Elsa's storage session source produces, which it
    /// caches by (target, unit, access) and hands to every matching caller. Before the connection gate this
    /// failed on PostgreSQL with NpgsqlOperationInProgressException and on SQL Server with "already an open
    /// DataReader"; SQLite serialized internally and MongoDB's driver is thread-safe, which is why only two
    /// of four providers ever showed it (elsa-foundation#1449).
    /// </summary>
    [SkippableFact]
    public async Task PostgreSql_one_shared_session_serializes_concurrent_readers_and_writers()
    {
        using var store = PostgreSqlStore.OpenOrSkip();
        using var connection = new PostgreSqlProviderFactory().Create(store.ConnectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("shared-session-" + suffix),
            Name = "shared_session_" + suffix,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 450, IsNullable = false },
                new() { Name = "value", Type = PortableType.String, MaxLength = 256 },
                new() { Name = "createdAt", Type = PortableType.DateTimeOffset, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Concurrency = ConcurrencyDeclaration.None
        };
        connection.Schema.Apply(unit);

        // Exactly one session, as the session source would hand out.
        var session = connection.OpenSession(unit, StorageAccess.Global);
        for (var seed = 0; seed < 8; seed++)
            session.Upsert(Values($"row-{seed}", "seed", DateTimeOffset.UnixEpoch), WriteOptions.Unconditional);

        var failures = new ConcurrentBag<Exception>();
        var readers = Enumerable.Range(0, 16).Select(index => Task.Run(() =>
        {
            try
            {
                for (var pass = 0; pass < 8; pass++)
                    _ = session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = $"row-{index % 8}" }));
            }
            catch (Exception failure) { failures.Add(failure); }
        }));
        var writers = Enumerable.Range(0, 8).Select(index => Task.Run(() =>
        {
            try
            {
                for (var pass = 0; pass < 8; pass++)
                    session.Upsert(Values($"row-{index}", $"pass-{pass}", DateTimeOffset.UnixEpoch), WriteOptions.Unconditional);
            }
            catch (Exception failure) { failures.Add(failure); }
        }));

        await Task.WhenAll(readers.Concat(writers));

        Assert.True(failures.IsEmpty,
            "Concurrent use of one shared session must serialize, not fault: " +
            string.Join("; ", failures.Select(failure => failure.GetType().Name + ": " + failure.Message)));
        for (var index = 0; index < 8; index++)
            Assert.NotNull(session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = $"row-{index}" })));
    }

    /// <summary>
    /// The property the connection gate cannot provide: owned sessions run genuinely CONCURRENTLY rather
    /// than queueing. Each caller opens its own session, so each has its own connection; if owned sessions
    /// secretly shared one, the gate would serialize them and the elapsed time would expand to the serial
    /// sum. Also proves the release half — the reason per-call sessions were rejected before this seam is
    /// that every one leaked a connection for the provider's lifetime (groundwork-v2#233).
    /// </summary>
    [SkippableFact]
    public async Task PostgreSql_owned_sessions_run_concurrently_and_release_their_connections()
    {
        using var store = PostgreSqlStore.OpenOrSkip();
        using var connection = new PostgreSqlProviderFactory().Create(store.ConnectionString);
        var suffix = Guid.NewGuid().ToString("N");
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("owned-session-" + suffix),
            Name = "owned_session_" + suffix,
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 450, IsNullable = false },
                new() { Name = "value", Type = PortableType.String, MaxLength = 256 },
                new() { Name = "createdAt", Type = PortableType.DateTimeOffset, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Concurrency = ConcurrencyDeclaration.None
        };
        connection.Schema.Apply(unit);

        var delayFunction = "owned_session_delay_" + suffix;
        var delayTrigger = "owned_session_trigger_" + suffix;
        await using (var delayConnection = new NpgsqlConnection(store.ConnectionString))
        {
            await delayConnection.OpenAsync();
            await using var delayCommand = delayConnection.CreateCommand();
            delayCommand.CommandText = $"""
                CREATE FUNCTION "{delayFunction}"() RETURNS trigger AS $function$
                BEGIN
                    PERFORM pg_sleep(1);
                    RETURN NEW;
                END;
                $function$ LANGUAGE plpgsql;
                CREATE TRIGGER "{delayTrigger}"
                BEFORE INSERT ON "{unit.Name}"
                FOR EACH ROW EXECUTE FUNCTION "{delayFunction}"();
                """;
            await delayCommand.ExecuteNonQueryAsync();
        }

        const int callers = 8;
        using var observer = new OwnedOperationOverlapObserver(callers);
        var sessions = Enumerable.Range(0, callers)
            .Select(_ => connection.OpenOwnedSession(unit, StorageAccess.Global, observer))
            .ToArray();
        observer.Arm();

        var work = sessions.Select((owned, index) => Task.Run(() =>
        {
            return owned.Upsert(
                Values($"row-{index}", "overlapped", DateTimeOffset.UnixEpoch),
                WriteOptions.Unconditional);
        })).ToArray();

        Exception? primaryFailure = null;
        try
        {
            Assert.True(observer.AllCommandsEntered.Wait(TimeSpan.FromSeconds(30)),
                "Owned-session commands queued behind a shared provider gate instead of overlapping.");
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            observer.Release.Set();
            var outcomes = await Task.WhenAll(work);
            elapsed.Stop();
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5),
                $"Eight one-second PostgreSQL commands took {elapsed.Elapsed}; owned sessions were physically serialized.");
            Assert.All(outcomes, outcome => Assert.True(outcome.Succeeded));
        }
        catch (Exception failure)
        {
            primaryFailure = failure;
        }
        finally
        {
            observer.Release.Set();
            try
            {
                await Task.WhenAll(work);
            }
            catch (Exception cleanupFailure)
            {
                primaryFailure ??= cleanupFailure;
            }

            foreach (var session in sessions)
            {
                try
                {
                    await session.DisposeAsync();
                }
                catch (Exception cleanupFailure)
                {
                    primaryFailure ??= cleanupFailure;
                }
            }
        }

        if (primaryFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryFailure).Throw();

        // Released: a fresh session still reads every row, and the provider never accumulated the eight
        // connections — they went back to the pool when each session was disposed.
        var reader = connection.OpenSession(unit, StorageAccess.Global);
        for (var index = 0; index < callers; index++)
            Assert.NotNull(reader.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = $"row-{index}" })));
    }

    private sealed class OwnedOperationOverlapObserver(int callers) : IProviderCommandObserver, IDisposable
    {
        private int armed;

        internal CountdownEvent AllCommandsEntered { get; } = new(callers);

        internal ManualResetEventSlim Release { get; } = new();

        internal void Arm() => Volatile.Write(ref armed, 1);

        public void Observe(ProviderCommandEvent command)
        {
            if (Volatile.Read(ref armed) == 0 || command.IsProbe)
                return;
            AllCommandsEntered.Signal();
            Release.Wait(TimeSpan.FromSeconds(30));
        }

        public void Dispose()
        {
            Release.Set();
            AllCommandsEntered.Dispose();
            Release.Dispose();
        }
    }

    private static StorageValues Values(string id, string value, DateTimeOffset createdAt) =>
        new(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = id, ["value"] = value, ["createdAt"] = createdAt
        });
}

internal sealed class BrokenConcurrencyFactory : IConcurrencyProviderFactory
{
    public string ProviderName => "broken";

    public IConcurrencyProviderConnection Create(string connectionString, StorageUnit declaration) =>
        new BrokenConcurrencyConnection();
}

internal sealed class TemporarySqliteStore : IDisposable
{
    private TemporarySqliteStore(string directory)
    {
        DirectoryPath = directory;
        ConnectionString = $"Data Source={Path.Combine(directory, "w2.db")}";
    }

    private string DirectoryPath { get; }

    public string ConnectionString { get; }

    public static TemporarySqliteStore Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), "groundwork-w2-sqlite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return new TemporarySqliteStore(directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class PostgreSqlStore : IDisposable
{
    private PostgreSqlStore(string adminConnectionString, string schema, string connectionString)
    {
        this.adminConnectionString = adminConnectionString;
        this.schema = schema;
        ConnectionString = connectionString;
    }

    private readonly string adminConnectionString;
    private readonly string schema;

    public string ConnectionString { get; }

    public static PostgreSqlStore OpenOrSkip()
    {
        var baseConnection = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(baseConnection),
            "Set GROUNDWORK_POSTGRES_CONNECTION to run the live PostgreSQL W2 harness.");
        var schema = "w2_" + Guid.NewGuid().ToString("N");
        using var admin = new NpgsqlConnection(baseConnection);
        try
        {
            admin.Open();
        }
        catch (Exception exception)
        {
            Skip.If(true, $"PostgreSQL is unavailable: {exception.Message}");
            throw;
        }

        using (var command = admin.CreateCommand())
        {
            command.CommandText = $"CREATE SCHEMA \"{schema}\";";
            command.ExecuteNonQuery();
        }
        var builder = new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema };
        return new PostgreSqlStore(baseConnection, schema, builder.ConnectionString);
    }

    public void Dispose()
    {
        // Npgsql pools per connection string, and every store here gets its own SearchPath, so this
        // store's pool is one no later test can ever reuse. Returning the sessions is not enough:
        // disposing an NpgsqlConnection hands it back to that pool rather than closing the socket, so
        // without this the idle physical connections survive for the life of the process. Nine
        // PostgreSQL tests at WriterCount = 32 exhaust a default max_connections = 100 that way, which
        // surfaces as 53300 in whichever tests happen to run once the budget is gone (#62).
        // Clearing before the drop also releases anything still holding a lock on the schema.
        using (var pooled = new NpgsqlConnection(ConnectionString))
            NpgsqlConnection.ClearPool(pooled);

        using var admin = new NpgsqlConnection(adminConnectionString);
        admin.Open();
        using var command = admin.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;";
        command.ExecuteNonQuery();
    }
}

internal sealed class BrokenConcurrencyConnection : IConcurrencyProviderConnection
{
    private readonly Dictionary<string, ConcurrencyStoredRow> rows = new(StringComparer.Ordinal);

    public void ApplySchema()
    {
    }

    public IConcurrencyProviderSession OpenSession() => new BrokenConcurrencySession(rows);

    public void Dispose()
    {
    }
}

internal sealed class BrokenConcurrencySession(Dictionary<string, ConcurrencyStoredRow> rows)
    : IConcurrencyProviderSession
{
    public ConcurrencyWriteOutcome ConditionalUpsert(ConcurrencyWriteRequest request)
    {
        lock (rows)
        {
            var version = rows.TryGetValue(request.Key, out var existing)
                ? existing.Version + 1
                : 1;
            rows[request.Key] = new ConcurrencyStoredRow(
                request.Key,
                request.Value,
                existing?.CreatedAt ?? request.CreatedAt,
                version);
            return new ConcurrencyWriteOutcome(ConcurrencyWriteOutcomeStatus.Updated, version);
        }
    }

    public ValueTask<ConcurrencyWriteOutcome> ConditionalUpsertAsync(
        ConcurrencyWriteRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ConditionalUpsert(request));

    public ConcurrencyStoredRow? Read(string key)
    {
        lock (rows)
            return rows.TryGetValue(key, out var row) ? row : null;
    }

    public ValueTask<ConcurrencyStoredRow?> ReadAsync(string key, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(key));

    public void Dispose()
    {
    }
}
