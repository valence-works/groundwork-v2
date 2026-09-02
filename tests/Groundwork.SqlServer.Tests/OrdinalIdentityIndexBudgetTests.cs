using Groundwork.Kernel;
using Groundwork.SqlServer;
using Xunit;

namespace Groundwork.SqlServer.Tests;

public sealed class OrdinalIdentityIndexBudgetTests
{
    [Fact]
    public void Included_logical_source_does_not_consume_the_ordinal_identity_key_budget()
    {
        var logical = StorageUnit.Declare("ordinal-budget", "ordinal_budget")
            .Int32("id", column => column.Required())
            .String("name", 212, column => column.Required().OrdinalIdentity("__groundwork_ordinal_name"))
            .Key("id")
            .Index("by_name", index => index.UseOrdinalIdentities().Column("name"))
            .Build();

        var physical = SqlServerSchemaCoordinator.Physicalize(logical);
        var index = Assert.Single(physical.Indexes);

        Assert.Equal(["__groundwork_ordinal_name"], index.Columns.Select(column => column.Column));
        Assert.Equal(["name"], index.IncludedColumns);
        SqlServerIndexKeyBudgetValidator.Validate(physical);
    }

    [Fact]
    public void Native_include_columns_are_emitted_outside_the_sql_server_key()
    {
        var sql = new SqlServerDialect().CreateIndexSql("people", new IndexDefinition
        {
            Name = "by_name",
            Columns = [new IndexColumn("__groundwork_ordinal_name")],
            IncludedColumns = ["name"]
        }, null);

        Assert.Contains("([__groundwork_ordinal_name] ASC) INCLUDE ([name]);", sql, StringComparison.Ordinal);
    }
}
