using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.MongoDb.TestingAdapter;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Testing;
using Microsoft.Data.Sqlite;
using MongoDB.Bson;
using MongoDB.Driver;
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
    public void SQLite_scoped_append_returns_inserted_and_is_readable_in_its_scope()
    {
        var path = Path.Combine(Path.GetTempPath(), "groundwork-idempotency-scoped-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var connection = new SqliteProviderFactory().Create($"Data Source={path}");
            var unit = Unit("idempotency-scoped-" + Guid.NewGuid().ToString("N"), scope: ScopePolicy.Scoped);
            Assert.True(connection.Schema.Apply(unit).Applied);
            var session = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-a")));

            var outcome = session.Append(new OperationId(DateTimeOffset.UnixEpoch, "scoped-operation"), [Values("scoped-row")]);

            Assert.Equal(WriteOutcomeStatus.Inserted, outcome.Status);
            Assert.NotNull(session.Read(Key("scoped-row")));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void SQLite_idempotency_cleanup_index_covers_unit_and_provider_time()
    {
        var path = Path.Combine(Path.GetTempPath(), "groundwork-idempotency-index-" + Guid.NewGuid().ToString("N") + ".db");
        var ledger = "ledger_index_" + Guid.NewGuid().ToString("N");
        try
        {
            using (var connection = new SqliteProviderFactory().Create($"Data Source={path}"))
            {
                var unit = Unit("idempotency-index-sqlite-" + Guid.NewGuid().ToString("N"), ledgerName: ledger);
                Assert.True(connection.Schema.Apply(unit).Applied);
                Assert.Equal(WriteOutcomeStatus.Inserted,
                    connection.OpenSession(unit, StorageAccess.Global)
                        .Append(new OperationId(DateTimeOffset.UnixEpoch, "index-operation"), [Values("indexed")]).Status);
            }

            using var native = new SqliteConnection($"Data Source={path}");
            native.Open();
            using var command = native.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name LIKE '__groundwork_ledger_cleanup_%';";
            var definition = Convert.ToString(command.ExecuteScalar());
            Assert.Contains("\"unit\", \"committed_at\"", definition, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [SkippableFact]
    public void MongoDB_idempotency_cleanup_index_covers_unit_and_provider_time()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run the MongoDB ledger index proof.");
        using var connection = new MongoDbProviderFactory().Create(connectionString!);
        var nativeConnection = Assert.IsType<MongoDbProviderConnection>(connection);
        var ledger = "ledger_index_" + Guid.NewGuid().ToString("N");
        var unit = Unit("idempotency-index-mongo-" + Guid.NewGuid().ToString("N"), ledgerName: ledger);
        try
        {
            Assert.True(connection.Schema.Apply(unit).Applied);
            Assert.Equal(MongoWriteOutcomeStatus.Inserted,
                connection.OpenSession(unit, MongoStorageAccess.Global)
                    .Append(new OperationId(DateTimeOffset.UnixEpoch, "index-operation"), [new MongoStorageValues(new Dictionary<string, object?> { ["id"] = "indexed", ["payload"] = "indexed" })]).Status);
        }
        catch (InvalidOperationException exception) when (IsStandaloneMongoCapabilityRefusal(exception))
        {
            Skip.If(true, "MongoDB idempotency requires a transaction-capable deployment.");
            return;
        }

        var index = nativeConnection.Database.GetCollection<BsonDocument>(ledger).Indexes.List()
            .ToList().Single(item => item["name"] == "__groundwork_ledger_cleanup");
        Assert.Equal("unit", index["key"].AsBsonDocument.GetElement(0).Name);
        Assert.Equal("committed_at", index["key"].AsBsonDocument.GetElement(1).Name);
    }

    [SkippableFact]
    public void PostgreSQL_scoped_append_returns_inserted_and_is_readable_in_its_scope()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run the PostgreSQL scoped idempotency proof.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertScopedAppend(connection, "postgresql");
    }

    [SkippableFact]
    public void SQLServer_scoped_append_returns_inserted_and_is_readable_in_its_scope()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run the SQL Server scoped idempotency proof.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertScopedAppend(connection, "sqlserver");
    }

    [Fact]
    public void InMemory_duplicate_append_keys_are_refused_and_leave_no_payload_or_ledger()
    {
        using var connection = new InMemoryProviderFactory().Create("idempotency-duplicate-inmemory-" + Guid.NewGuid().ToString("N"));
        AssertDuplicateAppendRejected(connection, "inmemory");
    }

    [Fact]
    public void SQLite_duplicate_append_keys_are_refused_and_leave_no_payload_or_ledger()
    {
        var path = Path.Combine(Path.GetTempPath(), "groundwork-idempotency-duplicate-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var connection = new SqliteProviderFactory().Create($"Data Source={path}");
            AssertDuplicateAppendRejected(connection, "sqlite");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void InMemory_append_allows_multiple_provider_sequence_rows()
    {
        using var connection = new InMemoryProviderFactory().Create("idempotency-generated-inmemory-" + Guid.NewGuid().ToString("N"));
        AssertGeneratedAppend(connection, "inmemory");
    }

    [Fact]
    public void SQLite_append_allows_multiple_provider_sequence_rows()
    {
        var path = Path.Combine(Path.GetTempPath(), "groundwork-idempotency-generated-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var connection = new SqliteProviderFactory().Create($"Data Source={path}");
            AssertGeneratedAppend(connection, "sqlite");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [SkippableFact]
    public void PostgreSQL_append_allows_multiple_provider_sequence_rows()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run the PostgreSQL generated-key append proof.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertGeneratedAppend(connection, "postgresql");
    }

    [SkippableFact]
    public void SQLServer_append_allows_multiple_provider_sequence_rows()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run the SQL Server generated-key append proof.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertGeneratedAppend(connection, "sqlserver");
    }

    [SkippableFact]
    public void MongoDB_append_allows_multiple_provider_sequence_rows()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run the MongoDB generated-key append proof.");
        using var connection = new MongoDbTestingFactory().Create(connectionString!);
        try
        {
            AssertGeneratedAppend(connection, "mongodb");
        }
        catch (InvalidOperationException exception) when (IsStandaloneMongoCapabilityRefusal(exception))
        {
            Skip.If(true, "MongoDB idempotency requires a transaction-capable deployment.");
        }
    }

    [SkippableFact]
    public void PostgreSQL_duplicate_append_keys_are_refused_and_leave_no_payload_or_ledger()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run the PostgreSQL duplicate idempotency proof.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertDuplicateAppendRejected(connection, "postgresql");
    }

    [SkippableFact]
    public void SQLServer_duplicate_append_keys_are_refused_and_leave_no_payload_or_ledger()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run the SQL Server duplicate idempotency proof.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertDuplicateAppendRejected(connection, "sqlserver");
    }

    [SkippableFact]
    public void MongoDB_duplicate_append_keys_are_refused_and_leave_no_payload_or_ledger()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run the MongoDB duplicate idempotency proof.");
        using var connection = new MongoDbTestingFactory().Create(connectionString!);
        try
        {
            AssertDuplicateAppendRejected(connection, "mongodb");
        }
        catch (InvalidOperationException exception) when (IsStandaloneMongoCapabilityRefusal(exception))
        {
            Skip.If(true, "MongoDB idempotency requires a transaction-capable deployment.");
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

    [Fact]
    public void InMemory_concurrent_unit_of_work_commits_merge_ledgers_without_losing_replay_entries()
    {
        using var connection = new InMemoryProviderFactory().Create("idempotency-uow-ledger-" + Guid.NewGuid().ToString("N"));
        var firstUnit = Unit("idempotency-uow-ledger-first-" + Guid.NewGuid().ToString("N"));
        var secondUnit = Unit("idempotency-uow-ledger-second-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(firstUnit).Applied);
        Assert.True(connection.Schema.Apply(secondUnit).Applied);

        var firstOperation = new OperationId(DateTimeOffset.UnixEpoch, "first-operation");
        var secondOperation = new OperationId(DateTimeOffset.UnixEpoch, "second-operation");
        using var first = connection.BeginUnitOfWork(StorageAccess.Global, firstUnit);
        using var second = connection.BeginUnitOfWork(StorageAccess.Global, secondUnit);
        Assert.Equal(WriteOutcomeStatus.Inserted, second.OpenSession(secondUnit).Append(secondOperation, [Values("second")]).Status);
        second.Commit();
        Assert.Equal(WriteOutcomeStatus.Inserted, first.OpenSession(firstUnit).Append(firstOperation, [Values("first")]).Status);
        first.Commit();

        var replay = connection.OpenSession(secondUnit, StorageAccess.Global)
            .Append(secondOperation, [Values("second-replay")]);
        Assert.Equal(WriteOutcomeStatus.Replayed, replay.Status);
    }

    [Fact]
    public void InMemory_short_window_reclamation_does_not_remove_another_units_valid_entry()
    {
        using var connection = new InMemoryProviderFactory().Create("idempotency-ledger-partition-" + Guid.NewGuid().ToString("N"));
        var shortUnit = Unit("idempotency-short-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(5));
        var longUnit = Unit("idempotency-long-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(10));
        Assert.True(connection.Schema.Apply(shortUnit).Applied);
        Assert.True(connection.Schema.Apply(longUnit).Applied);
        var longSession = connection.OpenSession(longUnit, StorageAccess.Global);
        var longOperation = new OperationId(DateTimeOffset.UnixEpoch, "long-operation");
        Assert.Equal(WriteOutcomeStatus.Inserted, longSession.Append(longOperation, [Values("long-first")]).Status);

        Thread.Sleep(20);
        var shortSession = connection.OpenSession(shortUnit, StorageAccess.Global);
        Assert.Equal(WriteOutcomeStatus.Inserted,
            shortSession.Append(new OperationId(DateTimeOffset.UnixEpoch, "short-operation"), [Values("short")]).Status);

        Assert.Equal(WriteOutcomeStatus.Replayed, longSession.Append(longOperation, [Values("long-replay")]).Status);
    }

    [Fact]
    public void Ledger_names_are_limited_to_the_portable_postgreSQL_identifier_byte_budget()
    {
        var tooLongUtf8Name = new string('é', 32);
        Assert.Throws<ArgumentException>(() => new SchemaSubject(Unit(
            "idempotency-ledger-name-" + Guid.NewGuid().ToString("N"),
            ledgerName: tooLongUtf8Name)));
    }

    [Fact]
    public void Ledger_names_cannot_collide_with_units_or_provider_catalogs()
    {
        var collidingName = "idempotency-ledger-collision-" + Guid.NewGuid().ToString("N");
        Assert.Throws<ArgumentException>(() => new SchemaSubject(Unit(collidingName, ledgerName: collidingName)));
        Assert.Throws<ArgumentException>(() => new SchemaSubject(Unit(
            "idempotency-ledger-provider-collision-" + Guid.NewGuid().ToString("N"),
            ledgerName: "__groundwork_metadata")));
        Assert.Throws<ArgumentException>(() => new SchemaSubject(Unit(
            "idempotency-ledger-provider-collision-" + Guid.NewGuid().ToString("N"),
            ledgerName: "__groundwork_sequences")));
    }

    [SkippableFact]
    public void MongoDB_concurrent_duplicate_nonce_inside_transactions_returns_one_replay()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run the MongoDB duplicate race proof.");
        using var firstConnection = new MongoDbTestingFactory().Create(connectionString!);
        using var secondConnection = new MongoDbTestingFactory().Create(connectionString!);
        var unit = Unit("idempotency-mongo-race-" + Guid.NewGuid().ToString("N"));
        Assert.True(firstConnection.Schema.Apply(unit).Applied);
        var outcomes = Task.WhenAll(
                Task.Run(() => AppendInUnitOfWork(firstConnection, unit, "race-first")),
                Task.Run(() => AppendInUnitOfWork(secondConnection, unit, "race-second")))
            .GetAwaiter().GetResult();

        Assert.Equal(1, outcomes.Count(outcome => outcome.Status == WriteOutcomeStatus.Inserted));
        Assert.Equal(1, outcomes.Count(outcome => outcome.Status == WriteOutcomeStatus.Replayed));
    }

    private static WriteOutcome AppendInUnitOfWork(
        IStorageProviderConnection connection,
        StorageUnit unit,
        string payload)
    {
        using var work = connection.BeginUnitOfWork(StorageAccess.Global, unit);
        var outcome = work.OpenSession(unit).Append(
            new OperationId(DateTimeOffset.UnixEpoch, "race-operation"), [Values(payload)]);
        work.Commit();
        return outcome;
    }

    [SkippableFact]
    public void MongoDB_schema_drift_surfaces_idempotency_window_and_ledger_changes()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run the MongoDB idempotency schema drift proof.");
        using var connection = new MongoDbProviderFactory().Create(connectionString!);
        var name = "idempotency-mongo-drift-" + Guid.NewGuid().ToString("N");
        var applied = Unit(name, TimeSpan.FromMinutes(1), ledgerName: "drift_ledger_a");
        Assert.True(connection.Schema.Apply(applied).Applied);
        var changedWindow = Unit(name, TimeSpan.FromMinutes(2), ledgerName: "drift_ledger_a");
        Assert.Throws<MongoSchemaConflictException>(() => connection.OpenSession(changedWindow, MongoStorageAccess.Global));
        var changedLedger = Unit(name, TimeSpan.FromMinutes(1), ledgerName: "drift_ledger_b");
        Assert.Throws<MongoSchemaConflictException>(() => connection.OpenSession(changedLedger, MongoStorageAccess.Global));
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
        catch (InvalidOperationException exception) when (IsStandaloneMongoCapabilityRefusal(exception))
        {
            Skip.If(true, "MongoDB idempotency requires a transaction-capable deployment.");
        }
    }

    [Fact]
    public void InMemory_replay_expiry_uses_commit_time_and_failed_batches_roll_back()
    {
        using var connection = new InMemoryProviderFactory().Create("idempotency-expiry-inmemory-" + Guid.NewGuid().ToString("N"));
        AssertExpiryAndRollback(connection, "inmemory");
    }

    [Fact]
    public void SQLite_replay_expiry_uses_commit_time_and_failed_batches_roll_back()
    {
        var path = Path.Combine(Path.GetTempPath(), "groundwork-idempotency-expiry-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var connection = new SqliteProviderFactory().Create($"Data Source={path}");
            AssertExpiryAndRollback(connection, "sqlite");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [SkippableFact]
    public void PostgreSQL_replay_expiry_uses_commit_time_and_failed_batches_roll_back()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run the PostgreSQL expiry proof.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertExpiryAndRollback(connection, "postgresql");
    }

    [SkippableFact]
    public void SQLServer_replay_expiry_uses_commit_time_and_failed_batches_roll_back()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run the SQL Server expiry proof.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertExpiryAndRollback(connection, "sqlserver");
    }

    [SkippableFact]
    public void MongoDB_replay_expiry_uses_commit_time_and_failed_batches_roll_back()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run the MongoDB expiry proof.");
        using var connection = new MongoDbTestingFactory().Create(connectionString!);
        try
        {
            AssertExpiryAndRollback(connection, "mongodb");
        }
        catch (InvalidOperationException exception) when (IsStandaloneMongoCapabilityRefusal(exception))
        {
            Skip.If(true, "MongoDB idempotency requires a transaction-capable deployment.");
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

    private static void AssertScopedAppend(IStorageProviderConnection connection, string provider)
    {
        var unit = Unit("s2scope-" + provider + "-" + Guid.NewGuid().ToString("N"), scope: ScopePolicy.Scoped);
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-a")));
        var outcome = session.Append(new OperationId(DateTimeOffset.UnixEpoch, "scoped-operation"), [Values("scoped-row")]);
        Assert.Equal(WriteOutcomeStatus.Inserted, outcome.Status);
        Assert.NotNull(session.Read(Key("scoped-row")));
    }

    private static void AssertGeneratedAppend(IStorageProviderConnection connection, string provider)
    {
        var unit = GeneratedUnit("s2generated-" + provider + "-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var operation = new OperationId(DateTimeOffset.UnixEpoch, "generated-operation");

        var outcome = session.Append(operation, [
            new StorageValues(new Dictionary<string, object?> { ["payload"] = "first" }),
            new StorageValues(new Dictionary<string, object?> { ["payload"] = "second" })
        ]);

        Assert.Equal(WriteOutcomeStatus.Inserted, outcome.Status);
        Assert.Equal("first", session.Read(new StorageKey(new Dictionary<string, object?> { ["sequence"] = 1L }))!.Values.Values["payload"]);
        Assert.Equal("second", session.Read(new StorageKey(new Dictionary<string, object?> { ["sequence"] = 2L }))!.Values.Values["payload"]);
    }

    private static bool IsStandaloneMongoCapabilityRefusal(InvalidOperationException exception) =>
        exception.Message.Contains("standalone MongoDB cannot provide", StringComparison.Ordinal);

    private static void AssertDuplicateAppendRejected(IStorageProviderConnection connection, string provider)
    {
        var unit = Unit("s2dup-" + provider + "-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var operation = new OperationId(DateTimeOffset.UnixEpoch, "duplicate-operation");
        Assert.Throws<ArgumentException>(() => session.Append(operation, [Values("same"), Values("same")]));
        Assert.Null(session.Read(Key("same")));
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Append(operation, [Values("accepted")]).Status);
    }

    private static void AssertExpiryAndRollback(IStorageProviderConnection connection, string provider)
    {
        var unit = Unit("idempotency-expiry-" + provider + "-" + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(5));
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var operation = new OperationId(DateTimeOffset.UnixEpoch, "expired-operation");

        var failure = Record.Exception(() => session.Append(operation, [
            Values("duplicate"),
            new StorageValues(new Dictionary<string, object?> { ["id"] = "invalid" })
        ]));
        Assert.NotNull(failure);
        if (provider == "mongodb")
            Assert.Contains("required", failure!.Message, StringComparison.OrdinalIgnoreCase);
        else
            Assert.IsType<ArgumentException>(failure);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Append(operation, [Values("accepted")]).Status);

        Thread.Sleep(50);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Append(operation, [Values("after-window")]).Status);
        Assert.NotNull(session.Read(Key("after-window")));
    }

    private static StorageUnit Unit(
        string name,
        TimeSpan? window = null,
        ScopePolicy scope = ScopePolicy.Global,
        string? ledgerName = null) => new()
    {
        Id = new StorageUnitId(name),
        Name = name,
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, MaxLength = 450, IsNullable = false },
            new() { Name = "payload", Type = PortableType.String, MaxLength = 450, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Scope = scope,
        AppendIdempotency = new AppendIdempotencyDeclaration
        {
            Window = window ?? TimeSpan.FromMinutes(10),
            LedgerName = ledgerName ?? "__groundwork_operations"
        }
    };

    private static StorageUnit GeneratedUnit(string name) => new()
    {
        Id = new StorageUnitId(name),
        Name = name,
        Columns =
        [
            new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
            new() { Name = "payload", Type = PortableType.String, MaxLength = 450, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["sequence"] },
        AppendIdempotency = new AppendIdempotencyDeclaration { Window = TimeSpan.FromMinutes(10) }
    };

    private static StorageValues Values(string id) => new(new Dictionary<string, object?>
    {
        ["id"] = id,
        ["payload"] = id
    });

    private static StorageKey Key(string id) => new(new Dictionary<string, object?> { ["id"] = id });
}
