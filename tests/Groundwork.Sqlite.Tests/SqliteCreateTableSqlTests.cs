using Xunit;

namespace Groundwork.Sqlite.Tests;

public sealed class SqliteCreateTableSqlTests
{
    [Theory]
    [InlineData("TEXT CHECK (value COLLATE NOCASE <> '')", "BINARY")]
    [InlineData("TEXT CHECK (value COLLATE NOCASE <> '') COLLATE GROUNDWORK_UTF16_ORDINAL NOT NULL", "GROUNDWORK_UTF16_ORDINAL")]
    public void ExtractCollation_uses_only_the_column_level_clause(string declaration, string expected)
    {
        Assert.Equal(expected, SqliteCreateTableSql.ExtractCollation(declaration));
    }
}
