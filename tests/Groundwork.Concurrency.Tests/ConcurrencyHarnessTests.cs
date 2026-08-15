using Groundwork.MongoDb.TestingAdapter;
using Groundwork.Kernel;
using Groundwork.PostgreSql;
using Groundwork.Sqlite;
using Groundwork.Testing;
using MongoDB.Driver;
using Npgsql;
using Xunit;

namespace Groundwork.Concurrency.Tests;

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
            new StorageProviderConcurrencyFactory("mongodb", new MongoDbTestingFactory()),
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

    public ConcurrencyStoredRow? Read(string key)
    {
        lock (rows)
            return rows.TryGetValue(key, out var row) ? row : null;
    }

    public void Dispose()
    {
    }
}
