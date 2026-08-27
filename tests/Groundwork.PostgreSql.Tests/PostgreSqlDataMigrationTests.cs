using System.Collections.Immutable;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Substrate.Relational;
using Npgsql;
using Xunit;

namespace Groundwork.PostgreSql.Tests;

/// <summary>
/// Live PostgreSQL proofs for the data-migration facility. The relational implementation is shared,
/// so these concentrate on what only a real server settles: the keyset scan and the set-based
/// chunk update running against a composite key and non-text columns.
/// </summary>
public sealed class PostgreSqlDataMigrationTests
{
    private const string MigrationId = "2026-08-normalize";
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 11, 0, 0, TimeSpan.Zero);

    [SkippableFact]
    public void A_budgeted_migration_chunks_resumes_and_replays_against_a_composite_key()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var target = Target();
        var executor = new RelationalSchemaExecutor(
            () => new NpgsqlConnection(database.ConnectionString), new PostgreSqlDialect());
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied,
            PhysicalSchemaApplication.Apply(target, executor, Now).Outcome);
        Seed(database, 6);

        var first = PhysicalSchemaApplication.Apply(
            target, executor, Now, null, Catalog(),
            new DataMigrationBudget { MaxRowsPerBatch = 2, MaxBatches = 1 });

        Assert.Equal(PhysicalSchemaApplicationOutcome.DataMigrationIncomplete, first.Outcome);
        var interrupted = Assert.Single(first.DataMigrations);
        Assert.Equal(DataMigrationStatus.Interrupted, interrupted.Status);
        Assert.Equal(2, interrupted.RowsScanned);
        // Two key columns, so the cursor names both in declared order.
        Assert.Equal("7:sacme-1;2:i2;", interrupted.ResumeCursor);
        Assert.Equal(new long?[] { 10, 20, null, null, null, null }, Scores(database));

        var entry = executor.ReadLedgerEntry(target.Identity, MigrationId)!;
        Assert.Equal(DataMigrationRunState.Running, entry.State);
        Assert.Null(entry.CompletedAt);

        var second = PhysicalSchemaApplication.Apply(
            target, executor, Now, null, Catalog(), new DataMigrationBudget { MaxRowsPerBatch = 4 });

        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, second.Outcome);
        Assert.Equal(DataMigrationStatus.Completed, Assert.Single(second.DataMigrations).Status);
        Assert.Equal(new long?[] { 10, 20, 30, 40, 50, 60 }, Scores(database));
        var completed = executor.ReadLedgerEntry(target.Identity, MigrationId)!;
        Assert.Equal(DataMigrationRunState.Completed, completed.State);
        Assert.Null(completed.Cursor);
        Assert.Equal(6, completed.RowsScanned);
        Assert.Equal(6, completed.RowsChanged);

        var replay = PhysicalSchemaApplication.Apply(target, executor, Now, null, Catalog());
        Assert.Equal(DataMigrationStatus.Replayed, Assert.Single(replay.DataMigrations).Status);
        Assert.Equal(6, executor.ReadLedgerEntry(target.Identity, MigrationId)!.RowsScanned);
    }

    [SkippableFact]
    public async Task The_asynchronous_surface_migrates_the_same_rows()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var target = Target();
        var executor = new RelationalSchemaExecutor(
            () => new NpgsqlConnection(database.ConnectionString), new PostgreSqlDialect());
        PhysicalSchemaApplication.Apply(target, executor, Now);
        Seed(database, 5);

        var result = await PhysicalSchemaApplication.ApplyAsync(
            target, executor, Now, null, Catalog(), new DataMigrationBudget { MaxRowsPerBatch = 2 });

        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, result.Outcome);
        Assert.Equal(DataMigrationStatus.Completed, Assert.Single(result.DataMigrations).Status);
        Assert.Equal(new long?[] { 10, 20, 30, 40, 50 }, Scores(database));
        var entries = await executor.ReadLedgerEntriesAsync(target.Identity);
        Assert.Equal(DataMigrationRunState.Completed, Assert.Single(entries).State);
    }

    [SkippableFact]
    public void A_failing_chunk_leaves_neither_its_rows_nor_its_cursor_behind()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var target = Target();
        var executor = new RelationalSchemaExecutor(
            () => new NpgsqlConnection(database.ConnectionString), new PostgreSqlDialect());
        PhysicalSchemaApplication.Apply(target, executor, Now);
        Seed(database, 4);

        Assert.Throws<InvalidOperationException>(() => PhysicalSchemaApplication.Apply(
            target, executor, Now, null, Catalog(new FailingTransform(failOnIndex: 4)),
            new DataMigrationBudget { MaxRowsPerBatch = 2 }));

        Assert.Equal(new long?[] { 10, 20, null, null }, Scores(database));
        var entry = executor.ReadLedgerEntry(target.Identity, MigrationId)!;
        Assert.Equal("7:sacme-1;2:i2;", entry.Cursor);
        Assert.Equal(2, entry.RowsScanned);
        Assert.Equal(1, entry.Batches);
    }

    [SkippableFact]
    public void The_provider_advertises_every_capability_the_facility_requires()
    {
        using var database = PostgreSqlFixture.OpenOrSkip();
        var executor = new RelationalSchemaExecutor(
            () => new NpgsqlConnection(database.ConnectionString), new PostgreSqlDialect());

        Assert.Equal(
            DataMigrationCapabilities.KeysetScan |
            DataMigrationCapabilities.AtomicChunkProgress |
            DataMigrationCapabilities.AppliedLedger |
            DataMigrationCapabilities.SetBasedBatchUpdate,
            executor.Capabilities);
        DataMigrationRunner.EnsureCapabilities(executor);
    }

    // ------------------------------------------------------------------ fixtures

    private static StorageUnit Unit() => new()
    {
        Id = new StorageUnitId("scores"),
        Name = "scores",
        Columns =
        [
            new ColumnDefinition { Name = "tenant", Type = PortableType.String, MaxLength = 32, IsNullable = false },
            new ColumnDefinition { Name = "seq", Type = PortableType.Int32, IsNullable = false },
            new ColumnDefinition { Name = "weight", Type = PortableType.Int32, IsNullable = false },
            new ColumnDefinition { Name = "score", Type = PortableType.Int64 }
        ],
        Key = new KeyDefinition { Columns = ["tenant", "seq"] }
    };

    private static PhysicalSchemaTarget Target()
    {
        var physical = PostgreSqlSchemaCoordinator.Physicalize(Unit());
        var basis = PostgreSqlSchemaCoordinator.Target(physical);
        return new PhysicalSchemaTarget(
            new SchemaSubject(physical, new SchemaEvolutionMetadata(semanticMigrationId: MigrationId)),
            basis.Provider,
            basis.ProviderDefinitions);
    }

    private static DataMigrationCatalog Catalog(IDataMigrationTransform? transform = null) =>
        new([new DataMigration(MigrationId, new StorageUnitId("scores"), transform ?? new ScoreTransform())]);

    private static void Seed(PostgreSqlFixture database, int rows)
    {
        using var connection = new NpgsqlConnection(database.ConnectionString);
        connection.Open();
        for (var index = 1; index <= rows; index++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO \"scores\" (\"tenant\",\"seq\",\"weight\",\"score\") VALUES (@tenant,@seq,@weight,NULL);";
            insert.Parameters.AddWithValue("@tenant", "acme-" + (index <= 2 ? 1 : 2));
            insert.Parameters.AddWithValue("@seq", index);
            insert.Parameters.AddWithValue("@weight", index * 10);
            insert.ExecuteNonQuery();
        }
    }

    private static long?[] Scores(PostgreSqlFixture database)
    {
        using var connection = new NpgsqlConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"score\" FROM \"scores\" ORDER BY \"tenant\",\"seq\";";
        using var reader = command.ExecuteReader();
        var scores = new List<long?>();
        while (reader.Read())
            scores.Add(reader.IsDBNull(0) ? null : reader.GetInt64(0));
        return scores.ToArray();
    }

    private sealed class ScoreTransform : IDataMigrationTransform
    {
        public string Identity => "score/v1";
        public ImmutableArray<string> SourceColumns => ["weight"];
        public ImmutableArray<string> TargetColumns => ["score"];
        public DataMigrationValues Transform(DataMigrationRow row) =>
            DataMigrationValues.Set(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // The declared type is Int32, so a transform written once sees an int on every
                // provider rather than whatever the driver happened to hand back.
                ["score"] = (long)(int)row["weight"]!
            });
    }

    private sealed class FailingTransform(int failOnIndex) : IDataMigrationTransform
    {
        public string Identity => "score/v1";
        public ImmutableArray<string> SourceColumns => ["weight"];
        public ImmutableArray<string> TargetColumns => ["score"];
        public DataMigrationValues Transform(DataMigrationRow row)
        {
            var weight = (int)row["weight"]!;
            if (weight == failOnIndex * 10)
                throw new InvalidOperationException($"transform refused weight {weight}");
            return DataMigrationValues.Set(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["score"] = (long)weight
            });
        }
    }
}
