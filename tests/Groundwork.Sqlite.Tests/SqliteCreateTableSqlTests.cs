using Groundwork.Kernel;
using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteCreateTableSqlTests
{
    private readonly SqliteDialect dialect = new();

    [Theory]
    [InlineData("TEXT CHECK (value COLLATE NOCASE <> '')", "BINARY")]
    [InlineData("TEXT CHECK (value COLLATE NOCASE <> '') COLLATE GROUNDWORK_UTF16_ORDINAL NOT NULL", "GROUNDWORK_UTF16_ORDINAL")]
    public void ExtractCollation_uses_only_the_column_level_clause(string declaration, string expected)
    {
        Assert.Equal(expected, SqliteCreateTableSql.ExtractCollation(declaration));
    }

    [Fact]
    public void Json_default_is_rendered_as_a_serialized_json_literal()
    {
        var column = new ColumnDefinition
        {
            Name = "payload",
            Type = PortableType.Json,
            Default = new PortableDefault(new Dictionary<string, object?>
            {
                ["state"] = "pending",
                ["items"] = new List<object?> { true, 2 }
            })
        };

        Assert.Equal("'{\"state\":\"pending\",\"items\":[true,2]}'", dialect.MapDefault(column));
    }
}
