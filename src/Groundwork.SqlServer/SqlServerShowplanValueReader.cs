using System.Data.SqlTypes;

namespace Groundwork.SqlServer;

internal static class SqlServerShowplanValueReader
{
    internal const string ColumnName = "Microsoft SQL Server 2005 XML Showplan";

    internal static string? Read(string columnName, object? value)
    {
        if (!string.Equals(columnName, ColumnName, StringComparison.OrdinalIgnoreCase))
            return null;

        var content = value switch
        {
            SqlXml xml when !xml.IsNull => xml.Value,
            SqlString text when !text.IsNull => text.Value,
            string text => text,
            _ => null
        };

        return string.IsNullOrWhiteSpace(content) ? null : content;
    }
}
