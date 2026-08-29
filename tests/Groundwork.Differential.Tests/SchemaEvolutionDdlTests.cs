using Groundwork.Kernel;
using Groundwork.Kernel.Schema;
using Groundwork.LiveDatabases;
using Groundwork.PostgreSql;
using Groundwork.Sqlite;
using Groundwork.SqlServer;
using Groundwork.Substrate.Relational;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Groundwork.Differential.Tests;

/// <summary>
/// Every evolution operation has to render on every relational dialect, including the one this
/// machine cannot reach. These assertions are on the emitted statements rather than on a live
/// catalog, so a dialect that has no rendering for a new operation kind fails here rather than in
/// whichever environment happens to run it first.
/// </summary>
public sealed class SchemaEvolutionDdlTests
{
    public static TheoryData<string> Dialects => new("sqlite", "postgresql", "sqlserver");

    private static RelationalDialect Create(string provider) => provider switch
    {
        "sqlite" => new SqliteDialect(),
        "postgresql" => new PostgreSqlDialect(),
        _ => new SqlServerDialect()
    };

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Every_dialect_renders_the_removal_and_rename_vocabulary(string provider)
    {
        var dialect = Create(provider);

        foreach (var sql in new[]
                 {
                     dialect.DropColumnSql("orders", "legacy_total"),
                     dialect.DropTableSql("orders"),
                     dialect.RenameTableSql("orders", "purchase_orders"),
                     dialect.RenameColumnSql("orders", "customer", "buyer")
                 })
        {
            Assert.False(string.IsNullOrWhiteSpace(sql));
        }

        Assert.Contains("legacy_total", dialect.DropColumnSql("orders", "legacy_total"), StringComparison.Ordinal);
        Assert.Contains("DROP COLUMN", dialect.DropColumnSql("orders", "legacy_total"), StringComparison.Ordinal);
        Assert.Contains("DROP TABLE", dialect.DropTableSql("orders"), StringComparison.Ordinal);
        Assert.Contains("purchase_orders", dialect.RenameTableSql("orders", "purchase_orders"), StringComparison.Ordinal);
        Assert.Contains("buyer", dialect.RenameColumnSql("orders", "customer", "buyer"), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Every_dialect_tolerates_dropping_an_index_that_is_already_gone(string provider) =>
        // A rename moves indexes by dropping them under the old storage name; the drop has to be
        // safe for an index a previous partial apply already removed.
        Assert.Contains("IF EXISTS", Create(provider).DropIndexSql("orders", "by_customer"), StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Every_dialect_renders_foreign_key_and_check_constraints(string provider)
    {
        var dialect = Create(provider);
        var reference = new ReferenceDefinition
        {
            Name = "fk_orders_customer",
            Columns = ["customer_id"],
            TargetUnitId = new StorageUnitId("customer"),
            TargetScope = ScopePolicy.Global,
            Enforcement = ReferenceEnforcement.Physical,
            TargetName = "customers",
            TargetKeyColumns = ["id"],
            TargetKeyHasProviderSequence = false
        };
        var column = new ColumnDefinition { Name = "quantity", Type = PortableType.Int32, IsNullable = false };
        var check = new CheckConstraintDefinition
        {
            Name = "ck_orders_quantity",
            Column = "quantity",
            Operator = CheckConstraintOperator.GreaterThan,
            Value = new PortableDefault(0)
        };
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("order"),
            Name = "orders",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.Guid, IsNullable = false },
                new ColumnDefinition { Name = "customer_id", Type = PortableType.Guid, IsNullable = false },
                column
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            References = [reference],
            CheckConstraints = [check]
        };

        var foreignKey = dialect.ForeignKeyDefinitionSql(reference);
        var checkSql = dialect.CheckConstraintDefinitionSql(check, column);
        var create = RelationalSql.CreateTable(dialect, unit);

        Assert.Contains("FOREIGN KEY", foreignKey, StringComparison.Ordinal);
        Assert.Contains("REFERENCES", foreignKey, StringComparison.Ordinal);
        Assert.Contains("CHECK", checkSql, StringComparison.Ordinal);
        Assert.Contains("fk_orders_customer", create, StringComparison.Ordinal);
        Assert.Contains("ck_orders_quantity", create, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlServer_drops_the_default_constraint_bound_to_a_column_before_dropping_it() =>
        // SQL Server auto-names the constraint behind a column default and then refuses to drop the
        // column while it exists, so the statement has to find it by name first.
        Assert.Contains(
            "sys.default_constraints",
            new SqlServerDialect().DropColumnSql("orders", "legacy_total"),
            StringComparison.Ordinal);

    /// <summary>
    /// Asserting the text of a statement proves it says the right thing, not that SQL Server can
    /// parse it. Those came apart once already: <c>QUOTENAME</c> inside <c>EXEC(...)</c> reads
    /// correctly and does not parse, because that form concatenates only literals and variables.
    /// SET PARSEONLY makes the server check syntax without executing or binding names, so this
    /// creates nothing and touches no data.
    /// </summary>
    [SkippableFact]
    public void Every_sql_server_statement_this_dialect_emits_parses_on_the_server()
    {
        var connectionString = LiveSqlServer.Required();
        var dialect = new SqlServerDialect();
        var column = new ColumnDefinition
        {
            Name = "legacy_total",
            Type = PortableType.Decimal,
            Precision = 18,
            Scale = 4
        };
        var index = new IndexDefinition { Name = "by_customer", Columns = [new IndexColumn("customer")] };
        var statements = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["DropColumnSql"] = dialect.DropColumnSql("orders", "legacy_total"),
            ["DropTableSql"] = dialect.DropTableSql("orders"),
            ["RenameTableSql"] = dialect.RenameTableSql("orders", "purchase_orders"),
            ["RenameColumnSql"] = dialect.RenameColumnSql("orders", "customer", "buyer"),
            ["DropIndexSql"] = dialect.DropIndexSql("orders", "by_customer"),
            ["CreateIndexSql"] = dialect.CreateIndexSql("orders", index, dialect.IndexFilter(index)),
            ["FinalizeColumnSql"] = dialect.FinalizeColumnSql("orders", column.Name, column)
        };

        using var connection = new SqlConnection(connectionString);
        connection.Open();
        var refused = new List<string>();
        foreach (var (name, sql) in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SET PARSEONLY ON; " + sql + " SET PARSEONLY OFF;";
            try
            {
                command.ExecuteNonQuery();
            }
            catch (SqlException exception) when (IsSyntaxError(exception))
            {
                refused.Add($"{name}: {exception.Message}");
            }
            catch (SqlException)
            {
                // The statement parsed. SET PARSEONLY still resolves object names, so a complaint
                // that "orders" does not exist means the syntax was accepted and binding failed —
                // which is the expected outcome against a database these fixtures never create.
            }
        }

        Assert.Empty(refused);
    }

    /// <summary>
    /// SQL Server reports a syntax error under its own error numbers, distinct from the ones it uses
    /// for an object that cannot be resolved. 102 is the "Incorrect syntax near" that QUOTENAME
    /// inside EXEC produced.
    /// </summary>
    private static bool IsSyntaxError(SqlException exception) =>
        exception.Errors.Cast<SqlError>().Any(error => error.Number is 102 or 103 or 105 or 155 or 156 or 170 or 178);

    [Fact]
    public void SqlServer_renames_through_sp_rename_rather_than_an_alter_clause()
    {
        var dialect = new SqlServerDialect();

        Assert.StartsWith("EXEC sp_rename", dialect.RenameTableSql("orders", "purchase_orders"), StringComparison.Ordinal);
        Assert.Contains("N'COLUMN'", dialect.RenameColumnSql("orders", "customer", "buyer"), StringComparison.Ordinal);
    }
}
