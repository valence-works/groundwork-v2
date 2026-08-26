using Groundwork.Kernel;
using Groundwork.Query.Model;
using Groundwork.MongoDb;
using Groundwork.PostgreSql;
using Groundwork.Sqlite;
using Groundwork.SqlServer;
using Groundwork.Testing;
using Groundwork.Store;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.ProviderCommands.Tests;

/// <summary>
/// Proofs over the session-scoped provider-command observer (#63): every command a session issues —
/// reads, writes and probes alike — raises exactly one <see cref="ProviderCommandEvent"/> on the observer
/// the session was opened with. The write-path shape proofs from the retired Groundwork.WritePath.Tests
/// suite are preserved here unchanged in what they assert; what changed is where the observer attaches.
///
/// Structural rule the tests encode: an assertion about ONE call observes a session opened for that call,
/// because the observer counts everything its session does. Seeding happens on unobserved sessions.
/// </summary>
public sealed class ProviderCommandTests
{
    // ---------------------------------------------------------------------------------------------
    // Read-path proofs — the reason this suite replaced the write-path one.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SQLite_read_and_query_raise_single_read_events_without_probes()
    {
        using var store = TemporarySqliteStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Unit("sqlite-read-events");
        connection.Schema.Apply(unit);
        connection.OpenSession(unit, StorageAccess.Global)
            .Insert(Values("one", "first", DateTimeOffset.UnixEpoch));

        var observer = new ProviderCommandObserver();
        var session = connection.OpenSession(unit, StorageAccess.Global, observer);

        var read = session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "one" }));
        Assert.NotNull(read);
        var readEvent = Assert.Single(observer.Commands);
        Assert.Equal("sqlite.read", readEvent.Operation);
        Assert.Equal(ProviderCommandKind.Read, readEvent.Kind);
        Assert.False(readEvent.IsProbe);
        Assert.Contains("SELECT", readEvent.CommandText, StringComparison.OrdinalIgnoreCase);

        var result = session.Query(Page(unit));
        Assert.Single(result.Rows);
        Assert.Equal(2, observer.RoundTrips);
        var queryEvent = observer.Commands[^1];
        Assert.Equal("sqlite.query", queryEvent.Operation);
        Assert.Equal(ProviderCommandKind.Read, queryEvent.Kind);
        Assert.False(queryEvent.IsProbe);
        Assert.Contains("SELECT", queryEvent.CommandText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InMemory_reference_provider_raises_the_same_read_events()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://read-events");
        var unit = Unit("memory-read-events");
        connection.Schema.Apply(unit);
        connection.OpenSession(unit, StorageAccess.Global)
            .Insert(Values("one", "first", DateTimeOffset.UnixEpoch));

        var observer = new ProviderCommandObserver();
        var session = connection.OpenSession(unit, StorageAccess.Global, observer);
        Assert.NotNull(session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "one" })));
        _ = session.Query(Page(unit));

        Assert.Equal(2, observer.RoundTrips);
        Assert.Equal(["in-memory.read", "in-memory.query"], observer.Commands.Select(command => command.Operation));
        Assert.All(observer.Commands, command => Assert.Equal(ProviderCommandKind.Read, command.Kind));
        Assert.DoesNotContain(observer.Commands, command => command.IsProbe);
    }

    [SkippableFact]
    public void PostgreSQL_read_and_query_raise_single_read_events()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL provider-command tests.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertReadEvents(connection, "postgresql");
    }

    [SkippableFact]
    public void SQLServer_read_and_query_raise_single_read_events()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run SQL Server provider-command tests.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertReadEvents(connection, "sqlserver");
    }

    [SkippableFact]
    public void MongoDB_read_and_query_raise_single_read_events()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB provider-command tests.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        AssertReadEvents(connection, "mongodb");
    }

    [Fact]
    public void InMemory_ordinary_writes_each_raise_one_write_event()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://ordinary-writes");
        AssertOrdinaryWritesObserved(connection, "in-memory");
    }

    [Fact]
    public void SQLite_ordinary_writes_each_raise_one_write_event()
    {
        using var store = TemporarySqliteStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        AssertOrdinaryWritesObserved(connection, "sqlite");
    }

    [SkippableFact]
    public void PostgreSQL_ordinary_writes_each_raise_one_write_event()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL provider-command tests.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertOrdinaryWritesObserved(connection, "postgresql");
    }

    [SkippableFact]
    public void SQLServer_ordinary_writes_each_raise_one_write_event()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run SQL Server provider-command tests.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertOrdinaryWritesObserved(connection, "sqlserver");
    }

    [SkippableFact]
    public void MongoDB_ordinary_writes_each_raise_one_write_event()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB provider-command tests.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        AssertOrdinaryWritesObserved(connection, "mongodb");
    }

    [Fact]
    public void Rejected_operations_count_no_commands()
    {
        using var connection = new InMemoryProviderFactory().Create("memory://rejected-ops");
        var unit = Unit("memory-rejected");
        connection.Schema.Apply(unit);

        var observer = new ProviderCommandObserver();
        var session = connection.OpenSession(unit, StorageAccess.Global, observer);

        // A query for a table this session does not own is refused before any provider work.
        Assert.Throws<ArgumentException>(() => session.Query(new QueryRequest(
            new TableId("someone_else"),
            Predicate.AlwaysTrue.Instance,
            [],
            Projection.All,
            Paging.Keyset(10),
            ResultShape.Rows.Instance)));
        // An aggregate naming an undeclared profile is refused before any provider work.
        Assert.Throws<AggregationValidationException>(() => session.Aggregate(new AggregationQuery("no-such-profile")));

        Assert.Equal(0, observer.RoundTrips);
        Assert.Empty(observer.Commands);
    }

    [Fact]
    public void One_session_records_reads_and_writes_in_order_and_OfKind_separates_them()
    {
        using var store = TemporarySqliteStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Unit("sqlite-interleaved");
        connection.Schema.Apply(unit);

        var observer = new ProviderCommandObserver();
        var session = connection.OpenSession(unit, StorageAccess.Global, observer);
        var conditional = session.Conditional();

        Assert.Equal(WriteOutcomeStatus.Inserted,
            conditional.ConditionalUpsert(Values("one", "first", DateTimeOffset.UnixEpoch)).Status);
        Assert.NotNull(session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "one" })));
        Assert.Equal(WriteOutcomeStatus.Updated,
            conditional.ConditionalUpsert(
                Values("one", "second", DateTimeOffset.UnixEpoch),
                new WriteOptions { Precondition = WritePrecondition.IfVersion(1) }).Status);
        _ = session.Query(Page(unit));

        Assert.Equal(4, observer.RoundTrips);
        Assert.Equal(
            [ProviderCommandKind.Write, ProviderCommandKind.Read, ProviderCommandKind.Write, ProviderCommandKind.Read],
            observer.Commands.Select(command => command.Kind));
        Assert.Equal(2, observer.OfKind(ProviderCommandKind.Write).Count);
        Assert.Equal(2, observer.OfKind(ProviderCommandKind.Read).Count);
        Assert.DoesNotContain(observer.Commands, command => command.IsProbe);
    }

    [Fact]
    public void SQLite_unit_of_work_observer_counts_the_batched_statement()
    {
        using var store = TemporarySqliteStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Unit("sqlite-uow-observer");
        connection.Schema.Apply(unit);

        var observer = new ProviderCommandObserver();
        using (var work = connection.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Exact, observer, unit))
        {
            for (var index = 0; index < 8; index++)
                work.Stage(RowWrite.Upsert(unit, Values($"row-{index}", "value", DateTimeOffset.UnixEpoch)));
            var report = work.CommitWithOutcomes();
            Assert.Equal(8, report.Outcomes.Count);
        }

        Assert.True(observer.RoundTrips >= 1);
        Assert.Contains(observer.Commands, command => command.Operation == "sqlite.batch-upsert");
        Assert.All(observer.OfKind(ProviderCommandKind.Write), command => Assert.False(command.IsProbe));
    }

    // ---------------------------------------------------------------------------------------------
    // Write-path shape proofs, carried over from the retired suite.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void SQLite_conditional_upsert_uses_one_command_and_preserves_created_at()
    {
        using var store = TemporarySqliteStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        AssertOneRoundTripAndCreatedAt(connection, "sqlite");
    }

    [SkippableFact]
    public void PostgreSQL_conditional_upsert_uses_one_command_and_preserves_created_at()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL provider-command tests.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertOneRoundTripAndCreatedAt(connection, "postgresql");
    }

    [SkippableFact]
    public void SQLServer_conditional_upsert_uses_one_batch_and_preserves_created_at()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run SQL Server provider-command tests.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertOneRoundTripAndCreatedAt(connection, "sqlserver");
    }

    [SkippableFact]
    public void MongoDB_conditional_upsert_uses_one_native_update_and_preserves_created_at()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB provider-command tests.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        AssertOneRoundTripAndCreatedAt(connection, "mongodb");
    }

    [Fact]
    public void SQLite_conflict_detail_is_lazy_and_cached_and_its_probe_is_a_read()
    {
        using var store = TemporarySqliteStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Unit("sqlite-lazy");
        connection.Schema.Apply(unit);
        connection.OpenSession(unit, StorageAccess.Global).Conditional()
            .ConditionalUpsert(Values("one", "first", DateTimeOffset.Parse("2026-01-01T00:00:00Z")));

        var observer = new ProviderCommandObserver();
        var outcome = connection.OpenSession(unit, StorageAccess.Global, observer).Conditional().ConditionalUpsert(
            Values("one", "stale", DateTimeOffset.Parse("2026-01-02T00:00:00Z")),
            new WriteOptions { Precondition = WritePrecondition.IfVersion(99) });

        Assert.Equal(WriteOutcomeStatus.ConcurrencyConflict, outcome.Status);
        Assert.Equal(1, observer.RoundTrips);
        Assert.Equal(WriteOutcomeStatus.ConcurrencyConflict, outcome.Detail.Status);
        Assert.Equal(2, observer.RoundTrips);
        _ = outcome.Detail;
        Assert.Equal(2, observer.RoundTrips);

        // The lazy probe is a SELECT: a read-kind probe, distinguishable from the write it explains.
        var probe = Assert.Single(observer.Commands, command => command.IsProbe);
        Assert.Equal(ProviderCommandKind.Read, probe.Kind);
    }

    [Fact]
    public void SQLite_non_versioned_conditional_upsert_reports_insert_and_update_without_a_read()
    {
        using var store = TemporarySqliteStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Unit("sqlite-none", ConcurrencyDeclaration.None);
        connection.Schema.Apply(unit);

        var firstObserver = new ProviderCommandObserver();
        var first = connection.OpenSession(unit, StorageAccess.Global, firstObserver).Conditional()
            .ConditionalUpsert(Values("one", "first", DateTimeOffset.UnixEpoch));
        var secondObserver = new ProviderCommandObserver();
        var second = connection.OpenSession(unit, StorageAccess.Global, secondObserver).Conditional()
            .ConditionalUpsert(Values("one", "second", DateTimeOffset.UnixEpoch));

        Assert.Equal(WriteOutcomeStatus.Inserted, first.Status);
        Assert.Equal(WriteOutcomeStatus.Updated, second.Status);
        Assert.Equal(1, firstObserver.RoundTrips);
        Assert.Equal(1, secondObserver.RoundTrips);
        Assert.DoesNotContain(firstObserver.Commands, command => command.IsProbe);
        Assert.DoesNotContain(secondObserver.Commands, command => command.IsProbe);
        Assert.DoesNotContain(firstObserver.Commands, command =>
            command.CommandText?.Contains("SELECT", StringComparison.OrdinalIgnoreCase) == true);
        var stored = connection.OpenSession(unit, StorageAccess.Global)
            .Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "one" }));
        Assert.NotNull(stored);
        Assert.DoesNotContain("__groundwork_action", stored!.Values.Values.Keys);
    }

    [Fact]
    public void SQLite_none_key_only_update_uses_one_native_statement_and_reports_affected_rows()
    {
        using var store = TemporarySqliteStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Unit("sqlite-none-key-only-update", ConcurrencyDeclaration.None);
        connection.Schema.Apply(unit);
        connection.OpenSession(unit, StorageAccess.Global).Insert(Values("existing", "first", DateTimeOffset.UnixEpoch));

        var existingObserver = new ProviderCommandObserver();
        var existing = connection.OpenSession(unit, StorageAccess.Global, existingObserver)
            .Update(KeyOnlyValues("existing"));
        var missingObserver = new ProviderCommandObserver();
        var missing = connection.OpenSession(unit, StorageAccess.Global, missingObserver)
            .Update(KeyOnlyValues("missing"));

        Assert.Equal(WriteOutcomeStatus.Updated, existing.Status);
        Assert.Equal(WriteOutcomeStatus.NotFound, missing.Status);
        AssertSingleUpdateWithoutProbe(existingObserver);
        AssertSingleUpdateWithoutProbe(missingObserver);
    }

    [Fact]
    public void SQLite_none_update_of_missing_row_reports_not_found()
    {
        using var store = TemporarySqliteStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Unit("sqlite-none-missing-update", ConcurrencyDeclaration.None);
        connection.Schema.Apply(unit);
        var observer = new ProviderCommandObserver();

        var outcome = connection.OpenSession(unit, StorageAccess.Global, observer).Update(
            Values("missing", "value", DateTimeOffset.UnixEpoch));

        Assert.Equal(WriteOutcomeStatus.NotFound, outcome.Status);
        AssertSingleUpdateWithoutProbe(observer);
    }

    [Fact]
    public void SQLite_none_provider_writes_use_one_native_statement_and_upsert_overwrites()
    {
        using var store = TemporarySqliteStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        AssertNoneProviderWrites(connection, "sqlite");
    }

    [SkippableFact]
    public void PostgreSQL_none_provider_writes_use_one_native_statement_and_report_not_found()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL provider-command tests.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertNoneProviderWrites(connection, "postgresql");
    }

    [SkippableFact]
    public void SQLServer_none_provider_writes_use_one_native_statement_and_upsert_overwrites()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run SQL Server provider-command tests.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertNoneProviderWrites(connection, "sqlserver");
    }

    [Fact]
    public void Concurrency_declaration_and_preconditions_are_explicit_and_system_owned()
    {
        var optimistic = ConcurrencyDeclaration.Optimistic("revision");

        Assert.Equal(ConcurrencyKind.Optimistic, optimistic.Kind);
        Assert.Equal("revision", optimistic.TokenColumn);
        Assert.Equal(WritePreconditionKind.Unconditional, WriteOptions.Unconditional.Precondition.Kind);
        Assert.Equal(WritePreconditionKind.CreateOnly, WriteOptions.CreateOnly.Precondition.Kind);
        Assert.Equal(42, WriteOptions.IfVersion(42).Precondition.Version);
    }

    [Fact]
    public void Explicit_token_column_is_provider_owned_and_not_required_from_application_values()
    {
        var baseDeclaration = Unit("explicit-token", ConcurrencyDeclaration.Optimistic("revision"));
        var declaration = baseDeclaration with
        {
            Columns =
            [
                ..baseDeclaration.Columns,
                new ColumnDefinition
                {
                    Name = "revision",
                    Type = PortableType.Int64,
                    IsNullable = false,
                    Default = new PortableDefault(0L)
                }
            ]
        };

        using var connection = new InMemoryProviderFactory().Create("memory://explicit-token");
        connection.Schema.Apply(declaration);
        var session = connection.OpenSession(declaration, StorageAccess.Global);

        var inserted = session.Insert(Values("one", "value", DateTimeOffset.UnixEpoch));
        Assert.Equal(WriteOutcomeStatus.Inserted, inserted.Status);
        Assert.Equal(1, inserted.Version);
        Assert.DoesNotContain("revision", session.Read(new StorageKey(
            new Dictionary<string, object?> { ["id"] = "one" }))!.Values.Values.Keys);

        var exception = Assert.Throws<InvalidOperationException>(() => session.Insert(
            new StorageValues(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = "two",
                ["value"] = "value",
                ["createdAt"] = DateTimeOffset.UnixEpoch,
                ["revision"] = 1L
            })));
        Assert.Contains("GW-WRITE-CONCURRENCY-003", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SQLite_none_schema_has_no_version_column_and_protected_writes_are_refused_before_io()
    {
        using var store = TemporarySqliteStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Unit("sqlite-explicit-none", ConcurrencyDeclaration.None);
        connection.Schema.Apply(unit);

        using (var catalog = new SqliteConnection(store.ConnectionString))
        {
            catalog.Open();
            using var command = catalog.CreateCommand();
            command.CommandText = $"PRAGMA table_info([{unit.Name}]);";
            using var reader = command.ExecuteReader();
            var columns = new List<string>();
            while (reader.Read()) columns.Add(reader.GetString(1));
            Assert.DoesNotContain("__groundwork_version", columns);
        }

        var observer = new ProviderCommandObserver();
        var session = connection.OpenSession(unit, StorageAccess.Global, observer);
        var exception = Assert.Throws<InvalidOperationException>(() => session.Insert(
            Values("one", "value", DateTimeOffset.UnixEpoch),
            new WriteOptions { Precondition = WritePrecondition.CreateOnly }));

        Assert.Contains("GW-WRITE-CONCURRENCY-001", exception.Message, StringComparison.Ordinal);
        Assert.Empty(observer.Commands);
        Assert.Throws<InvalidOperationException>(() => RowWrite.Update(
            unit,
            Values("one", "value", DateTimeOffset.UnixEpoch),
            WriteOptions.CreateOnly));

        var invalid = unit with
        {
            Id = new StorageUnitId("invalid-" + Guid.NewGuid().ToString("N")),
            Name = "invalid_" + Guid.NewGuid().ToString("N"),
            Concurrency = new ConcurrencyDeclaration
            {
                Kind = ConcurrencyKind.None,
                TokenColumn = "revision"
            }
        };
        Assert.Throws<ArgumentException>(() => connection.Schema.Apply(invalid));
        using var checkInvalid = new SqliteConnection(store.ConnectionString);
        checkInvalid.Open();
        using var checkCommand = checkInvalid.CreateCommand();
        checkCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        checkCommand.Parameters.AddWithValue("$name", invalid.Name);
        Assert.Equal(0L, checkCommand.ExecuteScalar());
    }

    [Fact]
    public void SQLite_optimistic_schema_synthesizes_zero_default_and_returns_first_version_one()
    {
        using var store = TemporarySqliteStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Unit("sqlite-synthesized-optimistic");
        connection.Schema.Apply(unit);

        using (var catalog = new SqliteConnection(store.ConnectionString))
        {
            catalog.Open();
            using var command = catalog.CreateCommand();
            command.CommandText = $"PRAGMA table_info([{unit.Name}]);";
            using var reader = command.ExecuteReader();
            var version = false;
            while (reader.Read())
            {
                if (reader.GetString(1) != "__groundwork_version") continue;
                version = true;
                Assert.Equal("0", reader.IsDBNull(4) ? null : reader.GetString(4));
            }
            Assert.True(version);
        }

        var result = connection.OpenSession(unit, StorageAccess.Global)
            .Conditional()
            .ConditionalUpsert(Values("one", "value", DateTimeOffset.UnixEpoch));
        Assert.Equal(WriteOutcomeStatus.Inserted, result.Status);
        Assert.Equal(1, result.Version);
    }

    [SkippableFact]
    public void PostgreSQL_partial_unique_violation_names_the_index_without_a_probe()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL provider-command tests.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertUniqueViolationWithoutProbe(connection, "postgresql-partial");
    }

    [SkippableFact]
    public void PostgreSQL_partial_key_conflict_target_is_inferred_and_executes()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL provider-command tests.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        var unit = Unit("postgresql-partial-key", ConcurrencyDeclaration.Optimistic(), includePartialKeyIndex: true);
        connection.Schema.Apply(unit);
        connection.OpenSession(unit, StorageAccess.Global).Conditional()
            .ConditionalUpsert(Values("one", "first", DateTimeOffset.UnixEpoch));

        var observer = new ProviderCommandObserver();
        var result = connection.OpenSession(unit, StorageAccess.Global, observer).Conditional().ConditionalUpsert(
            Values("one", "second", DateTimeOffset.UnixEpoch.AddDays(1)),
            new WriteOptions { Precondition = WritePrecondition.IfVersion(1) });

        Assert.Equal(WriteOutcomeStatus.Updated, result.Status);
        Assert.Equal(2, result.Version);
        Assert.Equal(1, observer.RoundTrips);
        Assert.DoesNotContain(observer.Commands, command => command.IsProbe);
        Assert.Contains("ON CONFLICT", observer.Commands.Single().CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHERE", observer.Commands.Single().CommandText, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public void PostgreSQL_scoped_conditional_upsert_does_not_duplicate_scope_parameters()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL provider-command tests.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        var unit = Unit("postgresql-scoped", ConcurrencyDeclaration.Optimistic(), scope: ScopePolicy.Scoped);
        connection.Schema.Apply(unit);
        var observer = new ProviderCommandObserver();
        var result = connection
            .OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-a")), observer)
            .Conditional()
            .ConditionalUpsert(Values("one", "first", DateTimeOffset.UnixEpoch));

        Assert.Equal(WriteOutcomeStatus.Inserted, result.Status);
        Assert.Equal(1, result.Version);
        Assert.Equal(1, observer.RoundTrips);
        Assert.DoesNotContain(observer.Commands, command => command.IsProbe);
    }

    [Fact]
    public void SQLite_missing_conflict_detail_is_lazy_and_cached()
    {
        using var store = TemporarySqliteStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Unit("sqlite-missing");
        connection.Schema.Apply(unit);
        var observer = new ProviderCommandObserver();
        var outcome = connection.OpenSession(unit, StorageAccess.Global, observer).Conditional().ConditionalUpsert(
            Values("missing", "value", DateTimeOffset.UnixEpoch),
            new WriteOptions { Precondition = WritePrecondition.IfVersion(99) });

        Assert.Equal(WriteOutcomeStatus.ConcurrencyConflict, outcome.Status);
        Assert.Equal(1, observer.RoundTrips);
        Assert.Equal(WriteOutcomeStatus.NotFound, outcome.Detail.Status);
        Assert.Equal(2, observer.RoundTrips);
        _ = outcome.Detail;
        Assert.Equal(2, observer.RoundTrips);
    }

    [Fact]
    public void SQLite_unique_violation_names_the_conflicting_constraint_without_a_probe()
    {
        using var store = TemporarySqliteStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        AssertUniqueViolationWithoutProbe(connection, "sqlite-unique");
    }

    [SkippableFact]
    public void MongoDB_unique_violation_is_not_misclassified_as_a_cas_conflict()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB provider-command tests.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        AssertUniqueViolationWithoutProbe(connection, "mongodb-unique");
    }

    [SkippableFact]
    public void SQLServer_unique_violation_names_the_index_without_a_probe()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run SQL Server provider-command tests.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertUniqueViolationWithoutProbe(connection, "sqlserver-unique");
    }

    [SkippableFact]
    public void MongoDB_provider_sequence_conditional_upsert_refuses_before_any_command()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB provider-command tests.");
        using var connection = new MongoProviderFactory().Create(connectionString!);
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("mongodb-provider-sequence-" + Guid.NewGuid().ToString("N")),
            Name = "mongodb_provider_sequence_" + Guid.NewGuid().ToString("N"),
            Columns =
            [
                new() { Name = "sequence", Type = PortableType.Int64, IsNullable = false, Generation = ColumnGeneration.ProviderSequence },
                new() { Name = "payload", Type = PortableType.String }
            ],
            Key = new KeyDefinition { Columns = ["sequence"] }
        };

        connection.Schema.Apply(unit);
        var observer = new ProviderCommandObserver();
        var session = connection.OpenSession(unit, StorageAccess.Global, observer).Conditional();

        var exception = Assert.Throws<NotSupportedException>(() => session.ConditionalUpsert(
            new StorageValues(new Dictionary<string, object?> { ["payload"] = "first" })));

        Assert.Contains("ProviderSequence", exception.Message, StringComparison.Ordinal);
        Assert.Contains("one-command", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, observer.RoundTrips);
        Assert.Empty(observer.Commands);
    }

    // ---------------------------------------------------------------------------------------------
    // Shared assertion bodies.
    // ---------------------------------------------------------------------------------------------

    private static void AssertReadEvents(IStorageProviderConnection connection, string provider)
    {
        var unit = Unit(provider + "-read-events");
        connection.Schema.Apply(unit);
        connection.OpenSession(unit, StorageAccess.Global)
            .Insert(Values("one", "first", DateTimeOffset.UnixEpoch));

        var observer = new ProviderCommandObserver();
        var session = connection.OpenSession(unit, StorageAccess.Global, observer);
        Assert.NotNull(session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "one" })));
        _ = session.Query(Page(unit));

        Assert.Equal(2, observer.RoundTrips);
        Assert.Equal([provider + ".read", provider + ".query"], observer.Commands.Select(command => command.Operation));
        Assert.All(observer.Commands, command => Assert.Equal(ProviderCommandKind.Read, command.Kind));
        Assert.DoesNotContain(observer.Commands, command => command.IsProbe);
    }

    private static void AssertOrdinaryWritesObserved(IStorageProviderConnection connection, string provider)
    {
        var unit = Unit(provider + "-ordinary-writes", ConcurrencyDeclaration.None);
        connection.Schema.Apply(unit);

        var observer = new ProviderCommandObserver();
        var session = connection.OpenSession(unit, StorageAccess.Global, observer);

        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(Values("one", "first", DateTimeOffset.UnixEpoch)).Status);
        Assert.Equal(WriteOutcomeStatus.Updated, session.Update(Values("one", "second", DateTimeOffset.UnixEpoch)).Status);
        Assert.Equal(WriteOutcomeStatus.Upserted, session.Upsert(Values("one", "third", DateTimeOffset.UnixEpoch)).Status);
        Assert.Equal(WriteOutcomeStatus.Deleted, session.Delete(new StorageKey(new Dictionary<string, object?> { ["id"] = "one" })).Status);

        // Four mutations, four write events. An unobserved ordinary write is a silent undercount in
        // exactly the seam the store-performance harness depends on, so this proof is per provider.
        Assert.Equal(4, observer.RoundTrips);
        Assert.Equal(4, observer.OfKind(ProviderCommandKind.Write).Count);
        Assert.DoesNotContain(observer.Commands, command => command.IsProbe);
    }

    private static void AssertUniqueViolationWithoutProbe(IStorageProviderConnection connection, string unitName)
    {
        var unit = Unit(unitName, ConcurrencyDeclaration.Optimistic(), includePartialUniqueIndex: true);
        connection.Schema.Apply(unit);
        connection.OpenSession(unit, StorageAccess.Global).Conditional()
            .ConditionalUpsert(Values("one", "duplicate", DateTimeOffset.UnixEpoch));

        var observer = new ProviderCommandObserver();
        var result = connection.OpenSession(unit, StorageAccess.Global, observer).Conditional().ConditionalUpsert(
            Values("two", "duplicate", DateTimeOffset.UnixEpoch));

        Assert.Equal(WriteOutcomeStatus.UniqueViolation, result.Status);
        Assert.Equal("ux_value_present", result.UniqueIndexName);
        Assert.Equal(1, observer.RoundTrips);
        Assert.DoesNotContain(observer.Commands, command => command.IsProbe);
    }

    private static void AssertOneRoundTripAndCreatedAt(
        IStorageProviderConnection connection,
        string provider)
    {
        var unit = Unit(provider + "-write-path");
        connection.Schema.Apply(unit);
        var firstTimestamp = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var firstObserver = new ProviderCommandObserver();
        var inserted = connection.OpenSession(unit, StorageAccess.Global, firstObserver).Conditional()
            .ConditionalUpsert(Values("one", "first", firstTimestamp));
        Assert.Equal(WriteOutcomeStatus.Inserted, inserted.Status);
        Assert.Equal(1, inserted.Version);
        Assert.Equal(1, firstObserver.RoundTrips);
        Assert.DoesNotContain(firstObserver.Commands, command => command.IsProbe);
        if (provider == "mongodb")
        {
            var commandText = Assert.Single(firstObserver.Commands).CommandText;
            Assert.Equal(
                "MongoDB.UpdateOne(upsert:true; filter=identity+version; update=$set/$inc/$setOnInsert)",
                commandText);
            Assert.DoesNotContain("first", commandText, StringComparison.Ordinal);
            Assert.DoesNotContain("one", commandText, StringComparison.Ordinal);
        }
        if (provider == "sqlserver")
        {
            var commandText = Assert.Single(firstObserver.Commands).CommandText;
            Assert.Contains("BEGIN TRANSACTION", commandText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("COMMIT TRANSACTION", commandText, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain(firstObserver.Commands, command =>
            command.CommandText?.Contains("SELECT FOR UPDATE", StringComparison.OrdinalIgnoreCase) == true);
        if (provider is "sqlite" or "postgresql")
            Assert.DoesNotContain(firstObserver.Commands, command =>
                command.CommandText?.Contains("SELECT", StringComparison.OrdinalIgnoreCase) == true);

        var secondObserver = new ProviderCommandObserver();
        var updated = connection.OpenSession(unit, StorageAccess.Global, secondObserver).Conditional()
            .ConditionalUpsert(
                Values("one", "second", firstTimestamp.AddDays(1)),
                new WriteOptions { Precondition = WritePrecondition.IfVersion(1) });
        Assert.Equal(WriteOutcomeStatus.Updated, updated.Status);
        Assert.Equal(2, updated.Version);
        Assert.Equal(1, secondObserver.RoundTrips);

        var read = connection.OpenSession(unit, StorageAccess.Global)
            .Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "one" }));
        Assert.NotNull(read);
        Assert.Equal(firstTimestamp, read!.Values.Values["createdAt"]);
        Assert.Equal("second", read.Values.Values["value"]);
    }

    private static void AssertNoneProviderWrites(IStorageProviderConnection connection, string provider)
    {
        var unit = Unit(provider switch
        {
            "postgresql" => "pg-none",
            "sqlserver" => "ss-none",
            _ => "sqlite-none"
        }, ConcurrencyDeclaration.None);
        connection.Schema.Apply(unit);
        var createdAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        connection.OpenSession(unit, StorageAccess.Global).Insert(Values("existing", "first", createdAt));

        var upsertObserver = new ProviderCommandObserver();
        var upsert = connection.OpenSession(unit, StorageAccess.Global, upsertObserver).Upsert(
            Values("existing", "second", createdAt.AddDays(1)));
        var updateObserver = new ProviderCommandObserver();
        var update = connection.OpenSession(unit, StorageAccess.Global, updateObserver)
            .Update(KeyOnlyValues("existing"));
        var missingObserver = new ProviderCommandObserver();
        var missing = connection.OpenSession(unit, StorageAccess.Global, missingObserver)
            .Update(KeyOnlyValues("missing"));

        Assert.Equal(WriteOutcomeStatus.Upserted, upsert.Status);
        var upsertCommand = Assert.Single(upsertObserver.Commands);
        Assert.False(upsertCommand.IsProbe);
        Assert.DoesNotContain("SELECT", upsertCommand.CommandText, StringComparison.OrdinalIgnoreCase);
        if (provider == "sqlserver")
            Assert.StartsWith("MERGE", upsertCommand.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(WriteOutcomeStatus.Updated, update.Status);
        Assert.Equal(WriteOutcomeStatus.NotFound, missing.Status);
        AssertSingleUpdateWithoutProbe(updateObserver);
        AssertSingleUpdateWithoutProbe(missingObserver);

        var stored = connection.OpenSession(unit, StorageAccess.Global)
            .Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "existing" }));
        Assert.NotNull(stored);
        Assert.Equal("second", stored!.Values.Values["value"]);
        Assert.Equal(createdAt, stored.Values.Values["createdAt"]);
    }

    private static void AssertSingleUpdateWithoutProbe(ProviderCommandObserver observer)
    {
        var command = Assert.Single(observer.Commands);
        Assert.False(command.IsProbe);
        Assert.Contains("UPDATE", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", command.CommandText, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------------------------
    // Fixtures.
    // ---------------------------------------------------------------------------------------------

    private static QueryRequest Page(StorageUnit unit) => new(
        new TableId(unit.Name),
        Predicate.AlwaysTrue.Instance,
        [],
        Projection.All,
        Paging.Keyset(10),
        ResultShape.Rows.Instance);

    private static StorageUnit Unit(
        string id,
        ConcurrencyDeclaration? concurrency = null,
        bool includePartialUniqueIndex = false,
        bool includePartialKeyIndex = false,
        ScopePolicy scope = ScopePolicy.Global) => new()
    {
        Id = new StorageUnitId(id + "-" + Guid.NewGuid().ToString("N")),
        Name = PhysicalName("w1_" + id + "_" + Guid.NewGuid().ToString("N")),
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 200 },
            new() { Name = "value", Type = PortableType.String, IsNullable = false, MaxLength = 200 },
            new() { Name = "createdAt", Type = PortableType.DateTimeOffset, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Concurrency = concurrency ?? ConcurrencyDeclaration.Optimistic(),
        Scope = scope,
        Indexes =
        [
            ..(includePartialUniqueIndex
                ? new[]
                {
                    new IndexDefinition
                    {
                        Name = "ux_value_present",
                        Columns = [new IndexColumn("value")],
                        IsUnique = true,
                        MissingValues = MissingValueBehavior.Excluded
                    }
                }
                : Array.Empty<IndexDefinition>()),
            ..(includePartialKeyIndex
                ? new[]
                {
                    new IndexDefinition
                    {
                        Name = "ux_key_present",
                        Columns = [new IndexColumn("id")],
                        IsUnique = true,
                        MissingValues = MissingValueBehavior.Excluded
                    }
                }
                : Array.Empty<IndexDefinition>())
        ]
    };

    private static StorageValues Values(string id, string value, DateTimeOffset createdAt) =>
        new(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["value"] = value,
            ["createdAt"] = createdAt
        });

    private static StorageValues KeyOnlyValues(string id) =>
        new(new Dictionary<string, object?> { ["id"] = id });

    private static string PhysicalName(string name)
    {
        var normalized = name.Replace('-', '_');
        return normalized.Length <= PortabilityValidator.MaximumPortableIdentifierLength
            ? normalized
            : normalized[..30] + "_" + normalized[^32..];
    }

    private sealed class TemporarySqliteStore : IDisposable
    {
        private readonly string directory;

        private TemporarySqliteStore(string directory)
        {
            this.directory = directory;
            ConnectionString = $"Data Source={Path.Combine(directory, "store.db")}";
        }

        public string ConnectionString { get; }

        public static TemporarySqliteStore Create()
        {
            var directory = Path.Combine(Path.GetTempPath(), "groundwork-w1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return new TemporarySqliteStore(directory);
        }

        public void Dispose()
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { }
        }
    }
}

internal static class StorageSessionExtensions
{
    public static IConcurrencyStorageSession Conditional(this IStorageSession session) =>
        session as IConcurrencyStorageSession ?? throw new InvalidOperationException(
            $"Provider session '{session.GetType().FullName}' does not expose conditional upsert.");
}
