using Groundwork.Kernel;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.Query.Model;
using Groundwork.SqlServer;
using Groundwork.Sqlite;
using Groundwork.Store;
using Groundwork.Testing;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Groundwork.StreamCapabilities.Tests;

public sealed class ExactAppendProofTests
{
    [Fact]
    public void Exact_append_extension_reports_stable_unsupported_capability_without_dispatch()
    {
        using var connection = new InMemoryProviderFactory().Create("exact-append-capability-" + Guid.NewGuid().ToString("N"));
        var unit = NonSequenceUnit("exact-append-capability-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(unit).Applied);
        var hidden = new CapabilityHidingSession(connection.OpenSession(unit, StorageAccess.Global));

        var refusal = Assert.Throws<NotSupportedException>(() => hidden.AppendWithOutcomes(
            new OperationId(DateTimeOffset.UtcNow, "unsupported-exact"),
            new StorageValues(new Dictionary<string, object?> { ["id"] = "one", ["payload"] = "one" })));
        Assert.Contains("GW-APPEND-003", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Legacy_provider_capability_factory_does_not_advertise_exact_append_without_opt_in()
    {
        var legacy = BatchWriteCapabilities.ForProvider("legacy", nativeBatch: false, exactOutcomeCost: "legacy", batchCost: "legacy");
        Assert.DoesNotContain(legacy, capability => capability.Id == BatchWriteCapabilities.ExactAppendOutcomes);

        using var connection = new InMemoryProviderFactory().Create("exact-append-capability-opt-in-" + Guid.NewGuid().ToString("N"));
        Assert.Contains(connection.Capabilities, capability => capability.Id == BatchWriteCapabilities.ExactAppendOutcomes);
    }

    [Fact]
    public void InMemory_exact_append_returns_ordered_generated_values_and_replays_them()
    {
        using var connection = new InMemoryProviderFactory().Create("exact-append-inmemory-" + Guid.NewGuid().ToString("N"));
        AssertExactAppend(connection, "inmemory");
    }

    [Fact]
    public void SQLite_exact_append_returns_ordered_generated_values_and_replays_them()
    {
        var path = Path.Combine(Path.GetTempPath(), "groundwork-exact-append-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using var connection = new SqliteProviderFactory().Create($"Data Source={path}");
            AssertExactAppend(connection, "sqlite");
            AssertExactAppendUnitOfWork(connection, "sqlite");
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [SkippableFact]
    public void PostgreSQL_exact_append_returns_ordered_generated_values_and_replays_them()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run the PostgreSQL exact append proof.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertExactAppend(connection, "postgresql");
        AssertExactAppendUnitOfWork(connection, "postgresql");
    }

    [SkippableFact]
    public void SQLServer_exact_append_returns_ordered_generated_values_and_replays_them()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run the SQL Server exact append proof.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertExactAppend(connection, "sqlserver");
        AssertExactAppendUnitOfWork(connection, "sqlserver");
    }

    [SkippableFact]
    public void MongoDB_exact_append_returns_ordered_generated_values_and_replays_them()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run the MongoDB exact append proof.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        Skip.If(!connection.Capabilities.Any(capability => capability.Id == BatchWriteCapabilities.ExactAppendOutcomes),
            "MongoDB deployment does not advertise transaction-backed exact append outcomes.");
        AssertExactAppend(connection, "mongodb");
        AssertExactAppendUnitOfWork(connection, "mongodb");
    }

    [SkippableFact]
    public void MongoDB_without_transaction_fit_refuses_exact_append_before_dispatch()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_STANDALONE_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_STANDALONE_CONNECTION to run the MongoDB capability refusal proof.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        Skip.If(connection.Capabilities.Any(capability => capability.Id == BatchWriteCapabilities.ExactAppendOutcomes),
            "The configured MongoDB deployment supports transactions; this proof targets standalone capability refusal.");
        var unit = NonSequenceUnit("exact-append-mongo-unsupported-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        Assert.False(session is IExactAppendStorageSession);
        var refusal = Assert.Throws<NotSupportedException>(() => session.AppendWithOutcomes(
            new OperationId(DateTimeOffset.UtcNow, "unsupported-exact"),
            new StorageValues(new Dictionary<string, object?> { ["id"] = "one", ["payload"] = "one" })));
        Assert.Contains("GW-APPEND-003", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InMemory_exact_append_normalizes_equivalent_timestamp_offsets_but_refuses_a_changed_instant()
    {
        using var connection = new InMemoryProviderFactory().Create("exact-append-time-" + Guid.NewGuid().ToString("N"));
        var unit = GeneratedUnit("exact-append-time-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var operation = new OperationId(DateTimeOffset.UtcNow, "timestamp-operation");
        var instant = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var first = session.AppendWithOutcomes(operation, [Values("first", instant)]);

        var equivalent = session.AppendWithOutcomes(operation, [Values("first", instant.ToOffset(TimeSpan.FromHours(2)))]);
        Assert.Equal(WriteOutcomeStatus.Replayed, equivalent.Status);
        Assert.Equal(first.Outcomes[0].GeneratedValues, equivalent.Outcomes[0].GeneratedValues);

        var changed = Assert.Throws<AppendIdempotencyConflictException>(() =>
            session.AppendWithOutcomes(operation, [Values("first", instant.AddSeconds(1))]));
        Assert.Equal(AppendIdempotencyConflictException.DiagnosticCode, changed.Message[..AppendIdempotencyConflictException.DiagnosticCode.Length]);
        Assert.Contains("new operation nonce", changed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InMemory_exact_append_canonicalizes_json_text_and_rejects_changed_json_without_consuming_sequence()
    {
        using var connection = new InMemoryProviderFactory().Create("exact-append-json-" + Guid.NewGuid().ToString("N"));
        var unit = JsonAppendUnit("exact-append-json-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var operation = new OperationId(DateTimeOffset.UtcNow, "json-operation");
        var first = session.AppendWithOutcomes(operation, [JsonValues("{\"b\":2,\"a\":{\"x\":true}}", 1L)]);

        using var equivalentDocument = JsonDocument.Parse("{\"a\":{\"x\":true},\"b\":2}");
        var replayed = session.AppendWithOutcomes(operation, [JsonValues(equivalentDocument.RootElement, 1)]);
        Assert.Equal(WriteOutcomeStatus.Replayed, replayed.Status);
        Assert.Equal(first.Outcomes[0].GeneratedValues, replayed.Outcomes[0].GeneratedValues);

        var changed = Assert.Throws<AppendIdempotencyConflictException>(() =>
            session.AppendWithOutcomes(operation, [JsonValues("{\"a\":{\"x\":false},\"b\":2}", 1L)]));
        Assert.Equal(AppendIdempotencyConflictException.DiagnosticCode, changed.Message[..AppendIdempotencyConflictException.DiagnosticCode.Length]);

        var afterConflict = session.AppendWithOutcomes(
            new OperationId(DateTimeOffset.UtcNow, "json-operation-next"),
            [JsonValues("{\"a\":1}", 2L)]);
        Assert.Equal(2L, afterConflict.Outcomes[0].GeneratedValue<long>("sequence"));
    }

    [Fact]
    public void InMemory_exact_append_normalizes_numeric_json_lexemes_without_lossy_conversion()
    {
        using var connection = new InMemoryProviderFactory().Create("exact-append-json-number-" + Guid.NewGuid().ToString("N"));
        var unit = JsonAppendUnit("exact-append-json-number-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var operation = new OperationId(DateTimeOffset.UtcNow, "json-number-operation");

        var first = session.AppendWithOutcomes(operation, [JsonValues("{\"value\":1}", 1L)]);
        var replayed = session.AppendWithOutcomes(operation, [JsonValues("{\"value\":1.0e0}", 1L)]);

        Assert.Equal(WriteOutcomeStatus.Replayed, replayed.Status);
        Assert.Equal(first.Outcomes[0].GeneratedValue<long>("sequence"), replayed.Outcomes[0].GeneratedValue<long>("sequence"));
        Assert.Throws<AppendIdempotencyConflictException>(() => session.AppendWithOutcomes(
            operation,
            [JsonValues("{\"value\":1.0000000000000000000000000001}", 1L)]));

        var afterConflict = session.AppendWithOutcomes(
            new OperationId(DateTimeOffset.UtcNow, "json-number-operation-next"),
            [JsonValues("{\"value\":2e0}", 2L)]);
        Assert.Equal(2L, afterConflict.Outcomes[0].GeneratedValue<long>("sequence"));
    }

    [Fact]
    public void InMemory_exact_append_rejects_malformed_utf16_before_ledger_or_sequence_mutation()
    {
        using var connection = new InMemoryProviderFactory().Create("exact-append-utf16-" + Guid.NewGuid().ToString("N"));
        var unit = GeneratedUnit("exact-append-utf16-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var operation = new OperationId(DateTimeOffset.UtcNow, "malformed-utf16-operation");

        Assert.Throws<EncoderFallbackException>(() => session.AppendWithOutcomes(operation, [Values("\uD800", DateTimeOffset.UnixEpoch)]));

        var committed = session.AppendWithOutcomes(operation, [Values("\uFFFD", DateTimeOffset.UnixEpoch)]);
        Assert.Equal(WriteOutcomeStatus.Inserted, committed.Status);
        Assert.Equal(1L, committed.Outcomes[0].GeneratedValue<long>("sequence"));
        Assert.Equal(WriteOutcomeStatus.Replayed, session.AppendWithOutcomes(
            operation,
            [Values("\uFFFD", DateTimeOffset.UnixEpoch)]).Status);
    }

    [Fact]
    public void InMemory_exact_append_normalizes_equivalent_declared_decimal_scales_but_refuses_changed_value()
    {
        using var connection = new InMemoryProviderFactory().Create("exact-append-decimal-" + Guid.NewGuid().ToString("N"));
        var unit = DecimalUnit("exact-append-decimal-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var operation = new OperationId(DateTimeOffset.UtcNow, "decimal-operation");

        var first = session.AppendWithOutcomes(operation, [DecimalValues(1.0m)]);
        var replayed = session.AppendWithOutcomes(operation, [DecimalValues(1.00m)]);

        Assert.Equal(WriteOutcomeStatus.Replayed, replayed.Status);
        Assert.Equal(first.Outcomes[0].GeneratedValue<long>("sequence"), replayed.Outcomes[0].GeneratedValue<long>("sequence"));
        Assert.Throws<AppendIdempotencyConflictException>(() => session.AppendWithOutcomes(operation, [DecimalValues(1.01m)]));

        var zeroOperation = new OperationId(DateTimeOffset.UtcNow, "decimal-zero-operation");
        var zero = session.AppendWithOutcomes(zeroOperation, [DecimalValues(0m)]);
        var negativeZero = decimal.Parse("-0.00", CultureInfo.InvariantCulture);
        var zeroReplay = session.AppendWithOutcomes(zeroOperation, [DecimalValues(negativeZero)]);
        Assert.Equal(WriteOutcomeStatus.Replayed, zeroReplay.Status);
        Assert.Equal(zero.Outcomes[0].GeneratedValue<long>("sequence"), zeroReplay.Outcomes[0].GeneratedValue<long>("sequence"));

        var afterConflict = session.AppendWithOutcomes(
            new OperationId(DateTimeOffset.UtcNow, "decimal-operation-next"),
            [DecimalValues(2.00m)]);
        Assert.Equal(3L, afterConflict.Outcomes[0].GeneratedValue<long>("sequence"));
    }

    [Fact]
    public void InMemory_exact_append_uses_declared_numeric_type_and_encodes_nullable_values()
    {
        using var connection = new InMemoryProviderFactory().Create("exact-append-types-" + Guid.NewGuid().ToString("N"));
        var unit = JsonAppendUnit("exact-append-types-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var operation = new OperationId(DateTimeOffset.UtcNow, "numeric-operation");
        var first = session.AppendWithOutcomes(operation, [JsonValues("{\"value\":true}", 1L)]);

        // Int32 is accepted for a declared Int64, but a fractional Decimal must not
        // be converted/rounded before the fingerprint is compared.
        var replayed = session.AppendWithOutcomes(operation, [JsonValues("{\"value\":true}", 1)]);
        Assert.Equal(WriteOutcomeStatus.Replayed, replayed.Status);
        Assert.Equal(first.Outcomes[0].GeneratedValue<long>("sequence"), replayed.Outcomes[0].GeneratedValue<long>("sequence"));
        Assert.Throws<ArgumentException>(() => session.AppendWithOutcomes(
            new OperationId(DateTimeOffset.UtcNow, "numeric-invalid"),
            [JsonValues("{\"value\":true}", 1.1m)]));

        var afterInvalid = session.AppendWithOutcomes(
            new OperationId(DateTimeOffset.UtcNow, "numeric-operation-next"),
            [JsonValues("{\"value\":true}", 2L)]);
        Assert.Equal(2L, afterInvalid.Outcomes[0].GeneratedValue<long>("sequence"));
    }

    [Fact]
    public void InMemory_exact_append_replays_after_provider_connection_restart()
    {
        var factory = new InMemoryProviderFactory();
        var connectionString = "exact-append-restart-" + Guid.NewGuid().ToString("N");
        var unit = GeneratedUnit("exact-append-restart-" + Guid.NewGuid().ToString("N"));
        AppendOutcomeReport committed;
        using (var firstConnection = factory.Create(connectionString))
        {
            Assert.True(firstConnection.Schema.Apply(unit).Applied);
            committed = firstConnection.OpenSession(unit, StorageAccess.Global)
                .AppendWithOutcomes(new OperationId(DateTimeOffset.UtcNow, "restart-operation"), [Values("restart", DateTimeOffset.UnixEpoch)]);
        }

        using var secondConnection = factory.Create(connectionString);
        var replayed = secondConnection.OpenSession(unit, StorageAccess.Global)
            .AppendWithOutcomes(new OperationId(DateTimeOffset.UtcNow, "restart-operation"), [Values("restart", DateTimeOffset.UnixEpoch)]);
        Assert.Equal(WriteOutcomeStatus.Replayed, replayed.Status);
        Assert.Equal(committed.Outcomes[0].GeneratedValue<long>("sequence"), replayed.Outcomes[0].GeneratedValue<long>("sequence"));
    }

    [Fact]
    public void InMemory_exact_append_reclaims_expired_nonce_and_preserves_scoped_isolation()
    {
        using var connection = new InMemoryProviderFactory().Create("exact-append-window-" + Guid.NewGuid().ToString("N"));
        var unit = GeneratedUnit("exact-append-window-" + Guid.NewGuid().ToString("N"), ScopePolicy.Scoped, TimeSpan.FromMilliseconds(100));
        Assert.True(connection.Schema.Apply(unit).Applied);
        var scopeA = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("a")));
        var scopeB = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("b")));
        var operation = new OperationId(DateTimeOffset.UtcNow, "same-scope-aware-operation");

        Assert.Equal(WriteOutcomeStatus.Inserted, scopeA.AppendWithOutcomes(operation, [Values("a", DateTimeOffset.UnixEpoch)]).Status);
        var scopeBCommitted = scopeB.AppendWithOutcomes(operation, [Values("b", DateTimeOffset.UnixEpoch)]);
        Assert.Equal(WriteOutcomeStatus.Inserted, scopeBCommitted.Status);
        Assert.Equal(WriteOutcomeStatus.Replayed, scopeA.AppendWithOutcomes(operation, [Values("a", DateTimeOffset.UnixEpoch)]).Status);

        Thread.Sleep(150);
        var afterWindow = scopeA.AppendWithOutcomes(operation, [Values("a-after-window", DateTimeOffset.UnixEpoch)]);
        Assert.Equal(WriteOutcomeStatus.Inserted, afterWindow.Status);
        Assert.NotNull(scopeA.Read(new StorageKey(new Dictionary<string, object?> { ["sequence"] = afterWindow.Outcomes[0].GeneratedValue<long>("sequence") })));
        Assert.NotNull(scopeB.Read(new StorageKey(new Dictionary<string, object?> { ["sequence"] = scopeBCommitted.Outcomes[0].GeneratedValue<long>("sequence") })));
    }

    [Fact]
    public void InMemory_exact_append_is_atomic_across_multiple_units_in_one_unit_of_work()
    {
        using var connection = new InMemoryProviderFactory().Create("exact-append-uow-" + Guid.NewGuid().ToString("N"));
        var first = GeneratedUnit("exact-append-uow-first-" + Guid.NewGuid().ToString("N"));
        var second = GeneratedUnit("exact-append-uow-second-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(first).Applied);
        Assert.True(connection.Schema.Apply(second).Applied);

        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, first, second))
        {
            var firstSession = work.OpenSession(first);
            var secondSession = work.OpenSession(second);
            Assert.Equal(WriteOutcomeStatus.Inserted, firstSession.AppendWithOutcomes(new OperationId(DateTimeOffset.UtcNow, "uow-first"), [Values("first", DateTimeOffset.UnixEpoch)]).Status);
            Assert.Equal(WriteOutcomeStatus.Inserted, secondSession.AppendWithOutcomes(new OperationId(DateTimeOffset.UtcNow, "uow-second"), [Values("second", DateTimeOffset.UnixEpoch)]).Status);
            work.Commit();
        }

        Assert.NotNull(connection.OpenSession(first, StorageAccess.Global).Read(new StorageKey(new Dictionary<string, object?> { ["sequence"] = 1L })));
        Assert.NotNull(connection.OpenSession(second, StorageAccess.Global).Read(new StorageKey(new Dictionary<string, object?> { ["sequence"] = 1L })));
    }

    [Fact]
    public async Task InMemory_exact_append_concurrent_same_nonce_returns_one_insert_and_one_replay()
    {
        using var connection = new InMemoryProviderFactory().Create("exact-append-concurrent-" + Guid.NewGuid().ToString("N"));
        var unit = GeneratedUnit("exact-append-concurrent-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(unit).Applied);
        var operation = new OperationId(DateTimeOffset.UtcNow, "concurrent-operation");
        var barrier = new Barrier(2);

        var tasks = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            var session = connection.OpenSession(unit, StorageAccess.Global);
            barrier.SignalAndWait();
            return session.AppendWithOutcomes(operation, [Values("same-payload", DateTimeOffset.UnixEpoch)]);
        })).ToArray();

        var reports = await Task.WhenAll(tasks);
        Assert.Equal(1, reports.Count(report => report.Status == WriteOutcomeStatus.Inserted));
        Assert.Equal(1, reports.Count(report => report.Status == WriteOutcomeStatus.Replayed));
        Assert.All(reports, report => Assert.Equal(1L, report.Outcomes[0].GeneratedValue<long>("sequence")));
    }

    [Fact]
    public void InMemory_exact_append_failure_rolls_back_payload_and_ledger_for_all_units()
    {
        using var connection = new InMemoryProviderFactory().Create("exact-append-uow-rollback-" + Guid.NewGuid().ToString("N"));
        var first = GeneratedUnit("exact-append-uow-rollback-first-" + Guid.NewGuid().ToString("N"));
        var second = GeneratedUnit("exact-append-uow-rollback-second-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(first).Applied);
        Assert.True(connection.Schema.Apply(second).Applied);
        var operation = new OperationId(DateTimeOffset.UtcNow, "rollback-operation");

        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, first, second))
        {
            Assert.Equal(WriteOutcomeStatus.Inserted, work.OpenSession(first)
                .AppendWithOutcomes(operation, [Values("first", DateTimeOffset.UnixEpoch)]).Status);
            Assert.Throws<ArgumentException>(() => work.OpenSession(second)
                .AppendWithOutcomes(new OperationId(DateTimeOffset.UtcNow, "rollback-second"), [new StorageValues(new Dictionary<string, object?> { ["payload"] = "missing-required-column" })]));
        }

        Assert.Null(connection.OpenSession(first, StorageAccess.Global).Read(new StorageKey(new Dictionary<string, object?> { ["sequence"] = 1L })));
        var retry = connection.OpenSession(first, StorageAccess.Global)
            .AppendWithOutcomes(operation, [Values("first-after-rollback", DateTimeOffset.UnixEpoch)]);
        Assert.Equal(WriteOutcomeStatus.Inserted, retry.Status);
    }

    [Fact]
    public void SQLite_exact_append_refuses_replay_of_a_legacy_outcome_less_ledger_entry()
    {
        var path = Path.Combine(Path.GetTempPath(), "groundwork-exact-append-legacy-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var unit = GeneratedUnit("exact-append-legacy-" + Guid.NewGuid().ToString("N"));
            var operation = new OperationId(DateTimeOffset.UtcNow, "legacy-operation");
            using (var connection = new SqliteProviderFactory().Create($"Data Source={path}"))
            {
                Assert.True(connection.Schema.Apply(unit).Applied);
                using var native = new SqliteConnection($"Data Source={path}");
                native.Open();
                using var command = native.CreateCommand();
                command.CommandText = "CREATE TABLE \"__groundwork_operations\" (unit TEXT NOT NULL, scope TEXT NOT NULL, nonce TEXT NOT NULL, committed_at TEXT NOT NULL, PRIMARY KEY (unit, scope, nonce));";
                command.ExecuteNonQuery();
                command.CommandText = "INSERT INTO \"__groundwork_operations\" (unit, scope, nonce, committed_at) VALUES ($unit, '', $nonce, $committed);";
                command.Parameters.AddWithValue("$unit", unit.Id.Value);
                command.Parameters.AddWithValue("$nonce", operation.Nonce);
                command.Parameters.AddWithValue("$committed", DateTimeOffset.UtcNow.ToString("O"));
                command.ExecuteNonQuery();

                var session = connection.OpenSession(unit, StorageAccess.Global);
                Assert.Equal(WriteOutcomeStatus.Replayed, session.Append(operation, [Values("legacy", DateTimeOffset.UnixEpoch)]).Status);
                var refusal = Assert.Throws<InvalidOperationException>(() => session.AppendWithOutcomes(operation, [Values("legacy", DateTimeOffset.UnixEpoch)]));
                Assert.Contains("GW-APPEND-002", refusal.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static void AssertExactAppend(IStorageProviderConnection connection, string provider)
    {
        var unit = GeneratedUnit("exact-append-" + provider + "-" + Guid.NewGuid().ToString("N"));
        Assert.True(connection.Schema.Apply(unit).Applied);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        var operation = new OperationId(DateTimeOffset.UtcNow, "exact-operation");
        var instant = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var committed = session.AppendWithOutcomes(operation, [
            Values("first", instant),
            Values("second", instant.AddSeconds(1))
        ]);

        Assert.Equal(WriteOutcomeStatus.Inserted, committed.Status);
        Assert.Equal(2, committed.Outcomes.Count);
        Assert.Equal(new[] { 1L, 2L }, committed.Outcomes.Select(outcome => outcome.GeneratedValue<long>("sequence")).ToArray());
        Assert.Equal("first", session.Read(new StorageKey(new Dictionary<string, object?> { ["sequence"] = 1L }))!.Values.Values["payload"]);
        Assert.Equal("second", session.Read(new StorageKey(new Dictionary<string, object?> { ["sequence"] = 2L }))!.Values.Values["payload"]);

        var replayed = session.AppendWithOutcomes(operation, [
            Values("first", instant),
            Values("second", instant.AddSeconds(1))
        ]);

        Assert.Equal(WriteOutcomeStatus.Replayed, replayed.Status);
        Assert.Equal(committed.Outcomes.Select(outcome => outcome.GeneratedValues), replayed.Outcomes.Select(outcome => outcome.GeneratedValues));

        Assert.Throws<AppendIdempotencyConflictException>(() => session.AppendWithOutcomes(operation, [Values("changed", instant)]));
        var afterConflict = session.AppendWithOutcomes(
            new OperationId(DateTimeOffset.UtcNow, "exact-operation-next"),
            [Values("third", instant)]);
        Assert.Equal(3L, afterConflict.Outcomes[0].GeneratedValue<long>("sequence"));
        Assert.Equal("third", session.Read(new StorageKey(new Dictionary<string, object?> { ["sequence"] = 3L }))!.Values.Values["payload"]);
    }

    private static void AssertExactAppendUnitOfWork(IStorageProviderConnection connection, string provider)
    {
        var first = GeneratedUnit(UowUnitName(provider, "f1"));
        var second = GeneratedUnit(UowUnitName(provider, "f2"));
        Assert.True(connection.Schema.Apply(first).Applied);
        Assert.True(connection.Schema.Apply(second).Applied);

        var firstOperation = new OperationId(DateTimeOffset.UtcNow, "uow-" + provider + "-first");
        var secondOperation = new OperationId(DateTimeOffset.UtcNow, "uow-" + provider + "-second");
        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, first, second))
        {
            Assert.Equal(WriteOutcomeStatus.Inserted, work.OpenSession(first)
                .AppendWithOutcomes(firstOperation, [Values("first", DateTimeOffset.UnixEpoch)]).Status);
            Assert.Equal(WriteOutcomeStatus.Inserted, work.OpenSession(second)
                .AppendWithOutcomes(secondOperation, [Values("second", DateTimeOffset.UnixEpoch)]).Status);
            work.Commit();
        }

        Assert.NotNull(connection.OpenSession(first, StorageAccess.Global)
            .Read(new StorageKey(new Dictionary<string, object?> { ["sequence"] = 1L })));
        Assert.NotNull(connection.OpenSession(second, StorageAccess.Global)
            .Read(new StorageKey(new Dictionary<string, object?> { ["sequence"] = 1L })));

        var rollbackFirst = GeneratedUnit(UowUnitName(provider, "r1"));
        var rollbackSecond = GeneratedUnit(UowUnitName(provider, "r2"));
        Assert.True(connection.Schema.Apply(rollbackFirst).Applied);
        Assert.True(connection.Schema.Apply(rollbackSecond).Applied);
        var rollbackOperation = new OperationId(DateTimeOffset.UtcNow, "uow-" + provider + "-rollback");

        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, rollbackFirst, rollbackSecond))
        {
            Assert.Equal(WriteOutcomeStatus.Inserted, work.OpenSession(rollbackFirst)
                .AppendWithOutcomes(rollbackOperation, [Values("rolled-back", DateTimeOffset.UnixEpoch)]).Status);
            Assert.ThrowsAny<Exception>(() => work.OpenSession(rollbackSecond)
                .AppendWithOutcomes(
                    new OperationId(DateTimeOffset.UtcNow, "uow-" + provider + "-rollback-failure"),
                    [new StorageValues(new Dictionary<string, object?> { ["payload"] = "missing-occurred-at" })]));
        }

        Assert.Null(connection.OpenSession(rollbackFirst, StorageAccess.Global)
            .Read(new StorageKey(new Dictionary<string, object?> { ["sequence"] = 1L })));
        var retry = connection.OpenSession(rollbackFirst, StorageAccess.Global)
            .AppendWithOutcomes(rollbackOperation, [Values("after-rollback", DateTimeOffset.UnixEpoch)]);
        Assert.Equal(WriteOutcomeStatus.Inserted, retry.Status);
        Assert.True(retry.Outcomes[0].GeneratedValue<long>("sequence") > 0);
    }

    private static string UowUnitName(string provider, string role) =>
        $"s6u-{provider}-{role}-{Guid.NewGuid():N}";

    private static StorageValues Values(string payload, DateTimeOffset occurredAt) => new(new Dictionary<string, object?>
    {
        ["payload"] = payload,
        ["occurredAt"] = occurredAt
    });

    private static StorageValues JsonValues(object json, object number) => new(new Dictionary<string, object?>
    {
        ["body"] = json,
        ["number"] = number,
        ["optional"] = null
    });

    private static StorageValues DecimalValues(decimal amount) => new(new Dictionary<string, object?>
    {
        ["amount"] = amount
    });

    private static StorageUnit JsonAppendUnit(string name) => new()
    {
        Id = new StorageUnitId(name),
        Name = PhysicalName(name),
        Columns =
        [
            new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
            new() { Name = "body", Type = PortableType.Json, IsNullable = false },
            new() { Name = "number", Type = PortableType.Int64, IsNullable = false },
            new() { Name = "optional", Type = PortableType.String, IsNullable = true }
        ],
        Key = new KeyDefinition { Columns = ["sequence"] },
        AppendIdempotency = new AppendIdempotencyDeclaration { Window = TimeSpan.FromMinutes(10) }
    };

    private static StorageUnit DecimalUnit(string name) => new()
    {
        Id = new StorageUnitId(name),
        Name = PhysicalName(name),
        Columns =
        [
            new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
            new() { Name = "amount", Type = PortableType.Decimal, IsNullable = false, Precision = 18, Scale = 4 }
        ],
        Key = new KeyDefinition { Columns = ["sequence"] },
        AppendIdempotency = new AppendIdempotencyDeclaration { Window = TimeSpan.FromMinutes(10) }
    };

    private static StorageUnit GeneratedUnit(
        string name,
        ScopePolicy scope = ScopePolicy.Global,
        TimeSpan? window = null) => new()
    {
        Id = new StorageUnitId(name),
        Name = PhysicalName(name),
        Columns =
        [
            new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
            new() { Name = "payload", Type = PortableType.String, MaxLength = 450, IsNullable = false },
            new() { Name = "occurredAt", Type = PortableType.DateTimeOffset, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["sequence"] },
        Scope = scope,
        AppendIdempotency = new AppendIdempotencyDeclaration { Window = window ?? TimeSpan.FromMinutes(10) }
    };

    private static StorageUnit NonSequenceUnit(string name) => new()
    {
        Id = new StorageUnitId(name),
        Name = PhysicalName(name),
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 200 },
            new() { Name = "payload", Type = PortableType.String, IsNullable = false, MaxLength = 200 }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        AppendIdempotency = new AppendIdempotencyDeclaration { Window = TimeSpan.FromMinutes(10) }
    };

    private static string PhysicalName(string name)
    {
        var normalized = name.Replace('-', '_');
        return normalized.Length <= PortabilityValidator.MaximumPortableIdentifierLength
            ? normalized
            : normalized[..30] + "_" + normalized[^32..];
    }

    private sealed class CapabilityHidingSession(IStorageSession inner) : IStorageSession
    {
        public StorageUnit Unit => inner.Unit;
        public StorageAccess Access => inner.Access;
        public StoredEntry? Read(StorageKey key) => inner.Read(key);
        public ValueTask<StoredEntry?> ReadAsync(StorageKey key, CancellationToken cancellationToken = default) => inner.ReadAsync(key, cancellationToken);
        public QueryMaterializedResult Query(QueryRequest request, QueryRenderOptions? options = null) => inner.Query(request, options);
        public ValueTask<QueryMaterializedResult> QueryAsync(QueryRequest request, QueryRenderOptions? options = null, CancellationToken cancellationToken = default) => inner.QueryAsync(request, options, cancellationToken);
        public AggregationResult Aggregate(AggregationQuery query) => inner.Aggregate(query);
        public ValueTask<AggregationResult> AggregateAsync(AggregationQuery query, CancellationToken cancellationToken = default) => inner.AggregateAsync(query, cancellationToken);
        public WriteOutcome Insert(StorageValues values, WriteOptions? options = null) => inner.Insert(values, options);
        public ValueTask<WriteOutcome> InsertAsync(StorageValues values, WriteOptions? options = null, CancellationToken cancellationToken = default) => inner.InsertAsync(values, options, cancellationToken);
        public WriteOutcome Update(StorageValues values, WriteOptions? options = null) => inner.Update(values, options);
        public ValueTask<WriteOutcome> UpdateAsync(StorageValues values, WriteOptions? options = null, CancellationToken cancellationToken = default) => inner.UpdateAsync(values, options, cancellationToken);
        public WriteOutcome Upsert(StorageValues values, WriteOptions? options = null) => inner.Upsert(values, options);
        public ValueTask<WriteOutcome> UpsertAsync(StorageValues values, WriteOptions? options = null, CancellationToken cancellationToken = default) => inner.UpsertAsync(values, options, cancellationToken);
        public WriteOutcome Delete(StorageKey key, WriteOptions? options = null) => inner.Delete(key, options);
        public ValueTask<WriteOutcome> DeleteAsync(StorageKey key, WriteOptions? options = null, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, options, cancellationToken);
        public WriteOutcome Append(OperationId operationId, IReadOnlyList<StorageValues> values) => inner.Append(operationId, values);
        public ValueTask<WriteOutcome> AppendAsync(OperationId operationId, IReadOnlyList<StorageValues> values, CancellationToken cancellationToken = default) => inner.AppendAsync(operationId, values, cancellationToken);
    }
}
