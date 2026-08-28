using Microsoft.Data.Sqlite;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Store;
using Groundwork.Sqlite;
using Groundwork.Substrate.Relational;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteRuntimeAdmissionTests
{
    [Fact]
    public void Dropped_column_on_a_plain_unit_is_fatal_at_session_open()
    {
        using var store = TemporaryStore.Create();
        var unit = PlainUnit("admission-drop");
        using (var connection = new SqliteProviderFactory().Create(store.ConnectionString))
        {
            Assert.True(connection.Schema.Apply(unit).Applied);
            connection.OpenSession(unit, StorageAccess.Global).Insert(Values("one", "first"));
        }

        Mutate(store, $"ALTER TABLE \"{unit.Name}\" DROP COLUMN \"payload\";");

        using var reopened = new SqliteProviderFactory().Create(store.ConnectionString);
        var failure = Assert.Throws<GroundworkRuntimeSchemaAdmissionException>(() => reopened.OpenSession(unit, StorageAccess.Global));
        Assert.Contains("GW-RUNTIME-001", failure.Message, StringComparison.Ordinal);
        Assert.Contains("payload", failure.Message, StringComparison.Ordinal);
        Assert.Contains(failure.Result.Refusals, refusal => refusal.Code == "GW-RUNTIME-001");
    }

    [Fact]
    public void Retyped_column_on_a_plain_unit_is_fatal_at_session_open()
    {
        using var store = TemporaryStore.Create();
        var unit = PlainUnit("admission-retype");
        using (var connection = new SqliteProviderFactory().Create(store.ConnectionString))
        {
            Assert.True(connection.Schema.Apply(unit).Applied);
        }

        Mutate(store,
            $"ALTER TABLE \"{unit.Name}\" DROP COLUMN \"payload\";" +
            $"ALTER TABLE \"{unit.Name}\" ADD COLUMN \"payload\" INTEGER;");

        using var reopened = new SqliteProviderFactory().Create(store.ConnectionString);
        var failure = Assert.Throws<GroundworkRuntimeSchemaAdmissionException>(() => reopened.OpenSession(unit, StorageAccess.Global));
        Assert.Contains("GW-RUNTIME-001", failure.Message, StringComparison.Ordinal);
        Assert.Contains("payload", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dropped_column_is_fatal_for_a_unit_of_work_too()
    {
        using var store = TemporaryStore.Create();
        var unit = PlainUnit("admission-uow");
        using (var connection = new SqliteProviderFactory().Create(store.ConnectionString))
        {
            Assert.True(connection.Schema.Apply(unit).Applied);
        }

        Mutate(store, $"ALTER TABLE \"{unit.Name}\" DROP COLUMN \"payload\";");

        using var reopened = new SqliteProviderFactory().Create(store.ConnectionString);
        var failure = Assert.Throws<GroundworkRuntimeSchemaAdmissionException>(
            () => reopened.BeginUnitOfWork(StorageAccess.Global, unit));
        Assert.Contains("GW-RUNTIME-001", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_search_key_catalog_is_classified_as_drift_not_a_raw_provider_error()
    {
        using var store = TemporaryStore.Create();
        var unit = FoldedUnit("admission-algorithms");
        using (var connection = new SqliteProviderFactory().Create(store.ConnectionString))
        {
            Assert.True(connection.Schema.Apply(unit).Applied);
        }

        Mutate(store, "DROP TABLE \"__groundwork_search_key_algorithms\";");

        using var reopened = new SqliteProviderFactory().Create(store.ConnectionString);
        var failure = Assert.Throws<GroundworkRuntimeSchemaAdmissionException>(() => reopened.OpenSession(unit, StorageAccess.Global));
        Assert.Contains("GW-RUNTIME-001", failure.Message, StringComparison.Ordinal);
        Assert.Contains("__groundwork_search_key_algorithms", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dropped_index_degrades_instead_of_blocking_session_open()
    {
        using var store = TemporaryStore.Create();
        var unit = IndexedUnit("admission-index");
        using (var connection = new SqliteProviderFactory().Create(store.ConnectionString))
        {
            Assert.True(connection.Schema.Apply(unit).Applied);
        }

        Mutate(store, $"DROP INDEX \"{SqliteDialect.PhysicalIndexName(unit.Name, "by_payload")}\";");

        var executor = new RelationalSchemaExecutor(
            () => new SqliteConnection(store.ConnectionString), new SqliteDialect());
        var inspection = executor.InspectDeployedHistory(
            SqliteSchemaCoordinator.Target(SqliteSchemaCoordinator.Physicalize(unit)));
        Assert.True(inspection.IsAppliedSchemaValid);
        Assert.Empty(inspection.ColumnDrift);
        Assert.Contains(inspection.IndexDrift, refusal => refusal.Code == "GW-RUNTIME-002" &&
            refusal.Path == "indexes.by_payload");

        using var reopened = new SqliteProviderFactory().Create(store.ConnectionString);
        var session = reopened.OpenSession(unit, StorageAccess.Global);
        Assert.Equal(WriteOutcomeStatus.Inserted, session.Insert(Values("one", "first")).Status);
        Assert.NotNull(session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "one" })));
    }

    [Fact]
    public void Safe_auto_apply_of_an_added_index_returns_ready_without_stale_refusal()
    {
        using var store = TemporaryStore.Create();
        var original = PlainUnit("admission-auto-index");
        var desired = IndexedUnit("admission-auto-index");
        var factory = new SqliteProviderFactory();

        using (var deployed = factory.Create(store.ConnectionString))
            Assert.True(deployed.Schema.Apply(original).Applied);

        using var connection = factory.Create(store.ConnectionString);
        var result = connection.Schema.InspectRuntimeAdmission(
            desired,
            new GroundworkRuntimeSchemaAdmissionOptions { AutoApplyOnStartup = true });

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, result.Application?.Outcome);
        Assert.True(result.IsReady);
        Assert.Equal(GroundworkRuntimeSchemaAdmissionStatus.Ready, result.Status);
        Assert.DoesNotContain(result.Refusals, refusal => refusal.Code == "GW-RUNTIME-002");
        Assert.True(connection.Schema.Diff(desired).IsEmpty);
    }

    [Fact]
    public void Admission_inspects_once_per_unit_per_connection()
    {
        using var store = TemporaryStore.Create();
        var unit = PlainUnit("admission-cache");
        using (var connection = new SqliteProviderFactory().Create(store.ConnectionString))
        {
            Assert.True(connection.Schema.Apply(unit).Applied);
        }

        using var reopened = new SqliteProviderFactory().Create(store.ConnectionString);
        var firstObserver = new ProviderCommandObserver();
        _ = reopened.OpenSession(unit, StorageAccess.Global, firstObserver);
        var admissionEvent = Assert.Single(firstObserver.Commands);
        Assert.Equal("sqlite.schema-admission", admissionEvent.Operation);
        Assert.Equal(ProviderCommandKind.Read, admissionEvent.Kind);
        Assert.False(admissionEvent.IsProbe);

        var secondObserver = new ProviderCommandObserver();
        _ = reopened.OpenSession(unit, StorageAccess.Global, secondObserver);
        Assert.Equal(0, secondObserver.RoundTrips);

        var workObserver = new ProviderCommandObserver();
        using (var work = reopened.BeginUnitOfWork(StorageAccess.Global, BatchWriteOptions.Default, workObserver, unit))
        {
            Assert.Equal(0, workObserver.RoundTrips);
        }
    }

    [Fact]
    public void Apply_then_first_open_verifies_the_catalog_once()
    {
        using var store = TemporaryStore.Create();
        var unit = PlainUnit("admission-apply");
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        Assert.True(connection.Schema.Apply(unit).Applied);

        var firstObserver = new ProviderCommandObserver();
        _ = connection.OpenSession(unit, StorageAccess.Global, firstObserver);
        Assert.Equal("sqlite.schema-admission", Assert.Single(firstObserver.Commands).Operation);

        var secondObserver = new ProviderCommandObserver();
        _ = connection.OpenSession(unit, StorageAccess.Global, secondObserver);
        Assert.Equal(0, secondObserver.RoundTrips);
    }

    [Fact]
    public void Tamper_between_apply_and_first_open_is_detected_on_the_same_connection()
    {
        using var store = TemporaryStore.Create();
        var unit = PlainUnit("admission-tamper");
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);
        Assert.True(connection.Schema.Apply(unit).Applied);

        Mutate(store, $"ALTER TABLE \"{unit.Name}\" DROP COLUMN \"payload\";");

        var failure = Assert.Throws<GroundworkRuntimeSchemaAdmissionException>(() => connection.OpenSession(unit, StorageAccess.Global));
        Assert.Contains("GW-RUNTIME-001", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Never_applied_unit_is_admitted_read_only_and_cached()
    {
        using var store = TemporaryStore.Create();
        var unit = PlainUnit("admission-unapplied");
        using var connection = new SqliteProviderFactory().Create(store.ConnectionString);

        var firstObserver = new ProviderCommandObserver();
        _ = connection.OpenSession(unit, StorageAccess.Global, firstObserver);
        Assert.Equal("sqlite.schema-admission", Assert.Single(firstObserver.Commands).Operation);

        using (var raw = new SqliteConnection(store.ConnectionString))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table';";
            Assert.Equal(0L, command.ExecuteScalar());
        }

        var secondObserver = new ProviderCommandObserver();
        _ = connection.OpenSession(unit, StorageAccess.Global, secondObserver);
        Assert.Equal(0, secondObserver.RoundTrips);
    }

    [Fact]
    public void Read_only_store_opens_sessions_without_writing()
    {
        using var store = TemporaryStore.Create();
        var unit = PlainUnit("admission-readonly");
        using (var connection = new SqliteProviderFactory().Create(store.ConnectionString))
        {
            Assert.True(connection.Schema.Apply(unit).Applied);
            connection.OpenSession(unit, StorageAccess.Global).Insert(Values("one", "first"));
        }
        SqliteConnection.ClearAllPools();

        using var readOnly = new SqliteProviderFactory().Create(store.ConnectionString + ";Mode=ReadOnly");
        var session = readOnly.OpenSession(unit, StorageAccess.Global);
        Assert.NotNull(session.Read(new StorageKey(new Dictionary<string, object?> { ["id"] = "one" })));
    }

    [Fact]
    public void Private_memory_store_admission_is_read_only_and_cached()
    {
        using var connection = new SqliteProviderFactory().Create("Data Source=:memory:");
        var unit = PlainUnit("admission-memory");

        var firstObserver = new ProviderCommandObserver();
        _ = connection.OpenSession(unit, StorageAccess.Global, firstObserver);
        Assert.Equal("sqlite.schema-admission", Assert.Single(firstObserver.Commands).Operation);

        var secondObserver = new ProviderCommandObserver();
        _ = connection.OpenSession(unit, StorageAccess.Global, secondObserver);
        Assert.Equal(0, secondObserver.RoundTrips);
    }

    private static void Mutate(TemporaryStore store, string sql)
    {
        using var connection = new SqliteConnection(store.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static StorageUnit PlainUnit(string id) => new()
    {
        Id = new StorageUnitId(id),
        Name = id.Replace('-', '_'),
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
            new ColumnDefinition { Name = "payload", Type = PortableType.String }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static StorageUnit IndexedUnit(string id) => PlainUnit(id) with
    {
        Indexes = [new IndexDefinition { Name = "by_payload", Columns = [new IndexColumn("payload")] }]
    };

    private static StorageUnit FoldedUnit(string id) => PlainUnit(id) with
    {
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false },
            new ColumnDefinition { Name = "payload", Type = PortableType.String, MaxLength = 64, Collation = PortableCollation.OrdinalIgnoreCase }
        ]
    };

    private static StorageValues Values(string id, string payload) => new(
        new Dictionary<string, object?> { ["id"] = id, ["payload"] = payload });

    private sealed class TemporaryStore : IDisposable
    {
        private readonly string directory;
        private TemporaryStore(string directory) { this.directory = directory; ConnectionString = $"Data Source={Path.Combine(directory, "store.db")}"; }
        public string ConnectionString { get; }
        public static TemporaryStore Create() { var path = Path.Combine(Path.GetTempPath(), "groundwork-sqlite-admission-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return new(path); }
        public void Dispose() { try { Directory.Delete(directory, recursive: true); } catch { } }
    }
}
