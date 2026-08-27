using System.Collections.Immutable;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.Sqlite;
using Groundwork.Substrate.Relational;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Groundwork.Sqlite.Tests;

/// <summary>
/// The expand–contract workflow against a live catalog, so what the plan claims about dual presence
/// is checked against what the database actually holds rather than argued about.
/// </summary>
public sealed class SqliteExpandContractTests
{
    private const string MigrationId = "2026-08-widen-total";
    private static readonly DateTimeOffset T0 = new(2026, 8, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    [Fact]
    public void A_widening_rename_expands_beside_its_old_column_and_contracts_only_once_the_gate_opens()
    {
        using var store = TemporaryStore.Create();
        var executor = Executor(store);
        var before = Target(Before(), evolution: null);
        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied,
            PhysicalSchemaApplication.Apply(before, executor, T0).Outcome);
        Seed(store);

        // ---- expand: the new column arrives, the old one keeps every value it held.
        var superseding = Target(After(), Superseding());
        var expand = PhysicalSchemaApplication.Apply(
            superseding, executor, T0.AddHours(1), dataMigrations: Catalog());

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, expand.Outcome);
        Assert.Equal(new[] { "id", "name", "total", "__groundwork_action", "total_amount" }, Columns(store));
        Assert.Equal(new decimal?[] { 1.25m, 2.50m, 3.75m }, Values(store, "total"));
        // The transform ran under the same authorization that admitted the schema change, so the
        // replacement column is populated by the time the expand reports Applied.
        Assert.Equal(new decimal?[] { 1.25m, 2.50m, 3.75m }, Values(store, "total_amount"));
        Assert.True(executor.ReadLedgerEntry(superseding.Identity, MigrationId)!.IsComplete);

