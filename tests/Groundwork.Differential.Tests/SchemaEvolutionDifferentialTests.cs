using System.Data.Common;
using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.PostgreSql;
using Groundwork.Sqlite;
using Groundwork.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;
using Xunit;

namespace Groundwork.Differential.Tests;

/// <summary>
/// One authorized evolution — rename primary storage, rename a column, widen a column, drop a
/// column, drop an index — run against every relational provider through the same public schema
/// machinery the deployment tool uses. Each case asserts on the rows themselves, because the point
/// of a rename is that the data is still there afterwards.
///
/// These share one live SQL Server and one live MongoDB with every other differential class, and
/// provider infrastructure DDL is created on first use rather than per test. Running alongside the
/// other live-provider classes therefore races that creation, so this class joins the collection
/// that already serializes them.
/// </summary>
[Collection(NativeProviderDifferentialCollection.Name)]
public sealed class SchemaEvolutionDifferentialTests
{
    [Fact]
    public void Sqlite_carries_rows_through_an_authorized_rename_alter_and_drop() =>
        RunEvolution(SqliteProvider());

    [SkippableFact]
    public void PostgreSql_carries_rows_through_an_authorized_rename_alter_and_drop() =>
        RunEvolution(PostgreSqlProvider());

    [SkippableFact]
    public void SqlServer_carries_rows_through_an_authorized_rename_alter_and_drop() =>
        RunEvolution(SqlServerProvider());

    [Fact]
    public void Sqlite_retires_primary_storage_under_authorization() =>
        RunRetirement(SqliteProvider());

    [SkippableFact]
    public void PostgreSql_retires_primary_storage_under_authorization() =>
        RunRetirement(PostgreSqlProvider());

    [SkippableFact]
    public void SqlServer_retires_primary_storage_under_authorization() =>
        RunRetirement(SqlServerProvider());

    private static void RunEvolution(EvolutionProvider provider)
    {
        using var store = provider.Open();
        var initial = Orders(store.Table, store.Table, includeLegacyTotal: true);
        Apply(store, initial);
        store.Execute(
            $"INSERT INTO {store.Quote(store.Table)} ({store.Quote("id")}, {store.Quote("customer")}, " +
            $"{store.Quote("total")}, {store.Quote("legacy_total")}) VALUES ('o-1', 'ada', 10, 7);");

        // Rename the storage and one column, widen another, and drop the legacy one — all at once.
        var renamedTable = store.Table + "_v2";
        var evolved = Orders(store.Table, renamedTable, includeLegacyTotal: false) with
        {
            Columns =
            [
                new() { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
                new() { Name = "buyer", Id = "customer", Type = PortableType.String, MaxLength = 200, IsNullable = false },
                new() { Name = "code", Type = PortableType.String, MaxLength = 32, Collation = PortableCollation.OrdinalIgnoreCase },
                new() { Name = "total", Type = PortableType.Decimal, Precision = 18, Scale = 4 }
            ],
            // by_total survives the storage rename. Every relational dialect Groundwork ships
            // derives its physical index name from the storage name, so an index that does not move
            // with its table stops being addressable by its declaration.
            Indexes = [new IndexDefinition { Name = "by_total", Columns = [new IndexColumn("total")] }]
        };

        var plan = Plan(store, evolved);
        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        Assert.Single(plan.Operations.OfType<RenamePrimaryStorageOperation>());
        var renamedColumn = Assert.Single(plan.Operations.OfType<RenameColumnOperation>());
        Assert.Equal("customer", renamedColumn.FromName);
        Assert.Equal("buyer", renamedColumn.ToName);
        Assert.Equal(ColumnAlterationKind.Widening, Assert.Single(plan.Operations.OfType<AlterColumnOperation>()).Alteration);
        Assert.Equal("legacy_total", Assert.Single(plan.Operations.OfType<DropColumnOperation>()).Column.Name);
        Assert.Equal("by_customer", Assert.Single(plan.Operations.OfType<DropPhysicalIndexOperation>()).Index.Name);
        // Unauthorized, the same plan refuses rather than dropping anything.
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.AuthorizationRequired,
            Apply(store, evolved, authorize: false).Outcome);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, Apply(store, evolved).Outcome);

