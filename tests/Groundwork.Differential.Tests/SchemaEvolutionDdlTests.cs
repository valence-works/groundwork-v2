using Groundwork.PostgreSql;
using Groundwork.Sqlite;
using Groundwork.SqlServer;
using Groundwork.Substrate.Relational;
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

    [Fact]
    public void SqlServer_drops_the_default_constraint_bound_to_a_column_before_dropping_it() =>
        // SQL Server auto-names the constraint behind a column default and then refuses to drop the
        // column while it exists, so the statement has to find it by name first.
        Assert.Contains(
            "sys.default_constraints",
            new SqlServerDialect().DropColumnSql("orders", "legacy_total"),
            StringComparison.Ordinal);

    [Fact]
    public void SqlServer_renames_through_sp_rename_rather_than_an_alter_clause()
    {
        var dialect = new SqlServerDialect();

        Assert.StartsWith("EXEC sp_rename", dialect.RenameTableSql("orders", "purchase_orders"), StringComparison.Ordinal);
        Assert.Contains("N'COLUMN'", dialect.RenameColumnSql("orders", "customer", "buyer"), StringComparison.Ordinal);
    }
}
