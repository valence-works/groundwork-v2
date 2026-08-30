using System.Collections.Immutable;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.SqlServer;
using Groundwork.Substrate.Relational;
using Groundwork.LiveDatabases;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Groundwork.SqlServer.Tests;

/// <summary>
/// Live SQL Server proofs. The relational implementation is shared, so these settle what only this
/// engine decides: its <c>OFFSET…FETCH</c> chunk limit, its <c>MERGE</c> ledger upsert, and a
/// <c>CASE</c> assignment whose arms include a null.
/// </summary>
// Shares the database LiveSqlServer claims with the provider, schema-tool and parity suites, so it
// belongs in their collection. Outside it, xUnit gives this class a default collection of its own
// and schedules it alongside the one whose fixture resets the catalog wholesale.
[Collection(SqlServerLiveDatabase.Name)]
public sealed class SqlServerDataMigrationTests
{
    private const string MigrationId = "2026-08-normalize";
    private static readonly DateTimeOffset Now = new(2026, 8, 27, 13, 0, 0, TimeSpan.Zero);

    [SkippableFact]
    public void A_budgeted_migration_chunks_resumes_and_replays()
    {
        var connectionString = LiveSqlServer.Required();
        var unit = Unit();
        var target = Target(unit);
        var executor = new RelationalSchemaExecutor(() => new SqlConnection(connectionString), new SqlServerDialect());
        try
        {
            Assert.Equal(PhysicalSchemaApplicationOutcome.Applied,
                PhysicalSchemaApplication.Apply(Target(unit, semanticMigrationId: null), executor, Now).Outcome);
            Seed(connectionString, unit, 5);

            var first = PhysicalSchemaApplication.Apply(
                target, executor, Now, null, Catalog(unit),
                new DataMigrationBudget { MaxRowsPerBatch = 2, MaxBatches = 1 });

            Assert.Equal(PhysicalSchemaApplicationOutcome.DataMigrationIncomplete, first.Outcome);
            Assert.Equal("2:i2;", Assert.Single(first.DataMigrations).ResumeCursor);
            // Row 3 is deliberately null-sourced, so the chunk that covers it renders a CASE arm
            // with a null literal beside parameterized arms.
            Assert.Equal(new string?[] { "a", "b", null, null, null }, Labels(connectionString, unit));

            var second = PhysicalSchemaApplication.Apply(
                target, executor, Now, null, Catalog(unit), new DataMigrationBudget { MaxRowsPerBatch = 2 });

            Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges, second.Outcome);
            Assert.Equal(DataMigrationStatus.Completed, Assert.Single(second.DataMigrations).Status);
            Assert.Equal(new string?[] { "a", "b", null, "d", "e" }, Labels(connectionString, unit));
            var completed = executor.ReadLedgerEntry(target.Identity, MigrationId)!;
            Assert.Equal(DataMigrationRunState.Completed, completed.State);
            Assert.Equal(5, completed.RowsScanned);

            var replay = PhysicalSchemaApplication.Apply(target, executor, Now, null, Catalog(unit));
            Assert.Equal(DataMigrationStatus.Replayed, Assert.Single(replay.DataMigrations).Status);
        }
        finally
        {
            Drop(connectionString, unit.Name);
        }
    }

    [SkippableFact]
    public void The_provider_advertises_every_capability_the_facility_requires()
    {
        var connectionString = LiveSqlServer.Required();
        var executor = new RelationalSchemaExecutor(() => new SqlConnection(connectionString), new SqlServerDialect());

        Assert.Equal(
            DataMigrationCapabilities.KeysetScan |
            DataMigrationCapabilities.AtomicChunkProgress |
            DataMigrationCapabilities.AppliedLedger |
            DataMigrationCapabilities.SetBasedBatchUpdate,
            executor.Capabilities);
    }

    [Fact]
    public void The_chunk_limit_uses_the_engines_offset_fetch_spelling()
    {
        // SQL Server has no LIMIT; the shared scan asks the dialect for the spelling it accepts.
        Assert.Equal(" OFFSET 0 ROWS FETCH NEXT 512 ROWS ONLY", new SqlServerDialect().LimitClause(512));
        Assert.Equal(2_098, new SqlServerDialect().ParameterBudget);
        Assert.Equal(1_049, RelationalRowMigration.AdmittedRows(new SqlServerDialect(), 1, 1, 5_000));
    }

    // ------------------------------------------------------------------ fixtures

    private static StorageUnit Unit()
    {
        var name = "migration_" + Guid.NewGuid().ToString("N");
        return new StorageUnit
        {
            Id = new StorageUnitId(name),
            Name = name,
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
                new ColumnDefinition { Name = "source", Type = PortableType.String, MaxLength = 32 },
                new ColumnDefinition { Name = "label", Type = PortableType.String, MaxLength = 32 }
            ],
            Key = new KeyDefinition { Columns = ["id"] }
        };
    }

    private static PhysicalSchemaTarget Target(StorageUnit unit, string? semanticMigrationId = MigrationId)
    {
        var physical = SqlServerSchemaCoordinator.Physicalize(unit);
        var basis = SqlServerSchemaCoordinator.Target(physical);
        if (semanticMigrationId is null)
            return basis;
        return new PhysicalSchemaTarget(
            new SchemaSubject(physical, new SchemaEvolutionMetadata(semanticMigrationId: semanticMigrationId)),
            basis.Provider,
            basis.ProviderDefinitions);
    }

    private static DataMigrationCatalog Catalog(StorageUnit unit) =>
        new([new DataMigration(MigrationId, unit.Id, new LabelTransform())]);

    private static void Seed(string connectionString, StorageUnit unit, int rows)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        for (var index = 1; index <= rows; index++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = $"INSERT INTO [{unit.Name}] ([id],[source],[label]) VALUES (@id,@source,NULL);";
            insert.Parameters.AddWithValue("@id", index);
            insert.Parameters.AddWithValue("@source",
                index == 3 ? DBNull.Value : ((char)('a' + index - 1)).ToString());
            insert.ExecuteNonQuery();
        }
    }

    private static string?[] Labels(string connectionString, StorageUnit unit)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT [label] FROM [{unit.Name}] ORDER BY [id];";
        using var reader = command.ExecuteReader();
        var labels = new List<string?>();
        while (reader.Read())
            labels.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        return labels.ToArray();
    }

    private static void Drop(string connectionString, string table)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"IF OBJECT_ID(N'[{table}]', N'U') IS NOT NULL DROP TABLE [{table}];";
        command.ExecuteNonQuery();
    }

    private sealed class LabelTransform : IDataMigrationTransform
    {
        public string Identity => "label/v1";
        public ImmutableArray<string> SourceColumns => ["source"];
        public ImmutableArray<string> TargetColumns => ["label"];
        public DataMigrationValues Transform(DataMigrationRow row) =>
            DataMigrationValues.Set(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["label"] = row["source"]
            });
    }
}
