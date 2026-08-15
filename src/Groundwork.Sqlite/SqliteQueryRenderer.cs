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

    protected override string RenderContains(string expression, string parameter) =>
        "(length(@" + parameter + ") = 0 OR instr(" + expression + ", @" + parameter + ") > 0)";

    protected override string RenderEndsWith(string expression, string parameter) =>
        "(length(@" + parameter + ") = 0 OR substr(" + expression + ", -length(@" + parameter + ")) = @" + parameter + ")";

    protected override string RenderElementOf(
        Predicate.ElementOf elementOf,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        if (elementOf.Set.Type is not QueryType type)
            throw new QueryRenderException("GW-SEM-TYPE-007", "An element set must declare its exact element type before rendering.");
        var expression = Dialect.QuoteIdentifier(elementOf.Set.Name);
        if (elementOf.Values.Length == 0)
            return elementOf.Quantifier == SetQuantifier.Any ? "1 = 0" : "1 = 1";
        var clauses = new List<string>();
        foreach (var value in elementOf.Values)
        {
            if (value.Kind == QueryConstantKind.Null)
                clauses.Add("json_each.value IS NULL");
            else
                clauses.Add("json_each.value = @" + AddElementParameter(type, value.Value, parameters, ref parameterIndex));
        }
        return elementOf.Quantifier == SetQuantifier.Any
            ? "EXISTS (SELECT 1 FROM json_each(" + expression + ") WHERE " + string.Join(" OR ", clauses) + ")"
            : string.Join(" AND ", clauses.Select(clause => "EXISTS (SELECT 1 FROM json_each(" + expression + ") WHERE " + clause + ")"));
    }
}
