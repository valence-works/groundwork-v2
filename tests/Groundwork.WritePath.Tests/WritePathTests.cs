using Groundwork.Kernel;
using Groundwork.MongoDb.TestingAdapter;
using Groundwork.PostgreSql;
using Groundwork.Sqlite;
using Groundwork.SqlServer;
using Groundwork.Testing;
using Xunit;

namespace Groundwork.WritePath.Tests;

public sealed class WritePathTests
{
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
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL write-path tests.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        AssertOneRoundTripAndCreatedAt(connection, "postgresql");
    }

    [SkippableFact]
    public void SQLServer_conditional_upsert_uses_one_batch_and_preserves_created_at()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run SQL Server write-path tests.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        AssertOneRoundTripAndCreatedAt(connection, "sqlserver");
    }

    [SkippableFact]
    public void MongoDB_conditional_upsert_uses_one_native_update_and_preserves_created_at()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB write-path tests.");
        using var connection = new MongoDbTestingFactory().Create(connectionString!);
        AssertOneRoundTripAndCreatedAt(connection, "mongodb");
    }

    [Fact]
    public void SQLite_conflict_detail_is_lazy_and_cached()
    {
        using var store = TemporarySqliteStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Unit("sqlite-lazy");
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global);
        session.Conditional().ConditionalUpsert(Values("one", "first", DateTimeOffset.Parse("2026-01-01T00:00:00Z")),
            new WriteOptions { Observer = new WritePathObserver() });

        var observer = new WritePathObserver();
        var outcome = session.Conditional().ConditionalUpsert(
            Values("one", "stale", DateTimeOffset.Parse("2026-01-02T00:00:00Z")),
            new WriteOptions { ExpectedVersion = 99, Observer = observer });

        Assert.Equal(WriteOutcomeStatus.ConcurrencyConflict, outcome.Status);
        Assert.Equal(1, observer.RoundTrips);
        Assert.Equal(WriteOutcomeStatus.ConcurrencyConflict, outcome.Detail.Status);
        Assert.Equal(2, observer.RoundTrips);
        _ = outcome.Detail;
        Assert.Equal(2, observer.RoundTrips);
    }

    [Fact]
    public void SQLite_non_versioned_conditional_upsert_reports_insert_and_update_without_a_read()
    {
        using var store = TemporarySqliteStore.Create();
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        var unit = Unit("sqlite-none", ConcurrencyDeclaration.None);
        connection.Schema.Apply(unit);
        var rawSession = connection.OpenSession(unit, StorageAccess.Global);
        var session = rawSession.Conditional();

        var firstObserver = new WritePathObserver();
        var first = session.ConditionalUpsert(
            Values("one", "first", DateTimeOffset.UnixEpoch),
            new WriteOptions { Observer = firstObserver });
        var secondObserver = new WritePathObserver();
        var second = session.ConditionalUpsert(
            Values("one", "second", DateTimeOffset.UnixEpoch),
            new WriteOptions { Observer = secondObserver });

        Assert.Equal(WriteOutcomeStatus.Inserted, first.Status);
        Assert.Equal(WriteOutcomeStatus.Updated, second.Status);
        Assert.Equal(1, firstObserver.RoundTrips);
        Assert.Equal(1, secondObserver.RoundTrips);
        Assert.DoesNotContain(firstObserver.Commands, command => command.IsProbe);
        Assert.DoesNotContain(secondObserver.Commands, command => command.IsProbe);
        Assert.DoesNotContain(firstObserver.Commands, command =>
            command.CommandText?.Contains("SELECT", StringComparison.OrdinalIgnoreCase) == true);
        var stored = rawSession.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "one" }));
        Assert.NotNull(stored);
        Assert.DoesNotContain("__groundwork_action", stored!.Values.Values.Keys);
    }

    [SkippableFact]
    public void PostgreSQL_partial_unique_violation_names_the_index_without_a_probe()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL write-path tests.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        var unit = Unit("postgresql-partial", ConcurrencyDeclaration.Optimistic, includePartialUniqueIndex: true);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global).Conditional();
        _ = session.ConditionalUpsert(Values("one", "duplicate", DateTimeOffset.UnixEpoch));

        var observer = new WritePathObserver();
        var result = session.ConditionalUpsert(
            Values("two", "duplicate", DateTimeOffset.UnixEpoch),
            new WriteOptions { Observer = observer });

        Assert.Equal(WriteOutcomeStatus.UniqueViolation, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.UniqueIndexName));
        Assert.Equal(1, observer.RoundTrips);
        Assert.DoesNotContain(observer.Commands, command => command.IsProbe);
    }

    [SkippableFact]
    public void PostgreSQL_partial_key_conflict_target_is_inferred_and_executes()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL write-path tests.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        var unit = Unit("postgresql-partial-key", ConcurrencyDeclaration.Optimistic, includePartialKeyIndex: true);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global).Conditional();
        _ = session.ConditionalUpsert(Values("one", "first", DateTimeOffset.UnixEpoch));

        var observer = new WritePathObserver();
        var result = session.ConditionalUpsert(
            Values("one", "second", DateTimeOffset.UnixEpoch.AddDays(1)),
            new WriteOptions { ExpectedVersion = 1, Observer = observer });

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
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL write-path tests.");
        using var connection = new PostgreSqlProviderFactory().Create(connectionString!);
        var unit = Unit("postgresql-scoped", ConcurrencyDeclaration.Optimistic, scope: ScopePolicy.Scoped);
        connection.Schema.Apply(unit);
        var rawSession = connection.OpenSession(unit, StorageAccess.Scoped(new StorageScope("scope-a")));
        var session = rawSession.Conditional();
        var observer = new WritePathObserver();
        var result = session.ConditionalUpsert(
            Values("one", "first", DateTimeOffset.UnixEpoch),
            new WriteOptions { Observer = observer });

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
        var observer = new WritePathObserver();
        var outcome = connection.OpenSession(unit, StorageAccess.Global).Conditional().ConditionalUpsert(
            Values("missing", "value", DateTimeOffset.UnixEpoch),
            new WriteOptions { ExpectedVersion = 99, Observer = observer });

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
        var unit = Unit("sqlite-unique", ConcurrencyDeclaration.Optimistic, includePartialUniqueIndex: true);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global).Conditional();
        _ = session.ConditionalUpsert(Values("one", "duplicate", DateTimeOffset.UnixEpoch));

        var observer = new WritePathObserver();
        var result = session.ConditionalUpsert(
            Values("two", "duplicate", DateTimeOffset.UnixEpoch),
            new WriteOptions { Observer = observer });

        Assert.Equal(WriteOutcomeStatus.UniqueViolation, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.UniqueIndexName));
        Assert.Equal(1, observer.RoundTrips);
        Assert.DoesNotContain(observer.Commands, command => command.IsProbe);
    }

    [SkippableFact]
    public void MongoDB_unique_violation_is_not_misclassified_as_a_cas_conflict()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB write-path tests.");
        using var connection = new MongoDbTestingFactory().Create(connectionString!);
        var unit = Unit("mongodb-unique", ConcurrencyDeclaration.Optimistic, includePartialUniqueIndex: true);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global).Conditional();
        _ = session.ConditionalUpsert(Values("one", "duplicate", DateTimeOffset.UnixEpoch));

        var observer = new WritePathObserver();
        var result = session.ConditionalUpsert(
            Values("two", "duplicate", DateTimeOffset.UnixEpoch),
            new WriteOptions { Observer = observer });

        Assert.Equal(WriteOutcomeStatus.UniqueViolation, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.UniqueIndexName));
        Assert.Equal(1, observer.RoundTrips);
        Assert.DoesNotContain(observer.Commands, command => command.IsProbe);
    }

    [SkippableFact]
    public void SQLServer_unique_violation_names_the_index_without_a_probe()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString), "Set GROUNDWORK_SQLSERVER_CONNECTION to run SQL Server write-path tests.");
        using var connection = new SqlServerProviderFactory().Create(connectionString!);
        var unit = Unit("sqlserver-unique", ConcurrencyDeclaration.Optimistic, includePartialUniqueIndex: true);
        connection.Schema.Apply(unit);
        var session = connection.OpenSession(unit, StorageAccess.Global).Conditional();
        _ = session.ConditionalUpsert(Values("one", "duplicate", DateTimeOffset.UnixEpoch));

        var observer = new WritePathObserver();
        var result = session.ConditionalUpsert(
            Values("two", "duplicate", DateTimeOffset.UnixEpoch),
            new WriteOptions { Observer = observer });

        Assert.Equal(WriteOutcomeStatus.UniqueViolation, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.UniqueIndexName));
        Assert.Equal(1, observer.RoundTrips);
        Assert.DoesNotContain(observer.Commands, command => command.IsProbe);
    }

    private static void AssertOneRoundTripAndCreatedAt(
        IStorageProviderConnection connection,
        string provider)
    {
        var unit = Unit(provider + "-write-path");
        connection.Schema.Apply(unit);
        var rawSession = connection.OpenSession(unit, StorageAccess.Global);
        var session = rawSession.Conditional();
        var firstTimestamp = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var firstObserver = new WritePathObserver();
        var inserted = session.ConditionalUpsert(
            Values("one", "first", firstTimestamp),
            new WriteOptions { Observer = firstObserver });
        Assert.Equal(WriteOutcomeStatus.Inserted, inserted.Status);
        Assert.Equal(1, inserted.Version);
        Assert.Equal(1, firstObserver.RoundTrips);
        Assert.DoesNotContain(firstObserver.Commands, command => command.IsProbe);
        Assert.DoesNotContain(firstObserver.Commands, command =>
            command.CommandText?.Contains("SELECT FOR UPDATE", StringComparison.OrdinalIgnoreCase) == true);
        if (provider is "sqlite" or "postgresql")
            Assert.DoesNotContain(firstObserver.Commands, command =>
                command.CommandText?.Contains("SELECT", StringComparison.OrdinalIgnoreCase) == true);

        var secondObserver = new WritePathObserver();
        var updated = session.ConditionalUpsert(
            Values("one", "second", firstTimestamp.AddDays(1)),
            new WriteOptions { ExpectedVersion = 1, Observer = secondObserver });
        Assert.Equal(WriteOutcomeStatus.Updated, updated.Status);
        Assert.Equal(2, updated.Version);
        Assert.Equal(1, secondObserver.RoundTrips);

        var read = rawSession.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "one" }));
        Assert.NotNull(read);
        Assert.Equal(firstTimestamp, read!.Values.Values["createdAt"]);
        Assert.Equal("second", read.Values.Values["value"]);
    }

    private static StorageUnit Unit(
        string id,
        ConcurrencyDeclaration concurrency = ConcurrencyDeclaration.Optimistic,
        bool includePartialUniqueIndex = false,
        bool includePartialKeyIndex = false,
        ScopePolicy scope = ScopePolicy.Global) => new()
    {
        Id = new StorageUnitId(id + "-" + Guid.NewGuid().ToString("N")),
        Name = "w1_" + id + "_" + Guid.NewGuid().ToString("N"),
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 200 },
            new() { Name = "value", Type = PortableType.String, IsNullable = false, MaxLength = 200 },
            new() { Name = "createdAt", Type = PortableType.DateTimeOffset, IsNullable = false }
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Concurrency = concurrency,
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
