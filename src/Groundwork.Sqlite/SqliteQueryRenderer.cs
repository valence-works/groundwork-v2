using Groundwork.Substrate.Relational;
using Groundwork.Query.Model;
using System.Globalization;

namespace Groundwork.Sqlite;

/// <summary>SQLite's one native renderer for the normalized v2 query contract.</summary>
public sealed class SqliteQueryRenderer : RelationalQueryRenderer
{
    public SqliteQueryRenderer()
        : base(new SqliteDialect(), parameterBudget: 999, supportsIndexHints: false)
    {
    }

    protected override string ProviderName => "SQLite";

    protected override string RenderColumn(ColumnRef column) => column.Type == QueryType.Decimal
        ? "CAST(" + Dialect.QuoteIdentifier(column.Name) + " AS NUMERIC)"
        : base.RenderColumn(column);

    protected override object? AdaptParameter(QueryType type, object? value) => type switch
    {
        QueryType.Boolean when value is bool boolean => boolean ? 1 : 0,
        QueryType.Guid when value is Guid guid => guid.ToString("D"),
        QueryType.DateTimeOffset when value is DateTimeOffset timestamp => timestamp.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        QueryType.Decimal when value is decimal decimalValue => decimalValue.ToString("G29", CultureInfo.InvariantCulture),
        _ => value
    };
}
