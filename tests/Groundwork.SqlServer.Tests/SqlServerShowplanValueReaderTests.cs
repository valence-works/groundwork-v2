using System.Data.SqlTypes;
using System.Xml;
using Xunit;

namespace Groundwork.SqlServer.Tests;

public sealed class SqlServerShowplanValueReaderTests
{
    [Fact]
    public void Exact_showplan_column_accepts_text_value()
    {
        const string plan = "<ShowPlanXML />";

        Assert.Equal(plan, SqlServerShowplanValueReader.Read(
            SqlServerShowplanValueReader.ColumnName,
            plan));
    }

    [Fact]
    public void Exact_showplan_column_accepts_provider_sql_values()
    {
        var sqlString = new SqlString("<ShowPlanXML sql-string=\"true\" />");
        using var xmlReader = XmlReader.Create(new StringReader("<ShowPlanXML sql-xml=\"true\" />"));
        var sqlXml = new SqlXml(xmlReader);

        Assert.Equal(sqlString.Value, SqlServerShowplanValueReader.Read(
            SqlServerShowplanValueReader.ColumnName,
            sqlString));
        Assert.True(SqlServerShowplanValueReader.Read(
                SqlServerShowplanValueReader.ColumnName,
                sqlXml)
            ?.Contains("sql-xml=\"true\"", StringComparison.Ordinal) == true);
    }

    [Theory]
    [InlineData("ordinary payload", "<ShowPlanXML />")]
    [InlineData(SqlServerShowplanValueReader.ColumnName + " ", "<ShowPlanXML />")]
    [InlineData(SqlServerShowplanValueReader.ColumnName, null)]
    public void Non_exact_or_null_values_are_rejected(string columnName, string? value)
    {
        Assert.Null(SqlServerShowplanValueReader.Read(columnName, value));
    }

    [Fact]
    public void Unsupported_value_type_is_rejected_even_for_exact_column()
    {
        Assert.Null(SqlServerShowplanValueReader.Read(
            SqlServerShowplanValueReader.ColumnName,
            new object()));
    }
}
