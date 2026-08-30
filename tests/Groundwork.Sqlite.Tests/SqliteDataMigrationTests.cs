using System.Collections.Immutable;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Sqlite;
using Groundwork.Substrate.Relational;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

/// <summary>
/// Live SQLite proofs for the data-migration facility: chunked execution, a durable ledger, an
/// atomic chunk, and a resume that can tell an interrupted pass from a finished one.
/// </summary>
public sealed class SqliteDataMigrationTests
{
    private const string MigrationId = "2026-08-slugify";
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Separate_executors_serialize_a_complete_migration_pass_for_one_file()
    {
        using var store = TemporaryStore.Create();
        var target = Target().Identity;
        var firstExecutor = Executor(store);
        var secondExecutor = Executor(store);
        using var first = firstExecutor.AcquireMigrationLock(target);

        var waiting = Task.Run(() => secondExecutor.AcquireMigrationLock(target));
        await Task.Delay(100);
        Assert.False(waiting.IsCompleted);

        first.Dispose();
        using var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(target, second.Target);
    }

    [Fact]
    public void A_budgeted_apply_migrates_a_chunk_and_leaves_a_resumable_ledger()
    {
        using var store = TemporaryStore.Create();
        var target = Target();
        var executor = Executor(store);
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied,
            PhysicalSchemaApplication.Apply(Target(semanticMigrationId: null), executor, Now).Outcome);
        Seed(store, 5);

        var first = PhysicalSchemaApplication.Apply(
            target, executor, Now, null,
            Catalog(), new DataMigrationBudget { MaxRowsPerBatch = 2, MaxBatches = 1 });

        Assert.Equal(PhysicalSchemaApplicationOutcome.DataMigrationIncomplete, first.Outcome);
        var interrupted = Assert.Single(first.DataMigrations);
        Assert.Equal(DataMigrationStatus.Interrupted, interrupted.Status);
        Assert.Equal("2:i2;", interrupted.ResumeCursor);
        Assert.Equal(new string?[] { "a/1", "b/2", null, null, null }, Slugs(store));

        var entry = executor.ReadLedgerEntry(target.Identity, MigrationId);
        Assert.NotNull(entry);
        Assert.Equal(DataMigrationRunState.Running, entry.State);
        Assert.Equal("2:i2;", entry.Cursor);
        Assert.Null(entry.CompletedAt);