        // ---- the window has not elapsed: the contract refuses and touches nothing. The window is
        // measured from the instants the provider actually acknowledged, not from the plan clock,
        // so these two attempts are timed against the real one.
        var expandedAt = DateTimeOffset.UtcNow;
        var early = PhysicalSchemaApplication.Apply(
            superseding, executor, expandedAt, phase: SchemaEvolutionPhase.Contract);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Rejected, early.Outcome);
        Assert.Equal("GW-EXPAND-003", Assert.Single(early.Plan.Refusals).Code);
        Assert.Equal(new[] { "id", "name", "total", "__groundwork_action", "total_amount" }, Columns(store));

        // ---- past the window: the contract removes the superseded column and nothing else.
        var contract = PhysicalSchemaApplication.Apply(
            superseding, executor, expandedAt + Window + TimeSpan.FromHours(1),
            phase: SchemaEvolutionPhase.Contract);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, contract.Outcome);
        Assert.Equal(new[] { "id", "name", "__groundwork_action", "total_amount" }, Columns(store));
        Assert.Equal(new decimal?[] { 1.25m, 2.50m, 3.75m }, Values(store, "total_amount"));

        // ---- and it is terminal: replanning either phase has nothing left to do.
        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges,
            PhysicalSchemaApplication.Apply(superseding, executor, expandedAt.AddDays(3)).Outcome);
        Assert.Equal(PhysicalSchemaApplicationOutcome.NoChanges,
            PhysicalSchemaApplication.Apply(
                superseding, executor, expandedAt.AddDays(3), phase: SchemaEvolutionPhase.Contract).Outcome);
        Assert.Equal(new[] { "id", "name", "__groundwork_action", "total_amount" }, Columns(store));
    }

    [Fact]
    public void A_contract_without_a_recorded_backfill_refuses_and_leaves_the_column_in_place()
    {
        using var store = TemporaryStore.Create();
        var executor = Executor(store);
        PhysicalSchemaApplication.Apply(Target(Before(), evolution: null), executor, T0);
        Seed(store);
        var superseding = Target(After(), Superseding());
        // The expand runs without a transform catalog, so the replacement column exists and is empty
        // and the data-migration ledger records nothing at all.
        PhysicalSchemaApplication.Apply(superseding, executor, T0.AddHours(1));

        var contract = PhysicalSchemaApplication.Apply(
            superseding, executor, DateTimeOffset.UtcNow.AddDays(30), phase: SchemaEvolutionPhase.Contract);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Rejected, contract.Outcome);
        Assert.Equal("GW-EXPAND-002", Assert.Single(contract.Plan.Refusals).Code);
        Assert.Equal(new[] { "id", "name", "total", "__groundwork_action", "total_amount" }, Columns(store));
        Assert.Equal(new decimal?[] { 1.25m, 2.50m, 3.75m }, Values(store, "total"));
        Assert.Equal(new decimal?[] { null, null, null }, Values(store, "total_amount"));
    }

    // ------------------------------------------------------------------ fixtures

    private static RelationalSchemaExecutor Executor(TemporaryStore store) =>
        new(() => Connect(store), new SqliteDialect());

    private static SqliteConnection Connect(TemporaryStore store)
    {
        var connection = new SqliteConnection(store.ConnectionString);
        connection.Open();
        connection.CreateCollation("GROUNDWORK_UTF16_ORDINAL", static (left, right) => string.CompareOrdinal(left, right));
        return connection;
    }

    private static StorageUnit Before() => new()
    {
        Id = new StorageUnitId("orders"),
        Name = "orders",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
            new ColumnDefinition { Name = "name", Type = PortableType.String, MaxLength = 64 },
            new ColumnDefinition { Name = "total", Type = PortableType.Decimal, Precision = 10, Scale = 2 }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static StorageUnit After() => new()
    {
        Id = new StorageUnitId("orders"),
        Name = "orders",
        Columns =
        [
            new ColumnDefinition { Name = "id", Type = PortableType.Int32, IsNullable = false },
            new ColumnDefinition { Name = "name", Type = PortableType.String, MaxLength = 64 },
            new ColumnDefinition { Name = "total_amount", Type = PortableType.Decimal, Precision = 18, Scale = 2 }
        ],
        Key = new KeyDefinition { Columns = ["id"] }
    };

    private static SchemaEvolutionMetadata Superseding() => new(
        semanticMigrationId: MigrationId,
        supersessions: [new ColumnSupersession(
            new ColumnDefinition { Name = "total", Type = PortableType.Decimal, Precision = 10, Scale = 2 },
            "total_amount")],
        dualPresenceWindow: Window);

    private static PhysicalSchemaTarget Target(StorageUnit unit, SchemaEvolutionMetadata? evolution)
    {
        var physical = SqliteSchemaCoordinator.Physicalize(unit);
        var basis = SqliteSchemaCoordinator.Target(physical);
        return new PhysicalSchemaTarget(
            new SchemaSubject(physical, evolution), basis.Provider, basis.ProviderDefinitions);
    }

    private static DataMigrationCatalog Catalog() =>
        new([new DataMigration(MigrationId, new StorageUnitId("orders"), new CopyTotalTransform())]);

    private static void Seed(TemporaryStore store)
    {
        using var connection = Connect(store);
        var totals = new[] { 1.25m, 2.50m, 3.75m };
        for (var index = 0; index < totals.Length; index++)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO \"orders\" (\"id\",\"name\",\"total\") VALUES (@id,@name,@total);";
            insert.Parameters.AddWithValue("@id", index + 1);
            insert.Parameters.AddWithValue("@name", ((char)('a' + index)).ToString());
            insert.Parameters.AddWithValue("@total", totals[index]);
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// What the catalog actually holds, asked of SQLite rather than inferred from the plan. The
    /// provider-owned <c>__groundwork_action</c> column is part of that answer and is asserted
    /// rather than filtered, so the expectation is the whole physical shape.
    /// </summary>
    private static string[] Columns(TemporaryStore store)
    {
        using var connection = Connect(store);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"name\" FROM pragma_table_info('orders') ORDER BY \"cid\";";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
            names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static decimal?[] Values(TemporaryStore store, string column)
    {
        using var connection = Connect(store);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"{column}\" FROM \"orders\" ORDER BY \"id\";";
        using var reader = command.ExecuteReader();
        var values = new List<decimal?>();
        while (reader.Read())
            values.Add(reader.IsDBNull(0) ? null : reader.GetDecimal(0));
        return values.ToArray();
    }

    private sealed class CopyTotalTransform : IDataMigrationTransform
    {
        public string Identity => "copy-total/v1";
        public ImmutableArray<string> SourceColumns => ["total"];
        public ImmutableArray<string> TargetColumns => ["total_amount"];
        public DataMigrationValues Transform(DataMigrationRow row) =>
            DataMigrationValues.Set(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["total_amount"] = row["total"]
            });
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
            var path = Path.Combine(Path.GetTempPath(), "groundwork-sqlite-expand-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TemporaryStore(path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // A leaked temporary directory is not worth failing a test over.
            }
        }
    }
}
