using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.MongoDb.TestingAdapter;
using Groundwork.PostgreSql;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Testing;
using System.Text.Json;
using Xunit;

namespace Groundwork.StreamCapabilities.Tests;

public sealed class IdempotencyProofTests
{
    [Fact]
    public void Providers_advertise_the_idempotent_append_capability()
    {
        using var connection = new InMemoryProviderFactory().Create("idempotency-capability-" + Guid.NewGuid().ToString("N"));

        Assert.Contains(connection.Capabilities, capability => capability.Id == BatchWriteCapabilities.AppendIdempotency);
    }

    [Fact]
    public void Schema_snapshot_preserves_append_idempotency_declaration()
    {
        var unit = Unit("idempotency-snapshot-" + Guid.NewGuid().ToString("N"), TimeSpan.FromSeconds(7));
        var snapshot = new SchemaSubject(unit).Definition;

        Assert.Equal(unit.AppendIdempotency, snapshot.AppendIdempotency);
        Assert.Equal(unit.AppendIdempotency, snapshot.Idempotency);
    }

    [Fact]
    public void Kernel_json_round_trip_preserves_append_idempotency_declaration()
    {
        var unit = Unit("idempotency-json-" + Guid.NewGuid().ToString("N"), TimeSpan.FromSeconds(7));
        var json = JsonSerializer.Serialize(unit);
        var roundTrip = JsonSerializer.Deserialize<StorageUnit>(json);

        Assert.NotNull(roundTrip);
        Assert.Equal(unit.AppendIdempotency, roundTrip!.AppendIdempotency);
    }

    [Fact]
    public void InMemory_replay_within_window_returns_replayed_and_writes_nothing()
    {
        using var connection = new InMemoryProviderFactory().Create("idempotency-inmemory-" + Guid.NewGuid().ToString("N"));
        AssertReplaySemantics(connection, "inmemory");
    }

    [Fact]
    public void SQLite_replay_within_window_returns_replayed_and_writes_nothing()
    {
        var path = Path.Combine(Path.GetTempPath(), "groundwork-idempotency-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var connection = new SqliteProviderFactory().Create($"Data Source={path}");
            AssertReplaySemantics(connection, "sqlite");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void InMemory_unit_of_work_commits_append_payload_and_ledger_together()
    {
        using var connection = new InMemoryProviderFactory().Create("idempotency-uow-" + Guid.NewGuid().ToString("N"));
        var unit = Unit("idempotency-uow-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(unit).Applied);
        var operation = new OperationId(DateTimeOffset.UtcNow, "uow-operation");
        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, unit))
        {
            var session = work.OpenSession(unit);
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Append(operation, [Values("uow-row")]).Status);
            Assert.Equal(WriteOutcomeStatus.Replayed, session.Append(operation, [Values("uow-replay")]).Status);
            work.Commit();
        }

        var committed = connection.OpenSession(unit, StorageAccess.Global);
        Assert.NotNull(committed.Read(Key("uow-row")));
        Assert.Null(committed.Read(Key("uow-replay")));
    }

    [SkippableFact]
    public void PostgreSQL_replay_within_window_returns_replayed_and_writes_nothing()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run the PostgreSQL idempotency proof.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertReplaySemantics(connection, "postgresql");
    }

    [SkippableFact]
    public void SQLServer_replay_within_window_returns_replayed_and_writes_nothing()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run the SQL Server idempotency proof.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertReplaySemantics(connection, "sqlserver");
    }

    [SkippableFact]
    public void MongoDB_replay_within_window_returns_replayed_and_writes_nothing()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run the MongoDB idempotency proof.");
        using var connection = new MongoDbTestingFactory().Create(connectionString!);
        try
        {
            AssertReplaySemantics(connection, "mongodb");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("transaction", StringComparison.OrdinalIgnoreCase))
        {
            Skip.If(true, "MongoDB idempotency requires a transaction-capable deployment.");
        }
    }

    [Theory]
    [InlineData("inmemory")]
    [InlineData("sqlite")]
    public void Replay_expiry_is_based_on_provider_commit_time_and_failed_batches_do_not_leave_a_ledger_row(string provider)
    {
        string? path = null;
        using var connection = provider == "inmemory"
            ? new InMemoryProviderFactory().Create("idempotency-expiry-" + Guid.NewGuid().ToString("N"))
            : CreateSqlite(out path);
        try
        {
            var unit = Unit("idempotency-expiry-" + provider + "-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(5));
            Assert.True(connection.Schema.Apply(unit).Applied);
            var session = connection.OpenSession(unit, StorageAccess.Global);
            var operation = new OperationId(DateTimeOffset.UnixEpoch, "expired-operation");
            Assert.Throws<ArgumentException>(() => session.Append(operation, [Values("duplicate"), new StorageValues(new Dictionary<string, object?> { ["id"] = "invalid" })]));
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Append(operation, [Values("accepted")] ).Status);

            Thread.Sleep(20);
            Assert.Equal(WriteOutcomeStatus.Inserted, session.Append(operation, [Values("after-window")]).Status);
            Assert.NotNull(session.Read(Key("after-window")));
        }
        finally
        {
            if (provider == "sqlite")
            {
                try { File.Delete(path!); } catch { }
            }
        }
    }

    private static void AssertReplaySemantics(IStorageProviderConnection connection, string provider)
    {
        var unit = Unit("idempotency-" + provider + "-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var operation = new OperationId(DateTimeOffset.UnixEpoch, "same-operation");
        var first = session.Append(operation, [Values("one")]);
        var replay = session.Append(operation, [Values("two")]);

        Assert.Equal(WriteOutcomeStatus.Inserted, first.Status);
        Assert.Equal(WriteOutcomeStatus.Replayed, replay.Status);
        Assert.NotNull(session.Read(Key("one")));
        Assert.Null(session.Read(Key("two")));
    }

    private static IStorageProviderConnection CreateSqlite(out string path)
    {
        path = Path.Combine(Path.GetTempPath(), "groundwork-idempotency-expiry-" + Guid.NewGuid().ToString("N") + ".db");
        return new SqliteProviderFactory().Create($"Data Source={path}");
    }

    private static StorageUnit Unit(string name, TimeSpan? window = null) => new()
    {
        Id = new StorageUnitId(name),
        Name = name,
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, MaxLength = 450, IsNullable = false },
            new() { Name = "payload", Type = PortableType.String, MaxLength = 450, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        AppendIdempotency = new AppendIdempotencyDeclaration { Window = window ?? TimeSpan.FromMinutes(10) }
    };

    private static StorageValues Values(string id) => new(new Dictionary<string, object?>
    {
        ["id"] = id,
        ["payload"] = id
    });

    private static StorageKey Key(string id) => new(new Dictionary<string, object?> { ["id"] = id });
}
