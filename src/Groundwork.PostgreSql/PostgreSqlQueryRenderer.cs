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

    protected override string RenderOrderTerm(OrderTerm term)
    {
        if (term.Column.Type != QueryType.String)
            return base.RenderOrderTerm(term);

        var expression = RenderColumn(term.Column);
        var key = RenderOrdinalKey(expression);
        var direction = term.Direction == OrderDirection.Ascending ? "ASC" : "DESC";
        var nullRank = term.NullOrder == NullOrder.First ? "0" : "1";
        var nonNullRank = term.NullOrder == NullOrder.First ? "1" : "0";
        return "CASE WHEN " + expression + " IS NULL THEN " + nullRank + " ELSE " + nonNullRank + " END ASC, " + key + " " + direction;
    }

    protected override string RenderRange(
        Predicate.Range range,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        if (range.Column.Type != QueryType.String)
            return base.RenderRange(range, parameters, ref parameterIndex);

        var expression = RenderColumn(range.Column);
        var key = RenderOrdinalKey(expression);
        var parts = new List<string> { expression + " IS NOT NULL" };
        if (range.Lower is { } lower)
        {
            var name = AddParameter(range.Column, lower.Value, parameters, ref parameterIndex);
            parts.Add(key + (lower.IsInclusive ? " >= " : " > ") + RenderOrdinalKey("@" + name));
        }
        if (range.Upper is { } upper)
        {
            var name = AddParameter(range.Column, upper.Value, parameters, ref parameterIndex);
            parts.Add(key + (upper.IsInclusive ? " <= " : " < ") + RenderOrdinalKey("@" + name));
        }
        return "(" + string.Join(" AND ", parts) + ")";
    }

    protected override string RenderCursorEquality(
        ColumnRef column,
        QueryConstant value,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        if (column.Type != QueryType.String)
            return base.RenderCursorEquality(column, value, parameters, ref parameterIndex);
        var expression = RenderColumn(column);
        if (value.Kind == QueryConstantKind.Null)
            return expression + " IS NULL";
        var name = AddParameter(column, value, parameters, ref parameterIndex);
        return "(" + expression + " IS NOT NULL AND " + RenderOrdinalKey(expression) + " = " + RenderOrdinalKey("@" + name) + ")";
    }

    protected override string RenderAfter(
        OrderTerm term,
        QueryConstant value,
        ICollection<QueryRenderParameter> parameters,
        ref int parameterIndex)
    {
        if (term.Column.Type != QueryType.String)
            return base.RenderAfter(term, value, parameters, ref parameterIndex);
        var expression = RenderColumn(term.Column);
        if (value.Kind == QueryConstantKind.Null)
            return term.NullOrder == NullOrder.First ? expression + " IS NOT NULL" : "1 = 0";
        var name = AddParameter(term.Column, value, parameters, ref parameterIndex);
        var comparison = term.Direction == OrderDirection.Ascending ? ">" : "<";
        var strict = "(" + expression + " IS NOT NULL AND " + RenderOrdinalKey(expression) + " " + comparison + " " + RenderOrdinalKey("@" + name) + ")";
        return term.NullOrder == NullOrder.First
            ? strict
            : "(" + strict + " OR " + expression + " IS NULL)";
    }

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
            return elementOf.Quantifier == SetQuantifier.Any
                ? "FALSE"
                : "jsonb_typeof(" + expression + ") = 'array'";
        var clauses = new List<string>();
        foreach (var value in elementOf.Values)
        {
            if (value.Kind == QueryConstantKind.Null)
                clauses.Add("element.value = 'null'::jsonb");
            else
                clauses.Add("element.value = to_jsonb(@" + AddElementParameter(type, value.Value, parameters, ref parameterIndex) + ")");
        }
        var exists = elementOf.Quantifier == SetQuantifier.Any
            ? "EXISTS (SELECT 1 FROM jsonb_array_elements(" + expression + ") AS element(value) WHERE " + string.Join(" OR ", clauses) + ")"
            : string.Join(" AND ", clauses.Select(clause => "EXISTS (SELECT 1 FROM jsonb_array_elements(" + expression + ") AS element(value) WHERE " + clause + ")"));
        return "CASE WHEN jsonb_typeof(" + expression + ") = 'array' THEN (" + exists + ") ELSE FALSE END";
    }

    private static string RenderOrdinalKey(string expression) =>
        "COALESCE((SELECT string_agg(CASE WHEN ascii(chars.ch) <= 65535 THEN lpad(to_hex(ascii(chars.ch)), 4, '0') ELSE " +
        "lpad(to_hex(55296 + ((ascii(chars.ch) - 65536) >> 10)), 4, '0') || " +
        "lpad(to_hex(56320 + ((ascii(chars.ch) - 65536) & 1023)), 4, '0') END, '' ORDER BY chars.ord) " +
        "FROM unnest(string_to_array(" + expression + ", NULL)) WITH ORDINALITY AS chars(ch, ord)), '')";
}
