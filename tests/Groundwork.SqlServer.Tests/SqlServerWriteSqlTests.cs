using Groundwork.Kernel;
using Groundwork.SqlServer;
using Xunit;

namespace Groundwork.SqlServer.Tests;

public sealed class SqlServerWriteSqlTests
{
    [Fact]
    public void None_upsert_renders_one_merge_that_updates_existing_rows()
    {
        var unit = new StorageUnit
        {
            Id = new StorageUnitId("none-upsert"),
            Name = "none_upsert",
            Columns =
            [
                new ColumnDefinition { Name = "id", Type = PortableType.String, IsNullable = false, MaxLength = 200 },
                new ColumnDefinition { Name = "value", Type = PortableType.String, IsNullable = false, MaxLength = 200 },
                new ColumnDefinition { Name = "createdAt", Type = PortableType.DateTimeOffset, IsNullable = false }
            ],
            Key = new KeyDefinition { Columns = ["id"] },
            Concurrency = ConcurrencyDeclaration.None
        };

        var sql = SqlServerStorageSession.RenderNoneUpsertSql(unit, unit.Columns);

        Assert.StartsWith("MERGE ", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WITH (HOLDLOCK)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WHEN MATCHED THEN UPDATE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target.[value]=source.[value]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("target.[createdAt]=source.[createdAt]", sql, StringComparison.Ordinal);
        Assert.Contains("WHEN NOT MATCHED BY TARGET THEN INSERT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, sql.Count(character => character == ';'));
    }
}