        // The row survived the rename, under its new storage name and its new column name.
        Assert.Equal(
            "ada",
            store.Scalar($"SELECT {store.Quote("buyer")} FROM {store.Quote(renamedTable)} WHERE {store.Quote("id")}='o-1';"));
        Assert.Equal(
            10m,
            Convert.ToDecimal(store.Scalar(
                $"SELECT {store.Quote("total")} FROM {store.Quote(renamedTable)} WHERE {store.Quote("id")}='o-1';")));

        // The provider definitions moved with the storage instead of being left behind under the
        // old name. A stale row or type per rename is exactly the residue this must not leave.
        Assert.Equal(0L, Convert.ToInt64(store.Scalar(
            $"SELECT count(*) FROM {store.Quote("__groundwork_search_key_algorithms")} " +
            $"WHERE {store.Quote("table_name")}='{store.Table}';")));
        Assert.NotEqual(0L, Convert.ToInt64(store.Scalar(
            $"SELECT count(*) FROM {store.Quote("__groundwork_search_key_algorithms")} " +
            $"WHERE {store.Quote("table_name")}='{renamedTable}';")));

        // Replanning the same declaration finds nothing left to do, and the ledger has shrunk.
        Assert.Empty(Plan(store, evolved).Operations);
        var inspection = Inspect(store, evolved);
        Assert.True(inspection.IsAppliedSchemaValid);
        Assert.False(inspection.HasColumnDrift);
        // The surviving index moved with its storage instead of being stranded under the old name.
        Assert.False(inspection.HasIndexDrift, string.Join("; ", inspection.IndexDrift.Select(refusal => refusal.Message)));
        var applied = inspection.History.AppliedState!;
        Assert.DoesNotContain(applied.Snapshot.SemanticOperations,
            operation => operation.SubjectIdentity is "legacy_total" or "customer" or "by_customer");
        Assert.Contains(applied.Snapshot.SemanticOperations, operation => operation.SubjectIdentity == "buyer");
    }

    private static void RunRetirement(EvolutionProvider provider)
    {
        using var store = provider.Open();
        Apply(store, Orders(store.Table, store.Table, includeLegacyTotal: false));
        Assert.True(store.TableExists(store.Table));

        var retired = Orders(store.Table, store.Table, includeLegacyTotal: false);
        var plan = Plan(store, retired, retires: true);
        Assert.True(plan.IsApplicable, string.Join("; ", plan.Refusals.Select(refusal => refusal.Message)));
        Assert.Single(plan.Operations.OfType<DropPrimaryStorageOperation>());
        Assert.Equal(
            PhysicalSchemaApplicationOutcome.AuthorizationRequired,
            Apply(store, retired, authorize: false, retires: true).Outcome);

        Assert.Equal(PhysicalSchemaApplicationOutcome.Applied, Apply(store, retired, retires: true).Outcome);

        Assert.False(store.TableExists(store.Table));
        Assert.Empty(Plan(store, retired, retires: true).Operations);
    }

    /// <summary>
    /// The logical id is per store, not the constant "orders". Schema history is keyed on
    /// (logical id, provider), and every SQL Server case in this suite shares one database — so a
    /// constant id makes two tests with different physical tables claim one history row, and the
    /// second one legitimately plans a rename away from a table the first already dropped. The
    /// physical name still varies independently, which is what the rename case needs.
    /// </summary>
    private static StorageUnit Orders(string id, string table, bool includeLegacyTotal) => new()
    {
        Id = new StorageUnitId(id),
        Name = table,
        Columns =
        [
            new() { Name = "id", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            new() { Name = "customer", Type = PortableType.String, MaxLength = 64, IsNullable = false },
            // Folded, so every relational provider records a search-key provider definition whose
            // identity embeds the storage name. Without one, only SQL Server (which always emits a
            // batch type) exercises provider definitions through a rename.
            new() { Name = "code", Type = PortableType.String, MaxLength = 32, Collation = PortableCollation.OrdinalIgnoreCase },
            new() { Name = "total", Type = PortableType.Decimal, Precision = 18, Scale = 4 },
            ..(includeLegacyTotal
                ? new[] { new ColumnDefinition { Name = "legacy_total", Type = PortableType.Decimal, Precision = 18, Scale = 4 } }
                : [])
        ],
        Key = new KeyDefinition { Columns = ["id"] },
        Indexes = includeLegacyTotal
            ?
            [
                new IndexDefinition { Name = "by_customer", Columns = [new IndexColumn("customer")] },
                new IndexDefinition { Name = "by_total", Columns = [new IndexColumn("total")] }
            ]
            : []
    };

    private static PhysicalSchemaTarget Target(EvolutionStore store, StorageUnit unit, bool retires)
    {
        var target = store.Session.Targets.Compile(unit);
        return retires
            ? new PhysicalSchemaTarget(
                new SchemaSubject(target.Subject.Definition, new SchemaEvolutionMetadata(retiresPrimaryStorage: true)),
                target.Provider,
                target.ProviderDefinitions)
            : target;
    }

    private static PhysicalSchemaDiffPlan Plan(EvolutionStore store, StorageUnit unit, bool retires = false)
    {
        var target = Target(store, unit, retires);
        return PhysicalSchemaDiffPlanner.Plan(target, Inspect(store, unit, retires).History, DateTimeOffset.UnixEpoch);
    }

    private static PhysicalSchemaInspectionResult Inspect(EvolutionStore store, StorageUnit unit, bool retires = false) =>
        store.Session.Inspector.InspectHistory(Target(store, unit, retires));

    private static PhysicalSchemaApplicationResult Apply(
        EvolutionStore store,
        StorageUnit unit,
        bool authorize = true,
        bool retires = false)
    {
        var target = Target(store, unit, retires);
        return PhysicalSchemaApplication.Apply(
            target,
            store.Session.Executor,
            planAuthorization: plan =>
            {
                var protection = PhysicalSchemaPlanProtection.Inspect(plan.Operations);
                if (protection.IsSafe)
                    return PhysicalSchemaPlanAuthorization.Allow;
                if (authorize)
                    return PhysicalSchemaPlanAuthorization.Allow;
                return PhysicalSchemaPlanAuthorization.Deny(protection.DestructiveOperations
                    .Select(operation => new SchemaRefusal(
                        "GW-CLI-008",
                        $"Destructive operation '{operation.Address ?? operation.Identity}' requires explicit authorization.",
                        "authorization.destructive")));
            });
    }

    private static EvolutionProvider SqliteProvider() => new(
        () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "gw_evo_" + Guid.NewGuid().ToString("N") + ".db");
            var connectionString = "Data Source=" + path;
            var session = new SqliteSchemaToolProviderSessionFactory().Open(
                new SchemaToolProviderOptions("sqlite", connectionString, null, AllowCreate: true, CancellationToken.None));
            return new EvolutionStore(
                session,
                "gw_evo_" + Guid.NewGuid().ToString("N")[..12],
                () =>
                {
                    // This assertion connection is not the provider's, so it has to register the
                    // ordinal collation the provider declares its string columns with.
                    var connection = new SqliteConnection(connectionString);
                    connection.Open();
                    connection.CreateCollation(
                        "GROUNDWORK_UTF16_ORDINAL",
                        static (left, right) => string.CompareOrdinal(left, right));
                    return connection;
                },
                identifier => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
                () =>
                {
                    session.Dispose();
                    File.Delete(path);
                });
        });

    private static EvolutionProvider PostgreSqlProvider() => new(
        () =>
        {
            var baseConnection = Environment.GetEnvironmentVariable("GROUNDWORK_POSTGRES_CONNECTION");
            Skip.If(string.IsNullOrWhiteSpace(baseConnection),
                "Set GROUNDWORK_POSTGRES_CONNECTION to run PostgreSQL evolution tests.");
            var schema = "gw_evo_" + Guid.NewGuid().ToString("N");
            using (var admin = new NpgsqlConnection(baseConnection))
            {
                admin.Open();
                using var create = admin.CreateCommand();
                create.CommandText = $"CREATE SCHEMA \"{schema}\";";
                create.ExecuteNonQuery();
            }
            var connectionString = new NpgsqlConnectionStringBuilder(baseConnection) { SearchPath = schema }.ConnectionString;
            var session = new PostgreSqlSchemaToolProviderSessionFactory().Open(
                new SchemaToolProviderOptions("postgresql", connectionString, null, AllowCreate: true, CancellationToken.None));
            return new EvolutionStore(
                session,
                "gw_evo_" + Guid.NewGuid().ToString("N")[..12],
                () => new NpgsqlConnection(connectionString),
                identifier => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"",
                () =>
                {
                    session.Dispose();
                    using (var pooled = new NpgsqlConnection(connectionString))
                        NpgsqlConnection.ClearPool(pooled);
                    using var admin = new NpgsqlConnection(baseConnection);
                    admin.Open();
                    using var drop = admin.CreateCommand();
                    drop.CommandText = $"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE;";
                    drop.ExecuteNonQuery();
                });
        });

    private static EvolutionProvider SqlServerProvider() => new(
        () =>
        {
            var connectionString = Environment.GetEnvironmentVariable("GROUNDWORK_SQLSERVER_CONNECTION");
            Skip.If(string.IsNullOrWhiteSpace(connectionString),
                "Set GROUNDWORK_SQLSERVER_CONNECTION to run SQL Server evolution tests.");
            var table = "gw_evo_" + Guid.NewGuid().ToString("N")[..12];
            var session = new SqlServerSchemaToolProviderSessionFactory().Open(
                new SchemaToolProviderOptions("sqlserver", connectionString, null, AllowCreate: true, CancellationToken.None));
            return new EvolutionStore(
                session,
                table,
                () => new SqlConnection(connectionString),
                identifier => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]",
                () =>
                {
                    session.Dispose();
                    using var connection = new SqlConnection(connectionString);
                    connection.Open();
                    foreach (var name in new[] { table, table + "_v2" })
                    {
                        using var drop = connection.CreateCommand();
                        drop.CommandText = $"DROP TABLE IF EXISTS [{name}];";
                        drop.ExecuteNonQuery();
                    }
                });
        });

    private sealed record EvolutionProvider(Func<EvolutionStore> Open);

    private sealed class EvolutionStore(
        ISchemaToolProviderSession session,
        string table,
        Func<DbConnection> connect,
        Func<string, string> quote,
        Action release) : IDisposable
    {
        public ISchemaToolProviderSession Session { get; } = session;

        public string Table { get; } = table;

        public string Quote(string identifier) => quote(identifier);

        public void Execute(string sql)
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public object? Scalar(string sql)
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = command.ExecuteScalar();
            return value == DBNull.Value ? null : value;
        }

        public bool TableExists(string name)
        {
            using var connection = Connect();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT 1 FROM {quote(name)} WHERE 1=0;";
            try
            {
                command.ExecuteNonQuery();
                return true;
            }
            catch (DbException)
            {
                return false;
            }
        }

        public void Dispose() => release();

        private DbConnection Connect()
        {
            var connection = connect();
            if (connection.State != System.Data.ConnectionState.Open)
                connection.Open();
            return connection;
        }
    }
}
