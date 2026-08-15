using Groundwork.Substrate.Relational;
using Groundwork.Query.Model;

namespace Groundwork.PostgreSql;

/// <summary>PostgreSQL's one native renderer for the normalized v2 query contract.</summary>
public sealed class PostgreSqlQueryRenderer : RelationalQueryRenderer
{
    public PostgreSqlQueryRenderer()
        : base(new PostgreSqlDialect(), parameterBudget: 65_535, supportsIndexHints: false)
    {
    }

    protected override string ProviderName => "PostgreSQL";

    protected override object? AdaptParameter(QueryType type, object? value) => type == QueryType.DateTimeOffset && value is DateTimeOffset timestamp
        ? timestamp.ToUniversalTime().Ticks
        : value;

    protected override string RenderColumn(ColumnRef column) => column.Type == QueryType.String
        ? "(" + base.RenderColumn(column) + " COLLATE \"C\")"
        : base.RenderColumn(column);

    protected override string RenderContains(string expression, string parameter) =>
        "(length(@" + parameter + ") = 0 OR POSITION(@" + parameter + " IN " + expression + ") > 0)";

    protected override string RenderEndsWith(string expression, string parameter) =>
        "(length(@" + parameter + ") = 0 OR RIGHT(" + expression + ", length(@" + parameter + ")) = @" + parameter + ")";

    protected override string RenderElementOf(
        Predicate.ElementOf elementOf,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        if (elementOf.Set.Type is not QueryType type)
            throw new QueryRenderException("GW-SEM-TYPE-007", "An element set must declare its exact element type before rendering.");
        var expression = Dialect.QuoteIdentifier(elementOf.Set.Name);
        if (elementOf.Values.Length == 0)
            return elementOf.Quantifier == SetQuantifier.Any ? "FALSE" : "TRUE";
        var clauses = new List<string>();
        foreach (var value in elementOf.Values)
        {
            if (value.Kind == QueryConstantKind.Null)
                clauses.Add("array_position(" + expression + ", NULL) IS NOT NULL");
            else
                clauses.Add("EXISTS (SELECT 1 FROM unnest(" + expression + ") AS element WHERE element = @" + AddElementParameter(type, value.Value, parameters, ref parameterIndex) + ")");
        }
        return elementOf.Quantifier == SetQuantifier.Any
            ? "(" + string.Join(" OR ", clauses) + ")"
            : "(" + string.Join(" AND ", clauses) + ")";
    }
}
