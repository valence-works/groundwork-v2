using System.Collections.Immutable;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Store;
using Groundwork.Substrate.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;
using Xunit;

namespace Groundwork.MongoDb.Tests;

/// <summary>
/// Live MongoDB proofs. MongoDB is not relational, so what it advertises differs from the
/// relational providers, and the facility refuses rather than approximating what it cannot do.
/// </summary>
public sealed class MongoDataMigrationTests
{
    private const string MigrationId = "2026-08-slugify";
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 12, 0, 0, TimeSpan.Zero);

    [SkippableFact]
    public void A_replica_set_advertises_atomic_chunks_but_not_the_relational_set_based_update()
    {
        using var context = OpenContextOrSkip();
        var executor = new MongoDataMigrationExecutor(context);

        Assert.Equal(
            DataMigrationCapabilities.KeysetScan |
            DataMigrationCapabilities.AppliedLedger |
            DataMigrationCapabilities.AtomicChunkProgress,
            executor.Capabilities);
        // Mongo has no multi-document update carrying a different value per document, so it does
        // not claim the relational batch update. The facility does not require it.
        Assert.False(executor.Capabilities.HasFlag(DataMigrationCapabilities.SetBasedBatchUpdate));
        DataMigrationRunner.EnsureCapabilities(executor);
    }

    [SkippableFact]
    public void A_budgeted_migration_chunks_resumes_and_replays()
    {
        var connectionString = ConnectionOrSkip();
        var unit = Unit();
        using var connection = (MongoDbProviderConnection)new MongoDbProviderFactory().Create(connectionString);
        Assert.True(connection.Schema.Apply(unit).Applied);
        Seed(connection, unit, 5);

        var executor = connection.DataMigrations;
        var target = MongoDataMigrationExecutor.TargetFor(unit);
        var migration = new DataMigration(MigrationId, unit.Id, new SlugTransform());

        var first = DataMigrationRunner.Run(
            executor, target, unit, migration,
            new DataMigrationBudget { MaxRowsPerBatch = 2, MaxBatches = 1 }, Now);

        Assert.Equal(DataMigrationStatus.Interrupted, first.Status);
        Assert.Equal(2, first.RowsScanned);
        Assert.Equal("4:sid2;", first.ResumeCursor);
        Assert.Equal(new string?[] { "a/id1", "b/id2", null, null, null }, Slugs(connection, unit));
        var running = executor.ReadLedgerEntry(target, MigrationId)!;
        Assert.Equal(DataMigrationRunState.Running, running.State);
        Assert.Null(running.CompletedAt);

        var second = DataMigrationRunner.Run(
            executor, target, unit, migration, new DataMigrationBudget { MaxRowsPerBatch = 2 }, Now);

        Assert.Equal(DataMigrationStatus.Completed, second.Status);
        Assert.Equal(5, second.RowsScanned);
        Assert.Equal(new string?[] { "a/id1", "b/id2", "c/id3", "d/id4", "e/id5" }, Slugs(connection, unit));
        var completed = executor.ReadLedgerEntry(target, MigrationId)!;
        Assert.Equal(DataMigrationRunState.Completed, completed.State);
        Assert.Null(completed.Cursor);

        var replay = DataMigrationRunner.Run(executor, target, unit, migration, null, Now);
        Assert.Equal(DataMigrationStatus.Replayed, replay.Status);
        Assert.Equal(5, replay.RowsScanned);
    }

    [SkippableFact]
    public async Task The_asynchronous_surface_migrates_the_same_documents()
    {
        var connectionString = ConnectionOrSkip();
        var unit = Unit();
        using var connection = (MongoDbProviderConnection)new MongoDbProviderFactory().Create(connectionString);
        Assert.True(connection.Schema.Apply(unit).Applied);
        Seed(connection, unit, 4);

        var result = await DataMigrationRunner.RunAsync(
            connection.DataMigrations,
            MongoDataMigrationExecutor.TargetFor(unit),
            unit,
            new DataMigration(MigrationId, unit.Id, new SlugTransform()),
            new DataMigrationBudget { MaxRowsPerBatch = 2 },
            Now);

        Assert.Equal(DataMigrationStatus.Completed, result.Status);
        Assert.Equal(new string?[] { "a/id1", "b/id2", "c/id3", "d/id4" }, Slugs(connection, unit));
    }

    [SkippableFact]
    public void A_failing_chunk_leaves_neither_its_documents_nor_its_cursor_behind()
    {
        var connectionString = ConnectionOrSkip();
        var unit = Unit();
        using var connection = (MongoDbProviderConnection)new MongoDbProviderFactory().Create(connectionString);
        Assert.True(connection.Schema.Apply(unit).Applied);
        Seed(connection, unit, 4);
        var executor = connection.DataMigrations;
        var target = MongoDataMigrationExecutor.TargetFor(unit);

        Assert.Throws<InvalidOperationException>(() => DataMigrationRunner.Run(
            executor, target, unit,
            new DataMigration(MigrationId, unit.Id, new FailingSlugTransform("id4")),
            new DataMigrationBudget { MaxRowsPerBatch = 2 }, Now));

        Assert.Equal(new string?[] { "a/id1", "b/id2", null, null }, Slugs(connection, unit));
        var entry = executor.ReadLedgerEntry(target, MigrationId)!;
        Assert.Equal(DataMigrationRunState.Running, entry.State);
        Assert.Equal("4:sid2;", entry.Cursor);
        Assert.Equal(2, entry.RowsScanned);
    }

    [SkippableFact]
    public void A_standalone_deployment_refuses_rather_than_writing_rows_it_cannot_commit_with_progress()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_STANDALONE_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_STANDALONE_CONNECTION to prove the standalone refusal.");
        var url = new MongoUrlBuilder(connectionString) { DatabaseName = "gwstandalone_" + Guid.NewGuid().ToString("N") };
        var unit = Unit();
        using var connection = (MongoDbProviderConnection)new MongoDbProviderFactory().Create(url.ToMongoUrl().ToString());
        Assert.True(connection.Schema.Apply(unit).Applied);
        Seed(connection, unit, 3);
        var executor = connection.DataMigrations;

        Assert.Equal(
            DataMigrationCapabilities.KeysetScan | DataMigrationCapabilities.AppliedLedger,
            executor.Capabilities);
        var refusal = Assert.Throws<DataMigrationRefusedException>(() => DataMigrationRunner.Run(
            executor, MongoDataMigrationExecutor.TargetFor(unit), unit,
            new DataMigration(MigrationId, unit.Id, new SlugTransform()), null, Now));

        Assert.Equal("GW-MIGRATION-001", refusal.Code);
        Assert.Equal(
            "GW-MIGRATION-001: this provider does not advertise data-migration capability " +
            "AtomicChunkProgress; it cannot move data under the facility's interruption guarantees.",
            refusal.Message);
        Assert.Equal(new string?[] { null, null, null }, Slugs(connection, unit));
        Assert.Empty(executor.ReadLedgerEntries(MongoDataMigrationExecutor.TargetFor(unit)));
    }

    // ------------------------------------------------------------------ fixtures

    private static string ConnectionOrSkip()
    {
        var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_MONGO_CONNECTION");
        Skip.If(string.IsNullOrWhiteSpace(connectionString),
            "Set GROUNDWORK_MONGO_CONNECTION to run MongoDB integration tests.");
        var url = new MongoUrlBuilder(connectionString!) { DatabaseName = "gwmigration_" + Guid.NewGuid().ToString("N") };
        return url.ToMongoUrl().ToString();
    }

    private static MongoClientContext OpenContextOrSkip() => new(ConnectionOrSkip());

    private static StorageUnit Unit()
    {
        var name = "migration_" + Guid.NewGuid().ToString("N");
        return new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, MaxLength = 32, IsNullable = false },
                new ColumnDefinition { Name = "name", Type = PortableType.String, MaxLength = 32 },
                new ColumnDefinition { Name = "slug", Type = PortableType.String, MaxLength = 64 }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
    }

    private static void Seed(MongoDbProviderConnection connection, StorageUnit unit, int rows)
    {
        var session = connection.OpenSession(unit, MongoStorageAccess.Global);
        for (var index = 1; index <= rows; index++)
        {
            session.Insert(new MongoStorageValues(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = "id" + index,
                ["name"] = ((char)('a' + index - 1)).ToString(),
                ["slug"] = null
            }));
        }
    }

    private static string?[] Slugs(MongoDbProviderConnection connection, StorageUnit unit) =>
        connection.Database.GetCollection<BsonDocument>(unit.Name)
            .Find(new BsonDocument())
            .Sort(new BsonDocument("_id", 1))
            .ToList()
            .Select(document => document["slug"].IsBsonNull ? null : document["slug"].AsString)
            .ToArray();

    private sealed class SlugTransform : IDataMigrationTransform
    {
        public string Identity => "slug/v1";
        public ImmutableArray<string> SourceColumns => ["name"];
        public ImmutableArray<string> TargetColumns => ["slug"];
        public DataMigrationValues Transform(DataMigrationRow row) =>
            DataMigrationValues.Set(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["slug"] = $"{row["name"]}/{row["id"]}"
            });
    }

    private sealed class FailingSlugTransform(string failOnId) : IDataMigrationTransform
    {
        public string Identity => "slug/v1";
        public ImmutableArray<string> SourceColumns => ["name"];
        public ImmutableArray<string> TargetColumns => ["slug"];
        public DataMigrationValues Transform(DataMigrationRow row)
        {
            if ((string?)row["id"] == failOnId)
                throw new InvalidOperationException($"transform refused row {failOnId}");
            return DataMigrationValues.Set(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["slug"] = $"{row["name"]}/{row["id"]}"
            });
        }
    }
}
