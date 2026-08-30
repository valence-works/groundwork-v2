using System.Collections.Immutable;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.LiveDatabases;
using Groundwork.Substrate.Relational;
using MySqlConnector;
using Xunit;

namespace Groundwork.MySql.Tests;

/// <summary>Live MySQL/MariaDB proofs for resumable, ledger-bound data migrations.</summary>
public sealed class MySqlDataMigrationTests
{
    private const string MigrationId = "2026-08-normalize";
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);

    [SkippableFact]
    public void A_budgeted_migration_chunks_resumes_and_replays()
    {
        using var database = LiveMySqlDatabase.OpenOrSkip();
        var target = Target();
        var executor = new RelationalSchemaExecutor(
            () => new MySqlConnection(database.ConnectionString),
            new MySqlDialect());
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.Applied,
            PhysicalSchemaApplication.Apply(Target(semanticMigrationId: null), executor, Now).Outcome);
        Seed(database, 5);

        var first = PhysicalSchemaApplication.Apply(
            target,
            executor,
            Now,
            dataMigrations: Catalog(),
            dataMigrationBudget: new DataMigrationBudget { MaxRowsPerBatch = 2, MaxBatches = 1 });

        Assert.Equal(PhysicalSchemaApplicationOutcome.DataMigrationIncomplete, first.Outcome);
        Assert.Equal("2:i2;", Assert.Single(first.DataMigrations).ResumeCursor);
        Assert.Equal(new string?[] { "a", "b", null, null, null }, Labels(database));

        var second = PhysicalSchemaApplication.Apply(
            target,
            executor,
            Now,
            dataMigrations: Catalog(),
            dataMigrationBudget: new DataMigrationBudget { MaxRowsPerBatch = 2 });

        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, second.Outcome);
        Assert.Equal(DataMigrationStatus.Completed, Assert.Single(second.DataMigrations).Status);
        Assert.Equal(new string?[] { "a", "b", "c", "d", "e" }, Labels(database));
        Assert.Equal(DataMigrationRunState.Completed, executor.ReadLedgerEntry(target.Identity, MigrationId)!.State);

        var replay = PhysicalSchemaApplication.Apply(target, executor, Now, dataMigrations: Catalog());
        Assert.Equal(DataMigrationStatus.Replayed, Assert.Single(replay.DataMigrations).Status);
    }

    [SkippableFact]
    public void The_provider_advertises_every_capability_the_facility_requires()
    {
        using var database = LiveMySqlDatabase.OpenOrSkip();
        var executor = new RelationalSchemaExecutor(
            () => new MySqlConnection(database.ConnectionString),
            new MySqlDialect());

        Assert.Equal(
            DataMigrationCapabilities.KeysetScan |
            DataMigrationCapabilities.AtomicChunkProgress |
            DataMigrationCapabilities.AppliedLedger |
            DataMigrationCapabilities.ExclusiveRunLease |
            DataMigrationCapabilities.SetBasedBatchUpdate,
            executor.Capabilities);
        DataMigrationRunner.EnsureCapabilities(executor);
    }

    private static StorageUnit Unit() => new()
    {
        Id = new StorageUnitId("mysql_migration"),
        Name = "mysql_migration",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
            new ColumnDefinition { Name = "source", Type = PortableType.String, MaxLength = 32 },
            new ColumnDefinition { Name = "label", Type = PortableType.String, MaxLength = 32 }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static PhysicalSchemaTarget Target(string? semanticMigrationId = MigrationId)
    {
        var physical = MySqlSchemaCoordinator.Physicalize(Unit());
        var basis = MySqlSchemaCoordinator.Target(physical);
        if (semanticMigrationId is null)
            return basis;
        return new PhysicalSchemaTarget(
            new SchemaSubject(physical, new SchemaEvolutionMetadata(semanticMigrationId: semanticMigrationId)),
            basis.Provider,
            basis.ProviderDefinitions);
    }

    private static DataMigrationCatalog Catalog() =>
        new([new DataMigration(MigrationId, Unit().Id, new LabelTransform())]);

    private static void Seed(LiveMySqlDatabase database, int rows)
    {
        using var connection = new MySqlConnection(database.ConnectionString);
        connection.Open();
        for (var index = 1; index <= rows; index++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO `mysql_migration` (`id`,`source`,`label`) VALUES (@id,@source,NULL);";
            insert.Parameters.AddWithValue("@id", index);
            insert.Parameters.AddWithValue("@source", ((char)('a' + index - 1)).ToString());
            insert.ExecuteNonQuery();
        }
    }

    private static string?[] Labels(LiveMySqlDatabase database)
    {
        using var connection = new MySqlConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT `label` FROM `mysql_migration` ORDER BY `id`;";
        using var reader = command.ExecuteReader();
        var labels = new List<string?>();
        while (reader.Read())
            labels.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        return labels.ToArray();
    }

    private sealed class LabelTransform : IDataMigrationTransform
    {
        public string Identity => "label";

        public string Version => "v1";

        public ImmutableArray<string> SourceColumns => ["source"];

        public ImmutableArray<string> TargetColumns => ["label"];

        public DataMigrationValues Transform(DataMigrationRow row) =>
            DataMigrationValues.Set(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["label"] = row["source"]
            });
    }
}