        var second = PhysicalSchemaApplication.Apply(
            target, executor, Now, null, Catalog(), new DataMigrationBudget { MaxRowsPerBatch = 2 });

        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, second.Outcome);
        Assert.Equal(DataMigrationStatus.Completed, Assert.Single(second.DataMigrations).Status);
        Assert.Equal(new string?[] { "a/1", "b/2", "c/3", "d/4", "e/5" }, Slugs(store));
        var completed = executor.ReadLedgerEntry(target.Identity, MigrationId)!;
        Assert.Equal(DataMigrationRunState.Completed, completed.State);
        Assert.Null(completed.Cursor);
        Assert.Equal(5, completed.RowsScanned);
        Assert.Equal(5, completed.RowsChanged);
        Assert.Equal(3, completed.Batches);

        var replay = PhysicalSchemaApplication.Apply(target, executor, Now, null, Catalog());
        Assert.Equal(DataMigrationStatus.Replayed, Assert.Single(replay.DataMigrations).Status);
        Assert.Equal(5, executor.ReadLedgerEntry(target.Identity, MigrationId)!.RowsScanned);
    }

    [Fact]
    public void A_chunk_that_fails_rolls_back_its_rows_and_its_cursor_together()
    {
        using var store = TemporaryStore.Create();
        var target = Target();
        var executor = Executor(store);
        PhysicalSchemaApplication.Apply(Target(semanticMigrationId: null), executor, Now);
        Seed(store, 4);

        // The first chunk (rows 1-2) commits; the second throws while transforming row 4.
        var catalog = Catalog(new FailingSlugTransform(failOnId: 4));
        var thrown = Assert.Throws<InvalidOperationException>(() => PhysicalSchemaApplication.Apply(
            target, executor, Now, null, catalog, new DataMigrationBudget { MaxRowsPerBatch = 2 }));
        Assert.Equal("transform refused row 4", thrown.Message);

        Assert.Equal(new string?[] { "a/1", "b/2", null, null }, Slugs(store));
        var entry = executor.ReadLedgerEntry(target.Identity, MigrationId)!;
        Assert.Equal(DataMigrationRunState.Running, entry.State);
        Assert.Equal("2:i2;", entry.Cursor);
        Assert.Equal(2, entry.RowsScanned);
        Assert.Equal(1, entry.Batches);
    }

    [Fact]
    public void A_migration_stopped_at_its_last_chunk_is_still_recorded_as_running()
    {
        using var store = TemporaryStore.Create();
        var target = Target();
        var executor = Executor(store);
        PhysicalSchemaApplication.Apply(Target(semanticMigrationId: null), executor, Now);
        Seed(store, 4);

        var stopped = PhysicalSchemaApplication.Apply(
            target, executor, Now, null, Catalog(),
            new DataMigrationBudget { MaxRowsPerBatch = 2, MaxBatches = 2 });

        // Every row now carries its migrated value, and the target is still not migrated: nothing
        // observed the source exhausted, so the ledger keeps a resume position rather than a
        // completion, and the schema tool reports the target as pending.
        Assert.Equal(new string?[] { "a/1", "b/2", "c/3", "d/4" }, Slugs(store));
        Assert.Equal(PhysicalSchemaApplicationOutcome.DataMigrationIncomplete, stopped.Outcome);
        Assert.Equal(DataMigrationRunState.Running, executor.ReadLedgerEntry(target.Identity, MigrationId)!.State);

        var resumed = PhysicalSchemaApplication.Apply(target, executor, Now, null, Catalog());
        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, resumed.Outcome);
        Assert.Equal(DataMigrationRunState.Completed, executor.ReadLedgerEntry(target.Identity, MigrationId)!.State);
    }

    [Fact]
    public void Reading_the_ledger_provisions_nothing()
    {
        using var store = TemporaryStore.Create();
        var executor = Executor(store);
        using (var connection = Connect(store))
        {
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE \"probe\" (\"id\" INTEGER);";
            create.ExecuteNonQuery();
        }

        Assert.Empty(executor.ReadLedgerEntries(Target().Identity));

        using var reopened = Connect(store);
        using var check = reopened.CreateCommand();
        check.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__groundwork_data_migrations';";
        Assert.Equal(0L, Convert.ToInt64(check.ExecuteScalar()));
    }

    [Fact]
    public void A_chunk_writes_one_set_based_statement_for_many_distinct_values()
    {
        var dialect = new SqliteDialect();
        var writes = new (IReadOnlyDictionary<string, object?>, IReadOnlyDictionary<string, object?>)[]
        {
            (Key(1), Set("a/1")),
            (Key(2), Set("b/2")),
            (Key(3), Set(null))
        };

        var rendered = RelationalRowMigration.RenderChunkUpdate(dialect, Unit(), writes);

        Assert.NotNull(rendered);
        Assert.Equal(
            "UPDATE \"orders\" SET \"slug\"=CASE " +
            "WHEN (\"id\"=@gwk0_0) THEN @gwv0_0 " +
            "WHEN (\"id\"=@gwk1_0) THEN @gwv1_0 " +
            "WHEN (\"id\"=@gwk2_0) THEN NULL " +
            "ELSE \"slug\" END " +
            "WHERE (\"id\"=@gwk0_0) OR (\"id\"=@gwk1_0) OR (\"id\"=@gwk2_0);",
            rendered.Sql);
        Assert.Equal(
            new[] { "@gwk0_0", "@gwk1_0", "@gwk2_0", "@gwv0_0", "@gwv1_0" },
            rendered.Parameters.Select(parameter => parameter.Key).ToArray());
    }

    [Fact]
    public void A_chunk_is_clamped_to_the_providers_parameter_budget()
    {
        // SQLite binds 999 parameters; one key column plus one target column is two per row, so a
        // 1,000-row request is admitted as 499 rather than failing in the driver.
        Assert.Equal(499, RelationalRowMigration.AdmittedRows(new SqliteDialect(), 1, 1, 1_000));
        Assert.Equal(10, RelationalRowMigration.AdmittedRows(new SqliteDialect(), 1, 1, 10));
    }

    [Fact]
    public void A_derived_column_backfill_spans_more_rows_than_one_chunk()
    {
        using var store = TemporaryStore.Create();
        var plain = PlainFoldedUnit(folded: false);
        var target = SqliteSchemaCoordinator.Target(SqliteSchemaCoordinator.Physicalize(plain));
        var executor = Executor(store);
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied,
            PhysicalSchemaApplication.Apply(target, executor, Now).Outcome);

        using (var connection = Connect(store))
        {
            using var transaction = connection.BeginTransaction();
            for (var index = 1; index <= 1_200; index++)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO \"folded\" (\"id\",\"name\") VALUES (@id,@name);";
                insert.Parameters.AddWithValue("@id", index);
                insert.Parameters.AddWithValue("@name", "Name" + index);
                insert.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        var folded = SqliteSchemaCoordinator.Target(SqliteSchemaCoordinator.Physicalize(PlainFoldedUnit(folded: true)));
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied,
            PhysicalSchemaApplication.Apply(folded, executor, Now).Outcome);

        using var reopened = Connect(store);
        using var check = reopened.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM \"folded\" WHERE \"__groundwork_search_name\" IS NULL;";
        Assert.Equal(0L, Convert.ToInt64(check.ExecuteScalar()));
        using var sample = reopened.CreateCommand();
        sample.CommandText = "SELECT \"__groundwork_search_name\" FROM \"folded\" WHERE \"id\"=1200;";
        Assert.Equal(
            PortableStringComparison.CreateSearchKey("Name1200", PortableStringComparisonPolicy.AsciiIgnoreCase),
            sample.ExecuteScalar() as string);
    }

    // ------------------------------------------------------------------ fixtures

    private static RelationalSchemaExecutor Executor(TemporaryStore store) =>
        new(() => Connect(store), new SqliteDialect());

    /// <summary>
    /// A connection carrying the ordinal collation the SQLite provider declares. The executor under
    /// test is the shared relational one, so the test supplies what the provider normally would.
    /// </summary>
    private static SqliteConnection Connect(TemporaryStore store)
    {
        var connection = new SqliteConnection(store.ConnectionString);
        connection.Open();
        connection.CreateCollation("GROUNDWORK_UTF16_ORDINAL", static (left, right) => string.CompareOrdinal(left, right));
        return connection;
    }

    private static StorageUnit Unit() => new()
    {
        Id = new StorageUnitId("orders"),
        Name = "orders",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
            new ColumnDefinition { Name = "name", Type = PortableType.String, MaxLength = 64 },
            new ColumnDefinition { Name = "slug", Type = PortableType.String, MaxLength = 64 }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static StorageUnit PlainFoldedUnit(bool folded) => new()
    {
        Id = new StorageUnitId("folded"),
        Name = "folded",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
            new ColumnDefinition
            {
                Name = "name",
                Type = PortableType.String,
                MaxLength = 64,
                Collation = folded ? PortableCollation.OrdinalIgnoreCase : null
            }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static PhysicalSchemaTarget Target(string? semanticMigrationId = MigrationId)
    {
        var physical = SqliteSchemaCoordinator.Physicalize(Unit());
        var basis = SqliteSchemaCoordinator.Target(physical);
        if (semanticMigrationId is null)
            return basis;
        return new PhysicalSchemaTarget(
            new SchemaSubject(physical, new SchemaEvolutionMetadata(semanticMigrationId: semanticMigrationId)),
            basis.Provider,
            basis.ProviderDefinitions);
    }

    private static DataMigrationCatalog Catalog(IDataMigrationTransform? transform = null) =>
        new([new DataMigration(MigrationId, new StorageUnitId("orders"), transform ?? new SlugTransform())]);

    private static void Seed(TemporaryStore store, int rows)
    {
        using var connection = Connect(store);
        for (var index = 1; index <= rows; index++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO \"orders\" (\"id\",\"name\",\"slug\") VALUES (@id,@name,NULL);";
            insert.Parameters.AddWithValue("@id", index);
            insert.Parameters.AddWithValue("@name", ((char)('a' + index - 1)).ToString());
            insert.ExecuteNonQuery();
        }
    }

    private static string?[] Slugs(TemporaryStore store)
    {
        using var connection = Connect(store);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"slug\" FROM \"orders\" ORDER BY \"id\";";
        using var reader = command.ExecuteReader();
        var slugs = new List<string?>();
        while (reader.Read())
            slugs.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        return slugs.ToArray();
    }

    private static IReadOnlyDictionary<string, object?> Key(int id) =>
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["id"] = id };

    private static IReadOnlyDictionary<string, object?> Set(string? slug) =>
        new Dictionary<string, object?>(StringComparer.Ordinal) { ["slug"] = slug };

    private sealed class SlugTransform : IDataMigrationTransform
    {
        public string Identity => "slug";
        public string Version => "v1";
        public ImmutableArray<string> SourceColumns => ["name"];
        public ImmutableArray<string> TargetColumns => ["slug"];
        public DataMigrationValues Transform(DataMigrationRow row) =>
            DataMigrationValues.Set(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["slug"] = $"{row["name"]}/{row["id"]}"
            });
    }

    private sealed class FailingSlugTransform(int failOnId) : IDataMigrationTransform
    {
        public string Identity => "slug";
        public string Version => "v1";
        public ImmutableArray<string> SourceColumns => ["name"];
        public ImmutableArray<string> TargetColumns => ["slug"];
        public DataMigrationValues Transform(DataMigrationRow row)
        {
            if (row["id"] is int id && id == failOnId)
                throw new InvalidOperationException($"transform refused row {id}");
            return DataMigrationValues.Set(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["slug"] = $"{row["name"]}/{row["id"]}"
            });
        }
    }

    private sealed class TemporaryStore : IDisposable
    {
        private readonly string directory;
        private TemporaryStore(string directory)
        {
            this.directory = directory;
            ConnectionString = $"Data Source={Path.Combine(directory, "store.db")}";
        }
        public string ConnectionString { get; }
        public static TemporaryStore Create()
        {
            var path = Path.Combine(Path.GetTempPath(), "groundwork-sqlite-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new(path);
        }
        public void Dispose() { try { Directory.Delete(directory, recursive: true); } catch { } }
    }
}
